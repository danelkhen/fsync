using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading.Tasks;
using WinSCP;
using System.IO;
using Corex.Helpers;
using System.Globalization;

namespace fsync
{
    class FSync
    {
        #region Commands
        public void SyncToRemote()
        {
            Sync(SynchronizationMode.Remote, false, false);
        }
        public void SyncToRemotePreview()
        {
            Sync(SynchronizationMode.Remote, true, false);
        }
        public void SyncToRemoteWithDelete()
        {
            Sync(SynchronizationMode.Remote, false, true);
        }
        public void SyncToRemoteWithDeletePreview()
        {
            Sync(SynchronizationMode.Remote, true, true);
        }
        public void SyncToLocal()
        {
            InternalSyncToLocal(false);
        }
        public void SyncToLocalWithDelete()
        {
            InternalSyncToLocal(true);
        }
        public void InternalSyncToLocal(bool allowDelete)
        {
            DisableRealTime(() =>
            {
                Do(BackupLocal);
                LocalDir.ToDirectoryInfo().VerifyExists();
                Sync(SynchronizationMode.Local, false, allowDelete);
                Console.WriteLine("Finished on {0}", DateTime.Now);
            });
        }
        public void CopyOverwriteLocal()
        {
            DisableRealTime(() =>
            {
                Do(BackupLocal);
                if (IsFileMode)
                {
                    var x = Session.Get(t => t.GetFiles(RemoteFile, LocalFile, false, new TransferOptions { }));
                    x.Check();
                }
                else
                {
                    var x = Session.Get(t => t.GetFiles(RemoteDir, LocalDir, false, new TransferOptions { }));
                    x.Check();
                }
            });
        }

        void DisableRealTime(Action action)
        {
            var wasRunning = FileWatcher != null && FileWatcher.IsRealTimeRunning;
            if (wasRunning)
                Do(StopRealtime);
            action();
            if (wasRunning)
                Do(StartRealtime);
        }

        bool IsFileMode { get { return LocalFile != null || RemoteFile != null; } }

        void VerifyDirMode()
        {
            if (IsFileMode)
                throw new Exception("Not available in file mode");
        }
        public void SyncToLocalPreview()
        {
            Sync(SynchronizationMode.Local, true, false);
        }
        public void SyncToLocalWithDeletePreview()
        {
            Sync(SynchronizationMode.Local, true, true);
        }
        public void SyncPreview()
        {
            Sync(SynchronizationMode.Both, true, false);
        }

        public bool IncludeSubdirectories { get; set; }

        public void StartRealtime()
        {
            if (FileWatcher == null)
            {
                FileWatcher = new FileWatcher { LocalDir = LocalDir, IncludeSubdirectories = IncludeSubdirectories, FilesChanged = FileWatcher_FilesChanged };
                if (LocalFile != null)
                {
                    FileWatcher.LocalDir = Path.GetDirectoryName(LocalFile);
                    FileWatcher.Filter = Path.GetFileName(LocalFile);
                }
            }
            FileWatcher.StartRealtime();
        }

        public Action<List<FileSystemEventArgs>> FilesUploaded { get; set; }
        private void FileWatcher_FilesChanged(List<FileSystemEventArgs> list)
        {
            var uploaded = Session.Get(t => list.Where(x => Upload(t, x.Name)).ToList());
            if (FilesUploaded != null)
                FilesUploaded(uploaded);

        }

        void VerifyRemoteDir(Session session, string dir)
        {
            try
            {
                var remoteDir = session.ListDirectory(dir);
            }
            catch
            {
                try
                {
                    session.CreateDirectory(dir);
                }
                catch
                {
                    var parent = Path.GetDirectoryName(dir).Replace("\\", "/");
                    if (parent.Split('/').Length < 2)
                        throw;
                    VerifyRemoteDir(session, parent);
                    session.CreateDirectory(dir);
                }
            }

        }
        private bool Upload(Session session, string relPath)
        {
            if (Path.GetExtension(relPath).EqualsIgnoreCase(".tmp"))
                return false;
            string localPath;
            string remotePath;
            if (IsFileMode)
            {
                localPath = Path.GetDirectoryName(LocalFile) + relPath;
                if (localPath != LocalFile)
                    throw new Exception("localPath!=LocalFile");
                remotePath = RemoteFile;
            }
            else
            {
                localPath = Path.Combine(LocalDir, relPath);
                remotePath = RemoteDir + relPath.Replace('\\', '/');
            }
            if (!File.Exists(localPath))
                return false;
            Console.WriteLine("Uploading {0} to {1}", localPath, remotePath);
            var remoteDir = Path.GetDirectoryName(remotePath).Replace("\\", "/");
            VerifyRemoteDir(session, remoteDir);

            var res = session.PutFiles(localPath, remotePath);

            if (!res.IsSuccess)
            {
                Console.WriteLine("FAILED!");
                res.Failures.ForEach(Console.WriteLine);
                return false;
            }
            // Print results
            foreach (TransferEventArgs transfer in res.Transfers)
            {
                Console.WriteLine("Upload of {0} succeeded", transfer.FileName);
            }
            return true;
        }

