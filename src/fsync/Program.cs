using Corex.Helpers;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using WinSCP;

namespace fsync
{
    class Program
    {
        static int Main(string[] args)
        {
            try
            {
                System.Console.Title = "fsync";
                var p = new Program { Args = args.ToList() };
                System.Console.CancelKeyPress += (s, e) =>
                {
                    try
                    {
                        p.DisconnectSession();
                    }
                    catch { }
                };
                p.Run();
                System.Console.ReadLine();
                return 0;
            }
            catch (Exception e)
            {
                System.Console.WriteLine("Error: {0}", e);
                System.Console.ReadLine();
                return 1;
            }
        }

        FakeConsole Console;
        void ListDirRecursive(Session session, string path)
        {
            var x = session.ListDirectory(path);
            foreach (RemoteFileInfo file in x.Files)
            {
                System.Console.WriteLine(file.Name);
                if (file.IsDirectory && file.Name != "." && file.Name != "..")
                    ListDirRecursive(session, path + "/" + file.Name);
            }
        }
        ThreadSafe<Session> Session;
        private void Run()
        {
            if (Args.IsNotNullOrEmpty())
            {
                var filename = Args[0].Trim('\"');
                System.Console.Title = Path.GetFileName(filename);
                Config = LoadConfig(new FileInfo(filename));
            }
            Console = new FakeConsole { DoWriteLine = System.Console.WriteLine };

            ConnectSession();
            AutoRestart = true;
            FSyncs = new List<FSync>();
            for (var i = 1; i < 10; i++)
            {
                var fp = Config.TryGetValue("FolderPair"+i);
                if (fp == null)
                    continue;

                var fs = new FSync
                {
                    AutoConnect = fp.Get<bool>("AutoConnect"),
                    AutoStartRealTime = fp.Get<bool>("AutoStartRealTime"),
                    Session = Session,
                    Console = new FakeConsole { DoWriteLine = s => Console.WriteLine("#1: {0}", s) },
                    IncludeSubdirectories = fp.Get<bool>("IncludeSubdirectories"),
                    LocalDir = fp["LocalDir"],
                    BackupDir = fp["BackupDir"],
                    RemoteDir = fp["RemoteDir"],
                    FilesUploaded = FSync_FilesUploaded,
                };
                FSyncs.Add(fs);
            }
            FSyncs.ForEach(t => t.Init());
            StartMenu();
        }

        public bool AutoRestart { get; set; }
        private void FSync_FilesUploaded(List<FileSystemEventArgs> list)
        {
            return;
            //if (!AutoRestart)
            //    return;
            //if (list.Where(t => Path.GetExtension(t.Name).EqualsIgnoreCase(".pm")).FirstOrDefault() == null)
            //    return;
            //DoRestart();
        }
        List<FSync> FSyncs;

        Menu Menu;
        private void StartMenu()
        {
            var FSync1 = FSyncs[0];
            var mes = FSync1.GetType().GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.DeclaredOnly).Where(t => !t.Name.StartsWith("get_") && !t.Name.StartsWith("set_")).ToList();
            mes = mes.Concat(this.GetType().GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.DeclaredOnly).Where(t => !t.Name.StartsWith("get_") && !t.Name.StartsWith("set_"))).ToList();
            var meNames = mes.Select(t => t.Name).Distinct();
            var mis = meNames.Select(t => new MenuItem { Name = t, Action = () => DoAction(t) }).ToList();
            mis.Where(t => t.Name == "Connect").First().Action = Reconnect;
            Menu = new Menu { Items = mis };

