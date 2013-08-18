﻿using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Linq;
using System.Threading;

namespace ChatServer
{
    class Server
    {
        static AuthMethod AuthMethod { get; set; }
        static string Password { get; set; }

        readonly TcpListener _listener;
        readonly IPAddress ip = IPAddress.Any;
        readonly int port, maxConnections;
        readonly bool logPackets, acceptServers;
        public readonly Thread listenThread;
        public static readonly ASCIIEncoding enc = new ASCIIEncoding();

        public Dictionary<int, Client> Connections { get; set; }
        public ICollection<string> Blacklist { get; set; }

        public bool Listen { get; set; }

        public Server(int port, int maxConnections, AuthMethod auth, string password)
        {
            this.maxConnections = maxConnections;
            this.port = port;
            this._listener = new TcpListener(ip, port);
            this.Connections = new Dictionary<int, Client>(maxConnections);
            this.listenThread = new Thread(StartListening);
            this.logPackets = Program.Settings["logPackets"];
            this.acceptServers = Program.Settings["acceptServerSockets"];

            this.Blacklist = new List<string>();

            Password = password;
            AuthMethod = auth;
        }

        void StartListening()
        {
            try
            {
                _listener.Start();
                Listen = true;
                Program.Write(LogMessageType.Network, "Started listening");
            }
            catch { Program.Write("There may be a guy listening on this port, we can't start our listener", "Network", ConsoleColor.Magenta); }

            while (Listen)
            {
                try
                {
                    if (Connections.Count + 1 > maxConnections) 
                        continue;
                    Socket socket = _listener.AcceptSocket();
                    if (this.Blacklist.Contains(socket.RemoteEndPoint.ToString().Split(':')[0]))
                    {
                        Program.Write(LogMessageType.Auth, "Rejected blacklisted IP: {0}", socket.RemoteEndPoint.ToString());
                        socket.Send(BlacklistedPacket);
                        socket.Close();
                        continue;
                    }
                    var watch = Stopwatch.StartNew();
                    Program.Write(LogMessageType.Network, "Incoming connection");
                    ConnectionFlags flag = ConnectionFlags.None;

                    Client client = OnClientConnected(socket, ref flag);
                    if (client != null && flag == ConnectionFlags.OK)
                        OnSuccessfulClientConnect(client);
                    else if ((flag == ConnectionFlags.BadPassword && AuthMethod == ChatServer.AuthMethod.Full) || flag == ConnectionFlags.BadFirstPacket)
                    {
                        try
                        {
                            socket.Send(AccessDeniedPacket);
                            socket.Shutdown(SocketShutdown.Both);
                            Program.Write(LogMessageType.Auth, "A client failed to connect");
                        }
                        finally
                        {
                            socket.Close();
                            socket = null;
                        }
                    }
                    else if (flag == ConnectionFlags.SocketError)
                    {
                        Program.Write("Socket error on connection", "Auth", ConsoleColor.Red);
                    }
                    watch.Stop();
                    Program.Write("Handled new connection in " + watch.Elapsed.TotalSeconds + " seconds", "Trace");
                }
                catch (SocketException e)
                {
                    Program.Write("Exception code: " + e.ErrorCode, "Socket Error", ConsoleColor.Red);
                    break;
                }
            }
        }

        void OnSuccessfulClientConnect(Client newClient)
        {
            newClient.Socket.Send(AccessGrantedPacket);
            Thread.Sleep(100);
            newClient.Send(CreatePacket(1, newClient.UserID, newClient.Username));

            lock (Connections.Values)
            {
                foreach (var otherGuy in Connections)
                    newClient.Send(CreatePacket(29, otherGuy.Key, otherGuy.Value.Username)); // tell the new guy about the others

                foreach (var otherGuy in Connections)
                    otherGuy.Value.Send(CreatePacket(31, newClient.UserID, newClient.Username)); // tell the other guys about the new guy
            }
                Connections.Add(newClient.UserID, newClient);

            Program.Write(LogMessageType.UserEvent, "{0} connected [{1}]", newClient.Username, newClient.UserID);
            (newClient.Thread = new Thread(() => HandleClient(newClient))).Start();
        }