        public void StopRealtime()
        {
            FileWatcher.StopRealtime();
        }
        static DateTime? TryParseExact(string s, string format)
        {
            DateTime dt;
            if (DateTime.TryParseExact(s, format, null, DateTimeStyles.None, out dt))
                return dt;
            return null;

        }
        public void BackupLocal()
        {
            var from = DateTime.Today;
            var format = "yyyy-MM-dd HH-mm-ss";
            if (BackupDir.ToDirectoryInfo().Exists)
            {
                var lastDir = BackupDir.ToDirectoryInfo().GetDirectories().Select(t => t.Name).OrderBy(t => t).LastOrDefault();
                if (lastDir != null)
                {
                    from = TryParseExact(lastDir, format).GetValueOrDefault(from);
                }
            }
            var dir = BackupDir.ToDirectoryInfo().GetDirectory(DateTime.Now.ToString(format));
            if (!dir.Exists)
                dir.Create();
            Console.WriteLine("Backing up local dir to {0}", dir.FullName);
            if (IsFileMode)
            {
                LocalFile.ToFileInfo().CopyToDirectory(dir, true);
            }
            else if (LocalDir.ToDirectoryInfo().Exists)
            {
                LocalDir.ToDirectoryInfo().Copy("*", IncludeSubdirectories, dir, true, true, fi => fi.LastWriteTime >= from);
            }
        }

        #endregion

        public SessionOptions SessionOptions { get; set; }
        public void Init()
        {
            if (Session == null)
            {
                Session = ThreadSafe.Create(new Session());
                // Connect
                //Session.Do(t => t.FileTransferred += (s, e) => Session.Do(x => session_FileTransferred(s, e)));
                Session.Do(t => t.OutputDataReceived += (s, e) => Console.WriteLine(e.Data));
            }
            if (AutoConnect)
                Do(Connect);
            if (AutoStartRealTime)
                Do(StartRealtime);
        }
        public FakeConsole Console { get; set; }
        public string BackupDir { get; set; }

        public void Connect()
        {
            Session.Do(t => t.Open(SessionOptions));
        }
        public void Disconnect()
        {
            Session.Do(t => t.Abort());
        }

        void Dispose()
        {
            if (Session != null)
            {
                Session.Do(t => t.Dispose());
                Session = null;
            }
        }
        public string LocalDir { get; set; }
        public string RemoteDir { get; set; }

        public FileWatcher FileWatcher { get; set; }


        void Sync(SynchronizationMode syncMode, bool preview, bool allowDelete)
        {
            if (IsFileMode)
            {
                var remoteFile = Session.Get(t => t.GetFileInfo(RemoteFile));
                var localFile = LocalFile.ToFileInfo();
                var diff = localFile.LastWriteTime - remoteFile.LastWriteTime;
                if (diff == TimeSpan.Zero)
                    return;
                if (diff > TimeSpan.Zero)
                {
                    if (syncMode == SynchronizationMode.Both || syncMode == SynchronizationMode.Remote)
                    {
                        Console.WriteLine("Local file is newer");
                        if (!preview)
                        {
                            //upload
                            var x = Session.Get(t => t.PutFiles(LocalFile, RemoteFile));
                            x.Check();
                        }
                    }
                }
                else
                {
                    if (syncMode == SynchronizationMode.Both || syncMode == SynchronizationMode.Local)
                    {
                        Console.WriteLine("Remote file is newer");
                        if (!preview)
                        {
                            //download
                            var x = Session.Get(t => t.GetFiles(RemoteFile, LocalFile));
                            x.Check();
                        }
                    }
                }

            }
            else
            {
                string fileMask = null;
                if(!IncludeSubdirectories)
                    fileMask = "|*/";
                var x = Session.Get(t => t.SynchronizeDirectories2(syncMode, LocalDir, RemoteDir, allowDelete, false,
                    SynchronizationCriteria.Time,
                    new TransferOptions { TransferMode = TransferMode.Automatic, FileMask = fileMask },
                    new SynchronizeOptions { Preview = preview }));
                x.Check();
                Console.WriteLine("Uploads: {0} Downloads: {1} Removals: {2} Failures:{3}", x.Uploads.Count, x.Downloads.Count, x.Removals.Count, x.Failures.Count);
            }
        }


        void Do(Action action)
        {
            var mi = action.Method;
            Console.WriteLine(mi.Name);
            action();
        }


        public ThreadSafe<Session> Session { get; set; }
        public bool AutoConnect { get; set; }
        public bool AutoStartRealTime { get; set; }

        public string LocalFile { get; set; }

        public string RemoteFile { get; set; }
    }
}
