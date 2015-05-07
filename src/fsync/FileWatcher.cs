using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using WinSCP;

namespace fsync
{
    class FileWatcher
    {
        public FileWatcher()
        {
            Console = new FakeConsole();
        }
        public FakeConsole Console { get; set; }
        //public ThreadSafe<Session> Session { get; set; }
        public string LocalDir { get; set; }
        IdleDetector IdleDetector;
        public void StartRealtime()
        {
            if (Watcher != null)
                return;
            IdleDetector = new IdleDetector { Timeout = TimeSpan.FromMilliseconds(100), Idle = IdleDetector_Idle };
            IdleDetector.Start();
            Watcher = new FileSystemWatcher(LocalDir, Filter ?? "*");
            Watcher.NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName;// | NotifyFilters.DirectoryName;
            Watcher.IncludeSubdirectories = IncludeSubdirectories;
            Watcher.Created += Watcher_Created;
            Watcher.Changed += Watcher_Changed;
            Watcher.Deleted += Watcher_Deleted;
            Watcher.Renamed += Watcher_Renamed;
            Watcher.EnableRaisingEvents = true;
        }
        public string Filter { get; set; }
        public bool IncludeSubdirectories { get; set; }

        [MethodImpl(MethodImplOptions.Synchronized)]
        private void IdleDetector_Idle()
        {
            var list = WatcherEvents.ToList();
            WatcherEvents.Clear();
            var list2 = new List<FileSystemEventArgs>();
            foreach (var e in list)
            {
                if (e.ChangeType == WatcherChangeTypes.Changed)
                {
                    if (e.Name.Contains("~"))
                        continue;
                    //if (File.Exists(e.FullPath))
                    list2.Add(e);
                }
            }
            if (list2.Count > 0)
            {
                if (FilesChanged != null)
                    FilesChanged(list2);
                //list2.ForEach(OnFileChanged);
            }
        }
        public bool IsRealTimeRunning
        {
            get { return Watcher != null; }
        }

        [MethodImpl(MethodImplOptions.Synchronized)]
        public void StopRealtime()
        {
            if (Watcher != null)
            {
                Watcher.Dispose();
                Watcher = null;
            }
            if (IdleDetector != null)
            {
                IdleDetector.Stop();
                IdleDetector = null;
            }
        }

        List<FileSystemEventArgs> WatcherEvents = new List<FileSystemEventArgs>();
        [MethodImpl(MethodImplOptions.Synchronized)]
        void Watcher_Renamed(object sender, RenamedEventArgs e)
        {
            Console.WriteLine("Watcher_Renamed {0} {1} old:{2}", e.ChangeType, e.Name, e.OldName, e.FullPath);
            var deletedFile = WatcherEvents.Where(t => t.ChangeType == WatcherChangeTypes.Deleted && t.Name == e.Name).FirstOrDefault();
            if (deletedFile != null)
            {
                Console.WriteLine("Detected delete, treating as file change");
                WatcherEvents.Remove(deletedFile);
                var e2 = new FileSystemEventArgs(WatcherChangeTypes.Changed, e.FullPath.Replace(e.Name, ""), e.Name);
                WatcherEvents.Add(e2);
                return;
            }
            WatcherEvents.Add(e);
            IdleDetector.Ping();
        }
        [MethodImpl(MethodImplOptions.Synchronized)]
        void Watcher_Deleted(object sender, FileSystemEventArgs e)
        {
            Console.WriteLine("Watcher_Deleted {0} {1}", e.ChangeType, e.Name, e.FullPath);
            WatcherEvents.Add(e);
            IdleDetector.Ping();
        }

        [MethodImpl(MethodImplOptions.Synchronized)]
        void Watcher_Changed(object sender, FileSystemEventArgs e)
        {
            WatcherEvents.Add(e);
            Console.WriteLine("Watcher_Changed {0} {1} IsFromRename:{3}", e.ChangeType, e.Name, e.FullPath, e is RenamedEventArgs);
            IdleDetector.Ping();
        }

        [MethodImpl(MethodImplOptions.Synchronized)]
        void Watcher_Created(object sender, FileSystemEventArgs e)
        {
            WatcherEvents.Add(e);
            Console.WriteLine("Watcher_Created {0} {1}", e.ChangeType, e.Name, e.FullPath);
            IdleDetector.Ping();
        }

        public Action<List<FileSystemEventArgs>> FilesChanged { get; set; }

        public FileSystemWatcher Watcher { get; set; }
    }

}