        void HandleClient(Client client)
        {
            client.ConnectionState = ConnectionState.Online;
            byte[] buffer = new byte[1024];
            int bytesRead = 0;

            //SendServerInformationPacket(client);
            try
            {
                while (client.ConnectionState == ConnectionState.Online)
                {
                    if (!Receive(client.Socket, buffer, ref bytesRead))
                    {
                        OnError(client);
                        break;
                    }

                    using (var packet = new Packet(enc.GetString(buffer, 0, bytesRead), client, false))
                        HandlePacket(packet);
                }
            }
            finally
            {
                lock (Connections)
                    OnClientDisconnected(client);
            }
        }

        static void SendServerInformationPacket(Client cln)
        {
            string packet = CreatePacket(69, Program.Settings["svOwner"], Program.Settings["svName"]);
            cln.Send(packet);
            cln.Send(CreatePacket(3, Program.Settings["svWelcomeMsg"]));
        }
        static string CreatePacket(params object[] o) {
            return string.Join("|", o);
        }

        void OnClientDisconnected(Client client)
        {
            Connections.Remove(client.UserID);
            lock (Connections)
                foreach (var user in Connections.Values)
                    user.Send(CreatePacket(12, client.UserID ^ 0x50));
            Program.Write(string.Format("{0} disconnected [{1}]", client.Username, client.UserID), "Network", ConsoleColor.Gray);
        }

        void HandlePacket(Packet p)
        {
            p.Seek(+1);
            var sender = p.Sender;
            if (logPackets && p.Header != "32")
                Program.Write("Received packet of type " + GetHeaderType(p.Header) + ", from " + sender.Username + "[" + sender.UserID + "]", "PacketLogs", ConsoleColor.Blue);

            if (p.Header == "32") // msg
            {
                sender.MessagesSent++;
                string message = p.ReadString();
                foreach (var user in Connections.Values)
                    user.Send("34|{0}|{1}", sender.UserID ^ 0x121, message);
            }
            else if (p.Header == "35") // whisper
            {
                string targetName = p.ReadString();
                Client target = Connections.Values.FirstOrDefault(c => c.Username == targetName);
                if (target != null)
                {
                    string message = p.ReadString();
                    target.Send(CreatePacket(37, sender.UserID, message)); // 37 = received whisper
                    sender.Send(CreatePacket(38, target.UserID, message)); // 38 = sent whisper
                }
                else
                    sender.Send(CreatePacket(-38));
            }
        }

        public void Broadcast(string message)
        {
            var watch = Stopwatch.StartNew();
            lock (this.Connections)
            {
                foreach (var user in this.Connections.Values)
                {
                    user.Send(CreatePacket(3, message));
                }
                Program.Write("Message broadcasted to " + this.Connections.Count + " in " + watch.Elapsed.Milliseconds);
            }
            watch.Stop();
        }

        public void AdminMessage(string message, int[] ids)
        {
            for (int i = 0; i < ids.Length; i++)
                if (this.Connections.ContainsKey(ids[i]))
                    this.Connections[ids[i]].Send(CreatePacket(4, message));
        }

        public static String GetHeaderType(string header)
        {
            switch (header)
            {
                case "32":
                    return "POST_MESSAGE";
                case "1":
                    return "INIT_CLIENT";
                case "E":
                    return "GET_ROLE_of";
                case "34":
                    return "NOTIFY_POST";
                case "29":
                    return "ADD_PREV_USER";
                case "31":
                    return "ADD_NEW_USER";
                case "12":
                    return "CLIENT_DISCONNECTED";
                case "3":
                    return "BROADCAST";
                case "4":
                    return "SYSTEM_MESSAGE";
                case "69":
                    return "SERVER_INFO";
                case "37":
                    return "RECEIVE_WHISPER";
                case "35":
                    return "SEND_WHISPER";
                case "38":
                    return "SENT_WHISPER";
                case "-38":
                    return "WHISPER_ERROR";
                case "-1":
                    return "KICK";
                default:
                    return "Unknown";
            }
        }