            Menu.Start();
        }

        public void KillAllSessions()
        {
            Process.GetProcessesByName("winscp").ForEach(t => t.Kill());
        }
        private void DoAction(string name)
        {
            var mi2 = this.GetType().GetMethod(name);
            if (mi2 != null)
            {
                Do(mi2, this);
                return;
            }
            var mi = FSyncs[0].GetType().GetMethod(name);
            DoEach(mi, FSyncs);
        }

        private void Reconnect()
        {
            Session.Do(t =>
                {
                    if (t.Opened)
                        t.Dispose();
                });
            ConnectSession();
        }

        public void DoRestart()
        {
            //TODO:
        }

        void ConnectSession()
        {
            var ss = CreateSession();
            Session = ThreadSafe.Create(ss);
            if (FSyncs != null)
                FSyncs.ForEach(t => t.Session = Session);
        }

        void DisconnectSession()
        {
            if (Session != null)
                Session.Do(t => t.Abort());
        }


        Dictionary<string, Dictionary<string, string>> LoadConfig(FileInfo file)
        {
            var dic = new Dictionary<string, Dictionary<string, string>>();
            var category = "";
            file.Lines().ForEach(line =>
            {
                if (line.IsNullOrEmpty())
                    return;
                if (line.StartsWith("[") && line.EndsWith("]"))
                {
                    category = line.Substring(1, line.Length - 2);
                    dic.Add(category, new Dictionary<string, string>());
                }
                else
                {
                    var tokens = line.SplitAt(line.IndexOf("="), true);
                    dic[category].Add(tokens[0], tokens[1]);
                }
            });
            return dic;
        }

        Dictionary<string, Dictionary<string, string>> Config;

        private Session CreateSession()
        {
            var sessionConf = Config["Session"];
            var sessionOptions = new SessionOptions
            {
                Protocol = Protocol.Sftp,
                HostName = sessionConf["HostName"],
                UserName = sessionConf["UserName"],
                Password = sessionConf.TryGetValue("Password"),
                GiveUpSecurityAndAcceptAnySshHostKey = true,
                SshPrivateKeyPath = sessionConf.TryGetValue("SshPrivateKeyPath"),
                PrivateKeyPassphrase = sessionConf.TryGetValue("SshPrivateKeyPassphrase"),
            };

            var ss = new Session();
            ss.OutputDataReceived += (s, e) => Console.WriteLine(e.Data);
            ss.FileTransferred += session_FileTransferred;
            ss.Open(sessionOptions);
            return ss;
        }



        void Do(MethodInfo mi, object target)
        {
            try
            {
                Console.WriteLine(mi.Name);
                var res = mi.Invoke(target, null);
                Console.WriteLine(res);
            }
            catch (TargetInvocationException e)
            {
                throw e.InnerException;
            }
        }

        void DoEach<T>(MethodInfo mi, IEnumerable<T> targets)
        {
            foreach (var target in targets)
                Do(mi, target);
        }

        private void session_FileTransferred(object sender, TransferEventArgs e)
        {
            if (e.Error == null)
            {
                Console.WriteLine("{0} Upload succeeded", e.FileName);
            }
            else
            {
                Console.WriteLine("{0} Upload failed: {1}", e.FileName, e.Error);
            }

            if (e.Chmod != null)
            {
                if (e.Chmod.Error == null)
                {
                    Console.WriteLine("{0} Permisions set to {1}", e.Chmod.FileName, e.Chmod.FilePermissions);
                }
                else
                {
                    Console.WriteLine("{0} Setting permissions failed: {1}", e.Chmod.FileName, e.Chmod.Error);
                }
            }
            else
            {
                //Console.WriteLine("{0} Permissions kept with their defaults", e.Destination);
            }

            if (e.Touch != null)
            {
                if (e.Touch.Error == null)
                {
                    Console.WriteLine("{0} Timestamp set to {1}", e.Touch.FileName, e.Touch.LastWriteTime);
                }
                else
                {
                    Console.WriteLine("{0} Setting timestamp failed: {1}", e.Touch.FileName, e.Touch.Error);
                }
            }
            else
            {
                // This should never happen with Session.SynchronizeDirectories
                //Console.WriteLine("{0} Timestamp kept with its default (current time)", e.Destination);
            }
        }



        public List<string> Args { get; set; }
    }

}
