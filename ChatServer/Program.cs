﻿using ChatServer.Properties;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace ChatServer
{
    partial class Program
    {
        public static readonly string[] ReservedNames = new[] { "admin", "system", "server", "TODEA" };
        public static readonly string SERVER_INI_PATH = Environment.CurrentDirectory + @"\server.ini";

        public static List<string> InviteCodes = new List<string>(500);

        const ConsoleColor DefaultColor = ConsoleColor.DarkGray;
        const int VARIOUS_JOB_TIMER_TICK = 7500;
        static Server server;
        static bool WriteInLogFile;
        static bool firstEditOfSettings = true;

        static StreamWriter writer = new StreamWriter("logs.txt", true);

        public static void Write(string msg, string header = "", ConsoleColor color = DefaultColor)
        {
            string msg_ = (!string.IsNullOrWhiteSpace(header) ? (string.Concat(DateTime.Now.ToLongTimeString(), " >>> [", header, "]  ", msg)) : (string.Concat(">>>", "   ", msg)));
            var _col = Console.ForegroundColor;
            Console.ForegroundColor = color;
            Console.WriteLine(msg_);
            Console.ForegroundColor = _col;

            if (WriteInLogFile)
            {
                writer.WriteLine(msg_);
                writer.Flush();
            }
        }
        public static void Write(LogMessageType type, string msg, params object[] obj)
        {
            string head = GetEnum(type);
            msg = string.Format(msg, obj);
            switch (type)
            {
                case LogMessageType.Config:
                    Write(msg, head, ConsoleColor.Red);
                    break;

                case LogMessageType.Network:
                    Write(msg, head, ConsoleColor.Green);
                    break;

                case LogMessageType.Chat:
                    Write(msg, head, ConsoleColor.DarkCyan);
                    break;

                case LogMessageType.Auth:
                    Write(msg, head);
                    break;

                case LogMessageType.UserEvent:
                    Write(msg, head, ConsoleColor.DarkYellow);
                    break;

                case LogMessageType.Packet:
                    Write(msg, head);
                    break;

                case LogMessageType.ReportFromUser:
                    Write(msg, "REPORT", ConsoleColor.DarkRed);
                    break;

                default:
                    Write(msg);
                    break;
            }
        }

        [STAThread]
        static void Main(string[] args)
        {
            if (args.Length > 0)
            {
                if (args[0] == "-wait")
                    Thread.Sleep(int.Parse(args[1]));
            }
            try
            {
                var watch = Stopwatch.StartNew();
                Settings = Program.LoadSettings();
                LoadInvitesCodes(false);
                WriteInLogFile = Settings["writeInFile"];
                InitializeConsole();

                server = new Server(Settings["serverPort"], Settings["maxClients"], Parse<AuthMethod>(Settings["authMethod"]), Settings["passKey"]);
                server.listenThread.Start();
                watch.Stop();
                Program.Write("Loaded server in " + watch.Elapsed.TotalSeconds + " seconds", "Trace");

                VariousJobTimer.Elapsed += VariousJobTimer_Elapsed;
                VariousJobTimer.Start();

                //

                string line = "";
                while ((line = Console.ReadLine()) != "csn")
                {
                    try
                    {
                        if (line == "")
                        { continue; }
                        if (line == "clients")
                        {
                            int uol = 0;
                            foreach (var pair in server.Connections)
                            {
                                Console.WriteLine("{0}\t\t-\t\t{1}", pair.Key.ToString().PadRight(20, ' '), pair.Value.Username.PadLeft(10, ' '));
                                uol++;
                            }
                            Console.WriteLine("\n\t>>  {0} users online", uol);
                        }
                        else if (line.StartsWith("settings"))
                        {
                            if (line.Length == 8)
                            {
                                Console.WriteLine("\n>>");
                                foreach (var pair in Settings)
                                    Console.WriteLine("\t{0}-{1}", pair.Key.PadRight(20, ' '), pair.Value.ToString().PadLeft(20, ' '));
                            }
                            else
                            {
                                string spec = line.Substring("settings".Length + 1);
                                dynamic result;
                                if (Settings.TryGetValue(spec, out result))
                                {
                                    Console.WriteLine(">>  {0}\t-\t{1}", spec, result);
                                }
                                else
                                {
                                    Console.WriteLine("Not found... use \'e\' command for adding");
                                }
                            }
                        }
                        else if (line == "cls" || line == "clear")
                            Console.Clear();
                        else if (line.Length > 2)
                        {
                            if (line[1] == ' ')
                            {
                                if (line[0] == 'b')
                                    server.Broadcast(line.Substring(2));
                                else if (line[0] == 'a')
                                {
                                    int[] uids = new String(line.Substring(2).TakeWhile(c => c != ' ' && c != '^').ToArray()).Split(',').Select(s => int.Parse(s)).ToArray();
                                    server.AdminMessage(line.Substring(line.IndexOf('^') + 1), uids);
                                }
                                else if (line[0] == 'k')
                                {
                                    string[] params_ = line.Split(' ');

                                    Client client = server.Connections[params_[1].ToInt()];
                                    string endpoint = client.Socket.RemoteEndPoint.ToString().Split(':')[0];

                                    Kick(client, endpoint);
                                    RemoveBlacklist(endpoint, (params_.Length == 2) ? Settings["defaultBanTime"] : params_[2].ToInt());
                                }
                                else if (line[0] == 'e')
                                {
                                    string[] data = line.Substring(2).Split('=');
                                    lock (Settings)
                                    {
                                        if (Settings.ContainsKey(data[0]))
                                        {
                                            try
                                            {
                                                dynamic oldVal = Settings[data[0]];
                                                //if (oldVal.GetType() == data[1].GetType())
                                                Console.WriteLine(">>\tChanged: {0}\t=\t{1}", data[0], Settings[data[0]] = data[1]);
                                                //else
                                                //    throw new ArgumentException("Wrong data type for " + data[0]);
                                                if (firstEditOfSettings)
                                                {
                                                    Write("Changing settings is unsafe: no type check!", "Warning", ConsoleColor.Red);
                                                    firstEditOfSettings = false;
                                                }
                                            }
                                            catch (IndexOutOfRangeException)
                                            {
                                                Console.WriteLine("Wrong syntax for E (edit) command");
                                            }
                                        }
                                        else
                                        {
                                            Console.WriteLine("No such key... add in session memory?");
                                            if (Console.ReadLine() == "1")
                                            {
                                                lock (Settings)
                                                {
                                                    AddSmart(data[0], data[1]);
                                                    Console.WriteLine("Added {0} = {1}", data[0], data[1]);
                                                }
                                            }
                                            else
                                                Console.WriteLine("Dismissed");
                                        }
                                    }
                                }
                            }
                            if (line.StartsWith("udata"))
                            {
                                string input = line.Substring(6);

                                int uid; Client val;
                                if (int.TryParse(input, out uid)) // in: "UID", out: uid (int), OUTPUT: Username
                                {
                                    if (server.Connections.TryGetValue(uid, out val))
                                        Write("Username: " + val.Username, "Query");
                                    else
                                        Write("No client with UID: " + uid);
                                }
                                else
                                {
                                    val = server.Connections.ValuesWhere(c => c.Username == input).FirstOrDefault();
                                    if (val != null)
                                        Write("UID: " + val.UserID, "Query");
                                    else
                                        Write("No client with Username: " + input);
                                }
                            }
                            else if (line.StartsWith("restart"))
                            {
                                int delay = 0;
                                if (line.Length > 7)
                                {
                                    delay = int.Parse(line.Substring(8));
                                }
                                Write(String.Format("Restarting in {0}...", delay), "Application", ConsoleColor.Blue);
                                Thread.Sleep(delay);
                                Process.Start("ChatServer.exe", "-wait 1000");
                                return;
                            }
                            else if (line == "add ic")
                            {
                                Console.Write("Expecting invite code: ");
                                string code;
                                InviteCodes.Add(code = Console.ReadLine());
                                Write(LogMessageType.Config, "Added invite code " + code);
                            }
                            else if (line.StartsWith("pop ic"))
                            {
                                string code;
                                lock (InviteCodes)
                                {
                                    if (InviteCodes.Count != 0)
                                    {
                                        code = InviteCodes.First();
                                    }
                                    else
                                    {
                                        code = Helper.GenerateRandomString(6);
                                        InviteCodes.Add(code);
                                    }
                                }
                                Console.Write(code);

                                if ((line.Length > 7 && line.Substring(7) == "-nc"))
                                {
                                    Console.WriteLine();
                                }
                                else
                                {
                                    Thread.Sleep(100);
                                    System.Windows.Forms.Clipboard.SetText(code);
                                    Console.WriteLine(" -> copied");
                                }
                            }
                            else if (line == "openf")
                            {
                                Process.Start("explorer", Environment.CurrentDirectory);
                            }
                        }
                    }

                    catch (Exception e)
                    {
                        Console.WriteLine(e.Message);
                    }
                }
            }
            catch (Microsoft.CSharp.RuntimeBinder.RuntimeBinderException)
            {
                Console.WriteLine("Inexistent/corrupt server.ini file, create a default one? (1 = yes)");
                if (Console.ReadLine() == "1")
                {
                    File.Delete("server.ini");
                    using (var writer = File.CreateText(Environment.CurrentDirectory + @"\server.ini"))
                    {
                        writer.WriteLine(Resources.DefaultSettingsFileContents);
                        writer.Flush();
                    }
                    Console.WriteLine("Created new server.ini... restart application");
                    System.Threading.Thread.Sleep(1000);
                }
                return;
            }
            finally
            {
                server = null;
                writer.Dispose();
                VariousJobTimer.Dispose();
                Environment.Exit(0);
            }
        }

        public static void Kick(Client client, string endpoint)
        {
            server.Broadcast(String.Format("{0} [{1}] has been kicked from the server!", client.Username, client.UserID));
            server.Blacklist.Add(endpoint);
            client.Send("-1");

            Server.OnError(client);
        }

        private static void LoadInvitesCodes(bool force)
        {
            InviteCodes = new List<string>(Settings["maxClients"]);
            if (force == true || Parse<AuthMethod>(Settings["authMethod"]) == AuthMethod.InviteCode)
            {
                try
                {
                    using (StreamReader reader = new StreamReader("invites.txt"))
                    {
                        string line;
                        while (string.IsNullOrWhiteSpace(line = reader.ReadLine()) == false)
                        {
                            Program.InviteCodes.Add(line);
                        }
                    } 
                }
                catch (FileNotFoundException)
                {
                    Write(LogMessageType.Config, "Warning: no invite code file (create invites.txt)");
                    Write(LogMessageType.Config, "Create new file? <yes/no>");
                    string ans = Console.ReadLine().ToLower();
                    if (ans == "yes" || ans == "def")
                    {
                        string[] args = new string[5];
                        if (ans == "def")
                        {
                            args = new[] { "100", "5", "6", "no", "invites.txt" };
                        }
                        else
                        {
                            Console.Write("Number of codes: ");
                            args[0] = Console.ReadLine();
                            Console.Write("Grouping number: ");
                            args[1] = Console.ReadLine();
                            Console.Write("Chars/group: ");
                            args[2] = Console.ReadLine();
                            Console.Write("Open file after? <yes/no>:");
                            args[3] = Console.ReadLine();
                            args[4] = "invites.txt";
                        }
                        if (!File.Exists("icgen.exe"))
                        {
                            int len = Resources.icgen.Length;
                            using (var stream = File.Create("icgen.exe", len))
                            {
                                stream.Write(Resources.icgen, 0, len);
                            }
                        }
                        using (var generatorProcess = Process.Start("icgen.exe", String.Join(" ", args)))
                        {
                            generatorProcess.WaitForExit();
                            if (generatorProcess.ExitCode == -2)
                            {
                                Write(LogMessageType.Config, "Could not generate invite codes, reason unknown.");
                            }
                            else
                            {
                                switch (generatorProcess.ExitCode)
                                {
                                    case -1:
                                        Write(LogMessageType.Config, "Wrong format for parameters, retrying process"); break;
                                    case 0:
                                        Write(LogMessageType.Config, "{0} codes generated! Restarting the server...", args[0]); break;
                                    default:
                                        Write(
                                            "Unknown exit code for icgen.exe <" + generatorProcess.ExitCode + ">",
                                            "ERROR", ConsoleColor.DarkRed); break;
                                }

                                LoadInvitesCodes(force);
                            }
                        }
                    }
                }
            }
        }

        static void VariousJobTimer_Elapsed(object sender, System.Timers.ElapsedEventArgs e)
        {
            lock (Settings)
            {
                WriteInLogFile = Settings["writeInFile"];
                ConsoleColor temp = Parse<ConsoleColor>(Settings["consoleBackColor"]);
                if (Console.BackgroundColor != temp)
                {
                    Console.BackgroundColor = temp;
                    Console.Clear();
                }
                Server.AuthMethod = Parse<AuthMethod>(Settings["authMethod"]);
                Server.Password = Settings["passKey"];
                // todo add more maybe
            }
            lock (InviteCodes)
            {
                if (InviteCodes.Count != 0)
                    File.WriteAllLines("invites.txt", InviteCodes);
            }
        }

        static System.Timers.Timer VariousJobTimer = new System.Timers.Timer(VARIOUS_JOB_TIMER_TICK);


        public static async void RemoveBlacklist(string p, int delay)
        {
            await Task.Delay(delay * 1000);
            server.Blacklist.Remove(p);
            Write(LogMessageType.UserEvent, "Removed {0} from blacklist", p);
        }

        static void InitializeConsole()
        {
            Console.Title = "Chat Server";

            ConsoleColor bckCol = Console.BackgroundColor = Parse<ConsoleColor>(Settings["consoleBackColor"]);
            Console.ForegroundColor = bckCol == ConsoleColor.Black ? ConsoleColor.Cyan : ConsoleColor.DarkCyan;

            Console.SetWindowSize(105, 25);
            Console.SetBufferSize(105, 1500);

            Console.Clear();
            Program.Write(LogMessageType.Config, "Settings loaded");
        }


        static string GetEnum<T>(T _enum) where T : struct
        {
            return Enum.GetName(typeof(T), _enum);
        }

        static T Parse<T>(string from) where T : struct // == where T : Enum
        {
            T auth;
            if (Enum.TryParse<T>(from, true, out auth))
                return auth;
            else return default(T);
        }
    }
}