        internal static void OnError(Client client)
        {
            client.Socket.Close();
            client.ConnectionState = ConnectionState.Offline;
        }

        static byte[] ServerSocketAdminRejected = new byte[] { 49, 64, 81, 100 };
        static byte[] ServerSocketAdminAccepted = new byte[] { 64, 81, 100, 121 };
        static byte[] NameRequiredPacket = new byte[] { 2, 8, 18, 32 };
        static byte[] ServerIsFullPacket = new byte[] { 2, 18, 8, 32 };
        static byte[] FullAuthRequiredPacket = new byte[] { 2, 32, 8, 18 };
        static byte[] AccessGrantedPacket = new byte[] { 2, 32, 18, 8 };
        static byte[] AccessDeniedPacket = new byte[] { 2, 18, 32, 8 };
        static byte[] BlacklistedPacket = new byte[] { 33, 6, 1 };
        static byte[] PingPacket = new byte[] { 4, 36 };

        private Client OnClientConnected(Socket newGuy, ref ConnectionFlags flag)
        {
            switch (AuthMethod)
            {
                case AuthMethod.UsernameOnly:
                    newGuy.Send(NameRequiredPacket);
                    break;
                case AuthMethod.Full:
                    newGuy.Send(FullAuthRequiredPacket);
                    break;
                case AuthMethod.InviteCode:
                default:
                    throw new NotImplementedException();
            }

            byte[] buffer = new byte[64];
            int bytesRead = 0;

            if (!Receive(newGuy, buffer, ref bytesRead))
                return OnConnectionError(ref flag);

            if (buffer[0] < 128)
            {
                if (buffer[0] == 0x45 && AuthMethod == ChatServer.AuthMethod.UsernameOnly) // UsernameOnly
                {
                    string username = Helper.XorText(enc.GetString(buffer, 1, bytesRead - 1), buffer[0]);
                    while (this.Connections.ValuesWhere(c => c.Username == username).Any() || Program.ReservedNames.Contains(username.ToLower()))
                        username += (char)Helper.Randomizer.Next((int)'a', (int)'z');

                    flag = ConnectionFlags.OK;
                    return new Client(username, newGuy);
                }
                else if (buffer[0] == 0x55 && AuthMethod == ChatServer.AuthMethod.Full)
                {
                    string[] data = enc.GetString(buffer, 1, bytesRead - 1).Split('|').Select(s => Helper.XorText(s, 0x55)).ToArray();
                    if (Server.Password == data[1])
                    {
                        flag = ConnectionFlags.OK;
                        return new Client(data[0], newGuy);
                    }
                    else
                    {
                        flag = ConnectionFlags.BadPassword;
                        return null;
                    }
                }
            }
            else
            {
                flag = ConnectionFlags.BadFirstPacket;
                return null;
            }

            return null;
        }

        static Client OnConnectionError(ref ConnectionFlags flag)
        {
            flag = ConnectionFlags.SocketError;
            return null;
        }

        static bool Receive(Socket socket, byte[] buffer, ref Int32 bytesRead)
        {
            try
            {
                bytesRead = socket.Receive(buffer, SocketFlags.None);
                if (bytesRead == 0) throw new SocketException();
                return true;
            }
            catch (SocketException)
            {
                return false;
            }
            catch (Exception e)
            {
                Console.WriteLine(e.Message);
                try { socket.Close(); } catch (SocketException) { }
                return false;
            }
        }

        /// <summary>
        /// Represents any possible situation for when a user logs in
        /// </summary>
        internal enum ConnectionFlags
        {
            OK,
            None,
            Banned,
            BadFirstPacket,
            SocketError,
            BadPassword
        }
    }
}