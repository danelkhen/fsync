﻿using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Threading;
using System.Xml;
using Microsoft.Win32;
using System.Diagnostics;
using System.Security;
using System.Text.RegularExpressions;

namespace WinSCP
{
    [Guid("38649D44-B839-4F2C-A9DC-5D45EEA4B5E9")]
    [ComVisible(true)]
    public enum SynchronizationMode
    {
        Local = 0,
        Remote = 1,
        Both = 2,
    }

    [Guid("3F770EC1-35F5-4A7B-A000-46A2F7A213D8")]
    [ComVisible(true)]
    [Flags]
    public enum SynchronizationCriteria
    {
        None = 0x00,
        Time = 0x01,
        Size = 0x02,
        Either = Time | Size,
    }

    [Guid("6C441F60-26AA-44FC-9B93-08884768507B")]
    [ComVisible(true)]
    [Flags]
    public enum EnumerationOptions
    {
        None = 0x00,
        AllDirectories = 0x01,
        MatchDirectories = 0x02,
        EnumerateDirectories = 0x04,
    }

    [Guid("16B6D8F6-C0B4-487D-9546-A25BBF582ED6")]
    [ComVisible(true)]
    public enum ProgressSide
    {
        Local = 0,
        Remote = 1,
    }

    public delegate void OutputDataReceivedEventHandler(object sender, OutputDataReceivedEventArgs e);
    public delegate void FileTransferredEventHandler(object sender, TransferEventArgs e);
    public delegate void FileTransferProgressEventHandler(object sender, FileTransferProgressEventArgs e);
    public delegate void FailedEventHandler(object sender, FailedEventArgs e);

    [Guid("56FFC5CE-3867-4EF0-A3B5-CFFBEB99EA35")]
    [ClassInterface(Constants.ClassInterface)]
    [ComVisible(true)]
    [ComSourceInterfaces(typeof(ISessionEvents))]
    public sealed partial class Session : IDisposable, IReflect
    {
        public string ExecutablePath { get { return _executablePath; } set { CheckNotOpened(); _executablePath = value; } }
        public string ExecutableProcessUserName { get { return _executableProcessUserName; } set { CheckNotOpened(); _executableProcessUserName = value; } }
        public SecureString ExecutableProcessPassword { get { return _executableProcessPassword; } set { CheckNotOpened(); _executableProcessPassword = value; } }
        public string AdditionalExecutableArguments { get { return _additionalExecutableArguments; } set { CheckNotOpened(); _additionalExecutableArguments = value; } }
        [Obsolete("Use AddRawConfiguration")]
        public bool DefaultConfiguration { get { return _defaultConfiguration; } set { CheckNotOpened(); _defaultConfiguration = value; } }
        [Obsolete("Always use the same version of assembly and WinSCP")]
        public bool DisableVersionCheck { get { return _disableVersionCheck; } set { CheckNotOpened(); _disableVersionCheck = value; } }
        [Obsolete("Use AddRawConfiguration")]
        public string IniFilePath { get { return _iniFilePath; } set { CheckNotOpened(); _iniFilePath = value; } }
        public TimeSpan ReconnectTime { get { return _reconnectTime; } set { CheckNotOpened(); _reconnectTime = value; } }
        public int ReconnectTimeInMilliseconds { get { return Tools.TimeSpanToMilliseconds(ReconnectTime); } set { ReconnectTime = Tools.MillisecondsToTimeSpan(value); } }
        public string DebugLogPath { get { CheckNotDisposed(); return Logger.LogPath; } set { CheckNotDisposed(); Logger.LogPath = value; } }
        public int DebugLogLevel { get { CheckNotDisposed(); return Logger.LogLevel; } set { CheckNotDisposed(); Logger.LogLevel = value; } }
        public string SessionLogPath { get { return _sessionLogPath; } set { CheckNotOpened(); _sessionLogPath = value; } }
        public string XmlLogPath { get { return _xmlLogPath; } set { CheckNotOpened(); _xmlLogPath = value; } }
        #if DEBUG
        public bool GuardProcessWithJob { get { return GuardProcessWithJobInternal; } set { GuardProcessWithJobInternal = value; } }
        public bool TestHandlesClosed { get { return TestHandlesClosedInternal; } set { TestHandlesClosedInternal = value; } }
        #endif
        public string HomePath { get { CheckOpened(); return _homePath; } }

        public TimeSpan Timeout { get; set; }

        public StringCollection Output { get; private set; }
        public bool Opened { get { CheckNotDisposed(); return (_process != null); } }

        public event FileTransferredEventHandler FileTransferred;
        public event FailedEventHandler Failed;
        public event OutputDataReceivedEventHandler OutputDataReceived;

        public event FileTransferProgressEventHandler FileTransferProgress
        {
            add
            {
                using (Logger.CreateCallstackAndLock())
                {
                    CheckNotOpened();
                    _fileTransferProgress += value;
                }
            }

            remove
            {
                using (Logger.CreateCallstackAndLock())
                {
                    CheckNotOpened();
                    _fileTransferProgress -= value;
                }
            }
        }

        public Session()
        {
            Logger = new Logger();

            using (Logger.CreateCallstackAndLock())
            {
                Timeout = new TimeSpan(0, 1, 0);
                _reconnectTime = new TimeSpan(0, 2, 0); // keep in sync with TScript::OptionImpl
                ResetOutput();
                _operationResults = new List<OperationResultBase>();
                _events = new List<Action>();
                _eventsEvent = new AutoResetEvent(false);
                _disposed = false;
                _defaultConfiguration = true;
                _logUnique = 0;
                _guardProcessWithJob = true;
                RawConfiguration = new Dictionary<string, string>();
            }
        }

        private void ResetOutput()
        {
            Output = new StringCollection();
            _error = new StringCollection();
        }

        public void Dispose()
        {
            using (Logger.CreateCallstackAndLock())
            {
                _disposed = true;

                Cleanup();
                Logger.Dispose();

                if (_eventsEvent != null)
                {
                    _eventsEvent.Close();
                    _eventsEvent = null;
                }

                GC.SuppressFinalize(this);
            }
        }

        public void Abort()
        {
            using (Logger.CreateCallstack())
            {
                CheckOpened();

                _aborted = true;

                // double-check
                if (_process != null)
                {
                    _process.Abort();
                }
            }
        }

        public void Open(SessionOptions sessionOptions)
        {
            using (Logger.CreateCallstackAndLock())
            {
                CheckNotDisposed();

                if (Opened)
                {
                    throw Logger.WriteException(new InvalidOperationException("Session is already opened"));
                }

                try
                {
                    SetupTempPath();
                    ResetOutput();

                    _process = ExeSessionProcess.CreateForSession(this);

                    _process.OutputDataReceived += ProcessOutputDataReceived;

                    _process.Start();

                    GotOutput();

                    // setup batch mode
                    WriteCommand("option batch on");
                    WriteCommand("option confirm off");

                    object reconnectTimeValue;
                    if (ReconnectTime != TimeSpan.MaxValue)
                    {
                        reconnectTimeValue = (int)ReconnectTime.TotalSeconds;
                    }
                    else
                    {
                        reconnectTimeValue = "off";
                    }
                    string reconnectTimeCommand =
                        string.Format(CultureInfo.InvariantCulture, "option reconnecttime {0}", reconnectTimeValue);
                    WriteCommand(reconnectTimeCommand);

                    string command;
                    string log;
                    SessionOptionsToUrlAndSwitches(sessionOptions, false, out command, out log);
                    const string openCommand = "open ";
                    command = openCommand + command;
                    log = openCommand + log;
                    WriteCommand(command, log);

                    // Wait until the log file gets created or WinSCP terminates (in case of fatal error)
                    do
                    {
                        string logExplanation;
                        lock (Output)
                        {
                            if (_error.Count > 0)
                            {
                                logExplanation = GetErrorOutputMessage();
                            }
                            else if (Output.Count > 0)
                            {
                                logExplanation =
                                    string.Format(
                                        CultureInfo.CurrentCulture, "Output was \"{0}\". ", ListToString(Output));
                            }
                            else
                            {
                                logExplanation = "There was no output. ";
                            }
                        }
                        logExplanation +=
                            string.Format(CultureInfo.CurrentCulture,
                                "Response log file {0} was not created. This could indicate lack of write permissions to the log folder or problems starting WinSCP itself.",
                                XmlLogPath);

                        if (_process.HasExited && !File.Exists(XmlLogPath))
                        {
                            Logger.WriteCounters();
                            Logger.WriteProcesses();
                            _process.WriteStatus();
                            string exitCode = string.Format(CultureInfo.CurrentCulture, "{0}", _process.ExitCode);
                            if (_process.ExitCode < 0)
                            {
                                exitCode = string.Format(CultureInfo.CurrentCulture, "{0} ({1:X})", exitCode, _process.ExitCode);
                            }
                            throw Logger.WriteException(
                                new SessionLocalException(this,
                                    string.Format(CultureInfo.CurrentCulture, "WinSCP process terminated with exit code {0}. ", exitCode) +
                                    logExplanation));
                        }

                        Thread.Sleep(50);

                        CheckForTimeout(
                            "WinSCP has not responded in time. " +
                            logExplanation);

                    } while (!File.Exists(XmlLogPath));

                    _logReader = new SessionLogReader(this);

                    _logReader.WaitForNonEmptyElement("session", LogReadFlags.ThrowFailures);

                    // special variant of ElementLogReader that throws when closing element (</session>) is encountered
                    _reader = new SessionElementLogReader(_logReader);

                    // Skip "open" command <group>
                    using (ElementLogReader groupReader = _reader.WaitForGroupAndCreateLogReader())
                    {
                        ReadElement(groupReader, LogReadFlags.ThrowFailures);
                    }

                    WriteCommand("pwd");

                    using (ElementLogReader groupReader = _reader.WaitForGroupAndCreateLogReader())
                    using (ElementLogReader cwdReader = groupReader.WaitForNonEmptyElementAndCreateLogReader("cwd", LogReadFlags.ThrowFailures))
                    {
                        while (cwdReader.Read(0))
                        {
                            string value;
                            if (cwdReader.GetEmptyElementValue("cwd", out value))
                            {
                                _homePath = value;
                            }
                        }

                        groupReader.ReadToEnd(LogReadFlags.ThrowFailures);
                    }

                }
                catch (Exception e)
                {
                    Logger.WriteLine("Exception: {0}", e);
                    Cleanup();
                    throw;
                }
            }
        }

        internal string GetErrorOutputMessage()
        {
            string result = null;
            if (_error.Count > 0)
            {
                result = string.Format(CultureInfo.CurrentCulture, "Error output was \"{0}\". ", ListToString(_error));
            }
            return result;
        }

        private static string ListToString(StringCollection list)
        {
            string[] error = new string[list.Count];
            list.CopyTo(error, 0);
            string s = string.Join(Environment.NewLine, error);
            return s;
        }

        public string ScanFingerprint(SessionOptions sessionOptions)
        {
            using (Logger.CreateCallstackAndLock())
            {
                string result;

                CheckNotDisposed();

                if (Opened)
                {
                    throw Logger.WriteException(new InvalidOperationException("Session is already opened"));
                }

                try
                {
                    ResetOutput();

                    string command;
                    string log; // unused
                    SessionOptionsToUrlAndSwitches(sessionOptions, true, out command, out log);

                    string additionalArguments = "/fingerprintscan " + command;

                    _process = ExeSessionProcess.CreateForConsole(this, additionalArguments);

                    _process.OutputDataReceived += ProcessOutputDataReceived;

                    _process.Start();

                    GotOutput();

                    while (!_process.HasExited)
                    {
                        Thread.Sleep(50);

                        CheckForTimeout();
                    }

                    string output = string.Join(Environment.NewLine, new List<string>(Output).ToArray());
                    if (_process.ExitCode == 0)
                    {
                        result = output;
                    }
                    else
                    {
                        throw Logger.WriteException(new SessionRemoteException(this, output));
                    }
                }
                catch (Exception e)
                {
                    Logger.WriteLine("Exception: {0}", e);
                    throw;
                }
                finally
                {
                    Cleanup();
                }

                return result;
            }
        }

        public void Close()
        {
            using (Logger.CreateCallstackAndLock())
            {
                CheckOpened();

                Cleanup();
            }
        }

        public RemoteDirectoryInfo ListDirectory(string path)
        {
            using (Logger.CreateCallstackAndLock())
            {
                CheckOpened();

                WriteCommand(string.Format(CultureInfo.InvariantCulture, "ls -- \"{0}\"", Tools.ArgumentEscape(IncludeTrailingSlash(path))));

                RemoteDirectoryInfo result = new RemoteDirectoryInfo();

                using (ElementLogReader groupReader = _reader.WaitForGroupAndCreateLogReader())
                using (ElementLogReader lsReader = groupReader.WaitForNonEmptyElementAndCreateLogReader("ls", LogReadFlags.ThrowFailures))
                {
                    string destination = null;
                    if (lsReader.TryWaitForEmptyElement("destination", 0))
                    {
                        lsReader.GetEmptyElementValue("destination", out destination);
                    }
                    if ((destination != null) && lsReader.TryWaitForNonEmptyElement("files", 0))
                    {
                        destination = IncludeTrailingSlash(destination);

                        using (ElementLogReader filesReader = lsReader.CreateLogReader())
                        {
                            while (filesReader.TryWaitForNonEmptyElement("file", 0))
                            {
                                RemoteFileInfo fileInfo = new RemoteFileInfo();

                                using (ElementLogReader fileReader = filesReader.CreateLogReader())
                                {
                                    while (fileReader.Read(0))
                                    {
                                        string value;
                                        if (fileReader.GetEmptyElementValue("filename", out value))
                                        {
                                            fileInfo.Name = value;
                                            fileInfo.FullName = destination + value;
                                        }
                                        else
                                        {
                                            ReadFile(fileInfo, fileReader);
                                        }
                                    }

                                    result.AddFile(fileInfo);
                                }
                            }
                        }

                        lsReader.ReadToEnd(LogReadFlags.ThrowFailures);
                        groupReader.ReadToEnd(LogReadFlags.ThrowFailures);
                    }
                    else
                    {
                        // "files" not found, keep reading, we expect "failure"
                        // This happens only in case of fatal errors,
                        // in case of normal error (non existing folder),
                        // the "failure" is caught in "group" already, before the "ls".
                        groupReader.ReadToEnd(LogReadFlags.ThrowFailures);
                        // only if not "failure", throw "files" not found
                        throw Logger.WriteException(SessionLocalException.CreateElementNotFound(this, "files"));
                    }
                }

                return result;
            }
        }

        private IEnumerable<RemoteFileInfo> DoEnumerateRemoteFiles(string path, Regex regex, EnumerationOptions options, bool throwReadErrors)
        {
            Logger.WriteLine("Starting enumeration of {0} ...", path);

            bool allDirectories = ((options & EnumerationOptions.AllDirectories) == EnumerationOptions.AllDirectories);
            bool matchDirectories = ((options & EnumerationOptions.MatchDirectories) == EnumerationOptions.MatchDirectories);
            bool enumerateDirectories = ((options & EnumerationOptions.EnumerateDirectories) == EnumerationOptions.EnumerateDirectories);

            if (enumerateDirectories && !allDirectories)
            {
                throw Logger.WriteException(new ArgumentException("Cannot use enumeration option EnumerateDirectories without AllDirectories"));
            }

            if (enumerateDirectories && matchDirectories)
            {
                throw Logger.WriteException(new ArgumentException("Cannot combine enumeration option EnumerateDirectories with MatchDirectories"));
            }

            RemoteDirectoryInfo directoryInfo;

            try
            {
                // Need to use guarded method for the listing, see a comment in EnumerateRemoteFiles
                directoryInfo = ListDirectory(path);
            }
            catch (SessionRemoteException)
            {
                if (throwReadErrors)
                {
                    throw;
                }
                else
                {
                    directoryInfo = null;
                }
            }

            if (directoryInfo != null)
            {
                foreach (RemoteFileInfo fileInfo in directoryInfo.Files)
                {
                    if (!fileInfo.IsThisDirectory && !fileInfo.IsParentDirectory)
                    {
                        bool matches = regex.IsMatch(fileInfo.Name);

                        bool enumerate;
                        if (!fileInfo.IsDirectory)
                        {
                            enumerate = matches;
                        }
                        else
                        {
                            if (enumerateDirectories)
                            {
                                enumerate = true;
                            }
                            else if (matchDirectories)
                            {
                                enumerate = matches;
                            }
                            else
                            {
                                enumerate = false;
                            }
                        }

                        if (enumerate)
                        {
                            Logger.WriteLine("Enumerating {0}", fileInfo.FullName);
                            yield return fileInfo;
                        }


                        if (fileInfo.IsDirectory && allDirectories)
                        {
                            foreach (RemoteFileInfo fileInfo2 in DoEnumerateRemoteFiles(CombinePaths(path, fileInfo.Name), regex, options, false))
                            {
                                yield return fileInfo2;
                            }
                        }
                    }
                }
            }

            Logger.WriteLine("Ended enumeration of {0}", path);
        }

        public IEnumerable<RemoteFileInfo> EnumerateRemoteFiles(string path, string mask, EnumerationOptions options)
        {
            // Note that this method exits as soon as DoEnumerateRemoteFiles is entered,
            // so the Session object is not guarded during the whole enumeration.
            // Though it should not matter as it uses only guarded methods (ListDirectory)
            // for the actual work on the session
            using (Logger.CreateCallstackAndLock())
            {
                CheckOpened();

                Regex regex = MaskToRegex(mask);

                return DoEnumerateRemoteFiles(path, regex, options, true);
            }
        }

        private static Regex MaskToRegex(string mask)
        {
            if (string.IsNullOrEmpty(mask) ||
                // *.* has to match even filename without dot
                (mask == "*.*"))
            {
                mask = "*";
            }

            return
                new Regex(
                    '^' +
                    mask
                        .Replace(".", "[.]")
                        .Replace("*", ".*")
                        .Replace("?", ".") +
                    '$',
                    RegexOptions.IgnoreCase);
        }

        public TransferOperationResult PutFiles(string localPath, string remotePath, bool remove = false, TransferOptions options = null)
        {
            using (Logger.CreateCallstackAndLock())
            {
                if (options == null)
                {
                    options = new TransferOptions();
                }

                CheckOpened();

                WriteCommand(
                    string.Format(CultureInfo.InvariantCulture,
                        "put {0} {1} -- \"{2}\" \"{3}\"",
                        BooleanSwitch(remove, "delete"), options.ToSwitches(),
                        Tools.ArgumentEscape(localPath), Tools.ArgumentEscape(remotePath)));

                TransferOperationResult result = new TransferOperationResult();

                using (ElementLogReader groupReader = _reader.WaitForGroupAndCreateLogReader())
                using (RegisterOperationResult(result))
                using (CreateProgressHandler())
                {
                    TransferEventArgs args = null;
                    bool mkdir = false;

                    while (groupReader.Read(0))
                    {
                        if (groupReader.IsNonEmptyElement(TransferEventArgs.UploadTag))
                        {
                            AddTransfer(result, args);
                            args = TransferEventArgs.Read(ProgressSide.Local, groupReader);
                            mkdir = false;
                        }
                        else if (groupReader.IsNonEmptyElement(TransferEventArgs.MkDirTag))
                        {
                            AddTransfer(result, args);
                            args = null;
                            mkdir = true;
                            // For now, silently ignoring results (even errors)
                            // of mkdir operation, including future chmod/touch
                        }
                        else if (groupReader.IsNonEmptyElement(ChmodEventArgs.Tag))
                        {
                            if (!mkdir)
                            {
                                if (args == null)
                                {
                                    throw Logger.WriteException(new InvalidOperationException("Tag chmod before tag upload"));
                                }
                                args.Chmod = ChmodEventArgs.Read(groupReader);
                            }
                        }
                        else if (groupReader.IsNonEmptyElement(TouchEventArgs.Tag))
                        {
                            if (!mkdir)
                            {
                                if (args == null)
                                {
                                    throw Logger.WriteException(new InvalidOperationException("Tag touch before tag upload"));
                                }
                                args.Touch = TouchEventArgs.Read(groupReader);
                            }
                        }
                    }

                    AddTransfer(result, args);
                }

                return result;
            }
        }

        private void AddTransfer(TransferOperationResult result, TransferEventArgs args)
        {
            if (args != null)
            {
                result.AddTransfer(args);
                RaiseFileTransferredEvent(args);
            }
        }

        public TransferOperationResult GetFiles(string remotePath, string localPath, bool remove = false, TransferOptions options = null)
        {
            using (Logger.CreateCallstackAndLock())
            {
                if (options == null)
                {
                    options = new TransferOptions();
                }

                CheckOpened();

                WriteCommand(
                    string.Format(CultureInfo.InvariantCulture, "get {0} {1} -- \"{2}\" \"{3}\"",
                        BooleanSwitch(remove, "delete"), options.ToSwitches(),
                        Tools.ArgumentEscape(remotePath), Tools.ArgumentEscape(localPath)));

                TransferOperationResult result = new TransferOperationResult();

                using (ElementLogReader groupReader = _reader.WaitForGroupAndCreateLogReader())
                using (RegisterOperationResult(result))
                using (CreateProgressHandler())
                {
                    TransferEventArgs args = null;

                    while (groupReader.Read(0))
                    {
                        if (groupReader.IsNonEmptyElement(TransferEventArgs.DownloadTag))
                        {
                            AddTransfer(result, args);
                            args = TransferEventArgs.Read(ProgressSide.Remote, groupReader);
                        }
                        else if (groupReader.IsNonEmptyElement(RemovalEventArgs.Tag))
                        {
                            // When "downloading and deleting" a folder,
                            // we get "rm" tag without preceeding "download" tag.
                            // So we use only the first "rm" tag after preceeding "download" tag,
                            // silently ignoring the others
                            if ((args != null) && (args.Removal == null))
                            {
                                args.Removal = RemovalEventArgs.Read(groupReader);
                            }
                        }
                    }

                    AddTransfer(result, args);
                }

                return result;
            }
        }

        public RemovalOperationResult RemoveFiles(string path)
        {
            using (Logger.CreateCallstackAndLock())
            {
                CheckOpened();

                WriteCommand(string.Format(CultureInfo.InvariantCulture, "rm -- \"{0}\"", Tools.ArgumentEscape(path)));

                RemovalOperationResult result = new RemovalOperationResult();

                using (ElementLogReader groupReader = _reader.WaitForGroupAndCreateLogReader())
                using (RegisterOperationResult(result))
                {
                    while (groupReader.Read(0))
                    {
                        if (groupReader.IsNonEmptyElement(RemovalEventArgs.Tag))
                        {
                            result.AddRemoval(RemovalEventArgs.Read(groupReader));
                        }
                    }
                }

                return result;
            }
        }

        public SynchronizationResult SynchronizeDirectories(
            SynchronizationMode mode, string localPath, string remotePath,
            bool removeFiles, bool mirror = false, SynchronizationCriteria criteria = SynchronizationCriteria.Time,
            TransferOptions options = null)
        {
            using (Logger.CreateCallstackAndLock())
            {
                if (options == null)
                {
                    options = new TransferOptions();
                }

                CheckOpened();

                if (removeFiles && (mode == SynchronizationMode.Both))
                {
                    throw Logger.WriteException(new ArgumentException("Cannot delete files in synchronization mode Both"));
                }

                if (mirror && (mode == SynchronizationMode.Both))
                {
                    throw Logger.WriteException(new ArgumentException("Cannot mirror files in synchronization mode Both"));
                }

                if ((criteria != SynchronizationCriteria.Time) && (mode == SynchronizationMode.Both))
                {
                    throw Logger.WriteException(new ArgumentException("Only Time criteria is allowed in synchronization mode Both"));
                }

                string modeName;
                switch (mode)
                {
                    case SynchronizationMode.Local:
                        modeName = "local";
                        break;
                    case SynchronizationMode.Remote:
                        modeName = "remote";
                        break;
                    case SynchronizationMode.Both:
                        modeName = "both";
                        break;
                    default:
                        throw Logger.WriteException(new ArgumentOutOfRangeException("mode"));
                }

                string criteriaName;
                switch (criteria)
                {
                    case SynchronizationCriteria.None:
                        criteriaName = "none";
                        break;
                    case SynchronizationCriteria.Time:
                        criteriaName = "time";
                        break;
                    case SynchronizationCriteria.Size:
                        criteriaName = "size";
                        break;
                    case SynchronizationCriteria.Either:
                        criteriaName = "either";
                        break;
                    default:
                        throw Logger.WriteException(new ArgumentOutOfRangeException("criteria"));
                }

                WriteCommand(
                    string.Format(CultureInfo.InvariantCulture,
                        "synchronize {0} {1} {2} {3} -criteria=\"{4}\" -- \"{5}\" \"{6}\"",
                        modeName,
                        BooleanSwitch(removeFiles, "delete"),
                        BooleanSwitch(mirror, "mirror"),
                        options.ToSwitches(),
                        criteriaName,
                        Tools.ArgumentEscape(localPath), Tools.ArgumentEscape(remotePath)));

                return ReadSynchronizeDirectories();
            }
        }

        private SynchronizationResult ReadSynchronizeDirectories()
        {
            using (Logger.CreateCallstack())
            {
                SynchronizationResult result = new SynchronizationResult();

                using (ElementLogReader groupReader = _reader.WaitForGroupAndCreateLogReader())
                using (RegisterOperationResult(result))
                using (CreateProgressHandler())
                {
                    TransferEventArgs transfer = null;

                    while (groupReader.Read(0))
                    {
                        ProgressSide? newSide = null;
                        if (groupReader.IsNonEmptyElement(TransferEventArgs.UploadTag))
                        {
                            newSide = ProgressSide.Local;
                        }
                        else if (groupReader.IsNonEmptyElement(TransferEventArgs.DownloadTag))
                        {
                            newSide = ProgressSide.Remote;
                        }

                        if (newSide.HasValue)
                        {
                            AddSynchronizationTransfer(result, transfer);
                            transfer = TransferEventArgs.Read(newSide.Value, groupReader);
                        }
                        else if (groupReader.IsNonEmptyElement(RemovalEventArgs.Tag))
                        {
                            result.AddRemoval(RemovalEventArgs.Read(groupReader));
                        }
                        else if (groupReader.IsNonEmptyElement(ChmodEventArgs.Tag))
                        {
                            if (transfer == null)
                            {
                                throw Logger.WriteException(new InvalidOperationException("Tag chmod before tag download"));
                            }
                            transfer.Chmod = ChmodEventArgs.Read(groupReader);
                        }
                        else if (groupReader.IsNonEmptyElement(TouchEventArgs.Tag))
                        {
                            if (transfer == null)
                            {
                                throw Logger.WriteException(new InvalidOperationException("Tag touch before tag download"));
                            }
                            transfer.Touch = TouchEventArgs.Read(groupReader);
                        }
                    }

                    AddSynchronizationTransfer(result, transfer);
                }
                return result;
            }
        }

        public CommandExecutionResult ExecuteCommand(string command)
        {
            using (Logger.CreateCallstackAndLock())
            {
                CheckOpened();

                WriteCommand(string.Format(CultureInfo.InvariantCulture, "call {0}", command));

                CommandExecutionResult result = new CommandExecutionResult();

                // registering before creating group reader, so that
                // it is still registered, when group reader is read to the end in its .Dispose();
                using (RegisterOperationResult(result))
                using (ElementLogReader groupReader = _reader.WaitForGroupAndCreateLogReader())
                using (ElementLogReader callReader = groupReader.WaitForNonEmptyElementAndCreateLogReader("call", LogReadFlags.ThrowFailures))
                {
                    while (callReader.Read(0))
                    {
                        string value;
                        if (callReader.GetEmptyElementValue("output", out value))
                        {
                            result.Output = value;
                        }
                        if (callReader.GetEmptyElementValue("erroroutput", out value))
                        {
                            result.ErrorOutput = value;
                        }
                        if (callReader.GetEmptyElementValue("exitcode", out value))
                        {
                            result.ExitCode = int.Parse(value, CultureInfo.InvariantCulture);
                        }
                    }
                }

                return result;
            }
        }

        public RemoteFileInfo GetFileInfo(string path)
        {
            using (Logger.CreateCallstackAndLock())
            {
                CheckOpened();

                return DoGetFileInfo(path);
            }
        }

        public bool FileExists(string path)
        {
            using (Logger.CreateCallstackAndLock())
            {
                CheckOpened();

                try
                {
                    _ignoreFailed = true;
                    try
                    {
                        DoGetFileInfo(path);
                    }
                    finally
                    {
                        _ignoreFailed = false;
                    }
                    return true;
                }
                catch (SessionRemoteException)
                {
                    return false;
                }
            }
        }

        public byte[] CalculateFileChecksum(string algorithm, string path)
        {
            using (Logger.CreateCallstackAndLock())
            {
                WriteCommand(string.Format(CultureInfo.InvariantCulture, "checksum -- \"{0}\" \"{1}\"", Tools.ArgumentEscape(algorithm), Tools.ArgumentEscape(path)));

                string hex = null;

                using (ElementLogReader groupReader = _reader.WaitForGroupAndCreateLogReader())
                using (ElementLogReader checksumReader = groupReader.WaitForNonEmptyElementAndCreateLogReader("checksum", LogReadFlags.ThrowFailures))
                {
                    while (checksumReader.Read(0))
                    {
                        string value;
                        if (checksumReader.GetEmptyElementValue("checksum", out value))
                        {
                            hex = value;
                        }
                    }

                    groupReader.ReadToEnd(LogReadFlags.ThrowFailures);
                }

                int len = hex.Length;

                if ((len % 2) != 0)
                {
                    string error = string.Format(CultureInfo.CurrentCulture, "Invalid string representation of checksum - {0}", hex);
                    throw Logger.WriteException(new SessionLocalException(this, error));
                }

                int count = len / 2;
                byte[] bytes = new byte[count];
                for (int i = 0; i < count; i++)
                {
                    bytes[i] = Convert.ToByte(hex.Substring(i * 2, 2), 16);
                }
                return bytes;
            }
        }

        public void CreateDirectory(string path)
        {
            using (Logger.CreateCallstackAndLock())
            {
                CheckOpened();

                WriteCommand(string.Format(CultureInfo.InvariantCulture, "mkdir \"{0}\"", Tools.ArgumentEscape(path)));

                using (ElementLogReader groupReader = _reader.WaitForGroupAndCreateLogReader())
                using (ElementLogReader mkdirReader = groupReader.WaitForNonEmptyElementAndCreateLogReader(TransferEventArgs.MkDirTag, LogReadFlags.ThrowFailures))
                {
                    ReadElement(mkdirReader, 0);
                    groupReader.ReadToEnd(LogReadFlags.ThrowFailures);
                }
            }
        }

        public void MoveFile(string sourcePath, string targetPath)
        {
            using (Logger.CreateCallstackAndLock())
            {
                CheckOpened();

                WriteCommand(string.Format(CultureInfo.InvariantCulture, "mv \"{0}\" \"{1}\"", Tools.ArgumentEscape(sourcePath), Tools.ArgumentEscape(targetPath)));

                using (ElementLogReader groupReader = _reader.WaitForGroupAndCreateLogReader())
                {
                    if (!groupReader.TryWaitForNonEmptyElement("mv", LogReadFlags.ThrowFailures))
                    {
                        throw Logger.WriteException(new SessionRemoteException(this, string.Format(CultureInfo.CurrentCulture, "{0} not found.", sourcePath)));
                    }
                    else
                    {
                        using (ElementLogReader mvReader = groupReader.CreateLogReader())
                        {
                            ReadElement(mvReader, 0);
                            groupReader.ReadToEnd(LogReadFlags.ThrowFailures);
                        }
                    }
                }
            }
        }

        // This is not static method only to make it visible to COM
        public string EscapeFileMask(string fileMask)
        {
            if (fileMask == null)
            {
                throw Logger.WriteException(new ArgumentNullException("fileMask"));
            }
            int lastSlash = fileMask.LastIndexOf('/');
            string path = lastSlash > 0 ? fileMask.Substring(0, lastSlash + 1) : string.Empty;
            string mask = lastSlash > 0 ? fileMask.Substring(lastSlash + 1) : fileMask;
            // Keep in sync with EscapeFileMask in GenerateUrl.cpp
            mask = mask.Replace("[", "[[]").Replace("*", "[*]").Replace("?", "[?]");
            return path + mask;
        }

        public string TranslateRemotePathToLocal(string remotePath, string remoteRoot, string localRoot)
        {
            if (remotePath == null)
            {
                throw Logger.WriteException(new ArgumentNullException("remotePath"));
            }

            if (remoteRoot == null)
            {
                throw Logger.WriteException(new ArgumentNullException("remoteRoot"));
            }

            if (localRoot == null)
            {
                throw Logger.WriteException(new ArgumentNullException("localRoot"));
            }

            if ((localRoot.Length > 0) && !localRoot.EndsWith("\\", StringComparison.Ordinal))
            {
                localRoot += "\\";
            }

            // not adding to empty root paths, because the path may not even start with slash
            if ((remoteRoot.Length > 0) && !remoteRoot.EndsWith("/", StringComparison.Ordinal))
            {
                remoteRoot += "/";
            }

            string localPath;
            // special case
            if (remotePath == remoteRoot)
            {
                localPath = localRoot;
            }
            else
            {
                if (!remotePath.StartsWith(remoteRoot, StringComparison.Ordinal))
                {
                    throw Logger.WriteException(new InvalidOperationException(string.Format(CultureInfo.CurrentCulture, "{0} does not start with {1}", remotePath, remoteRoot)));
                }

                string subPath = remotePath.Substring(remoteRoot.Length);
                // can happen only when remoteRoot is empty
                if (subPath.StartsWith("/", StringComparison.Ordinal))
                {
                    subPath = subPath.Substring(1);
                }
                subPath = subPath.Replace('/', '\\');
                localPath = localRoot + subPath;
            }
            return localPath;
        }

        public string TranslateLocalPathToRemote(string localPath, string localRoot, string remoteRoot)
        {
            if (localPath == null)
            {
                throw Logger.WriteException(new ArgumentNullException("localPath"));
            }

            if (localRoot == null)
            {
                throw Logger.WriteException(new ArgumentNullException("localRoot"));
            }

            if (remoteRoot == null)
            {
                throw Logger.WriteException(new ArgumentNullException("remoteRoot"));
            }

            if ((localRoot.Length > 0) && !localRoot.EndsWith("\\", StringComparison.Ordinal))
            {
                localRoot += "\\";
            }

            // not adding to empty root paths, because the path may not even start with slash
            if ((remoteRoot.Length > 0) && !remoteRoot.EndsWith("/", StringComparison.Ordinal))
            {
                remoteRoot += "/";
            }

            string remotePath;
            // special case
            if (localPath == localRoot)
            {
                remotePath = remoteRoot;
            }
            else
            {
                if (!localPath.StartsWith(localRoot, StringComparison.Ordinal))
                {
                    throw Logger.WriteException(new InvalidOperationException(string.Format(CultureInfo.CurrentCulture, "{0} does not start with {1}", localPath, localRoot)));
                }

                string subPath = localPath.Substring(localRoot.Length);
                // can happen only when localRoot is empty
                if (subPath.StartsWith("\\", StringComparison.Ordinal))
                {
                    subPath = subPath.Substring(1);
                }
                subPath = subPath.Replace('\\', '/');
                remotePath = remoteRoot + subPath;
            }
            return remotePath;
        }

        public string CombinePaths(string path1, string path2)
        {
            if (path1 == null)
            {
                throw Logger.WriteException(new ArgumentNullException("path1"));
            }

            if (path2 == null)
            {
                throw Logger.WriteException(new ArgumentNullException("path2"));
            }

            string result;

            if (path2.StartsWith("/", StringComparison.Ordinal))
            {
                result = path2;
            }
            else
            {
                result =
                    path1 +
                    ((path1.Length == 0) || (path2.Length == 0) || path1.EndsWith("/", StringComparison.Ordinal) ? string.Empty : "/") +
                    path2;
            }
            return result;
        }

        public void AddRawConfiguration(string setting, string value)
        {
            CheckNotOpened();
            RawConfiguration.Add(setting, value);
        }

        [ComRegisterFunction]
        private static void ComRegister(Type t)
        {
            string subKey = GetTypeLibKey(t);
            Assembly assembly = Assembly.GetAssembly(t);
            object[] attributes = assembly.GetCustomAttributes(typeof(GuidAttribute), false);
            if (attributes.Length == 0)
            {
                throw new InvalidOperationException(string.Format(CultureInfo.CurrentCulture, "Cannot find {0} attribute for assembly {1}", typeof(GuidAttribute), assembly));
            }
            GuidAttribute guidAttribute = (GuidAttribute)attributes[0];
            Registry.ClassesRoot.CreateSubKey(subKey).SetValue(null, guidAttribute.Value);
        }

        [ComUnregisterFunction]
        private static void ComUnregister(Type t)
        {
            string subKey = GetTypeLibKey(t);
            Registry.ClassesRoot.DeleteSubKey(subKey, false);
        }

        private void ReadFile(RemoteFileInfo fileInfo, CustomLogReader fileReader)
        {
            using (Logger.CreateCallstack())
            {
                string value;
                if (fileReader.GetEmptyElementValue("type", out value))
                {
                    fileInfo.FileType = value[0];
                }
                else if (fileReader.GetEmptyElementValue("size", out value))
                {
                    fileInfo.Length = long.Parse(value, CultureInfo.InvariantCulture);
                }
                else if (fileReader.GetEmptyElementValue("modification", out value))
                {
                    fileInfo.LastWriteTime = XmlConvert.ToDateTime(value, XmlDateTimeSerializationMode.Local);
                }
                else if (fileReader.GetEmptyElementValue("permissions", out value))
                {
                    fileInfo.FilePermissions = FilePermissions.CreateReadOnlyFromText(value);
                }
                else if (fileReader.GetEmptyElementValue("owner", out value))
                {
                    fileInfo.Owner = value;
                }
                else if (fileReader.GetEmptyElementValue("group", out value))
                {
                    fileInfo.Group = value;
                }
            }
        }

        internal static string BooleanSwitch(bool flag, string name)
        {
            return flag ? string.Format(CultureInfo.InvariantCulture, "-{0}", name) : null;
        }

        internal static string BooleanSwitch(bool flag, string onName, string offName)
        {
            return flag ? string.Format(CultureInfo.InvariantCulture, "-{0}", onName) : string.Format(CultureInfo.InvariantCulture, "-{0}", offName);
        }

        private void AddSynchronizationTransfer(SynchronizationResult result, TransferEventArgs transfer)
        {
            if (transfer != null)
            {
                if (transfer.Side == ProgressSide.Local)
                {
                    result.AddUpload(transfer);
                }
                else
                {
                    result.AddDownload(transfer);
                }
                RaiseFileTransferredEvent(transfer);
            }
        }

        private static string IncludeTrailingSlash(string path)
        {
            if (!string.IsNullOrEmpty(path) && !path.EndsWith("/", StringComparison.Ordinal))
            {
                path += '/';
            }
            return path;
        }

        private void Cleanup()
        {
            using (Logger.CreateCallstack())
            {
                if (_process != null)
                {
                    Logger.WriteLine("Terminating process");
                    try
                    {
                        try
                        {
                            WriteCommand("exit");
                            _process.Close();
                        }
                        finally
                        {
                            _process.Dispose();
                            _process = null;
                        }
                    }
                    catch (Exception e)
                    {
                        Logger.WriteLine("Process cleanup Exception: {0}", e);
                    }
                }

                Logger.WriteLine("Disposing log readers");

                if (_reader != null)
                {
                    _reader.Dispose();
                    _reader = null;
                }

                if (_logReader != null)
                {
                    _logReader.Dispose();
                    _logReader = null;
                }

                // Cleanup log file
                if ((XmlLogPath != null) && File.Exists(XmlLogPath))
                {
                    Logger.WriteLine("Deleting XML log file [{0}]", XmlLogPath);
                    try
                    {
                        File.Delete(XmlLogPath);
                    }
                    catch (DirectoryNotFoundException e)
                    {
                        Logger.WriteLine("XML log cleanup DirectoryNotFoundException: {0}", e);
                    }
                    catch (IOException e)
                    {
                        Logger.WriteLine("XML log cleanup IOException: {0}", e);
                    }
                    catch (UnauthorizedAccessException e)
                    {
                        Logger.WriteLine("XML log cleanup UnauthorizedAccessException: {0}", e);
                    }

                    _xmlLogPath = null;
                }
            }
        }

        private void WriteCommand(string command)
        {
            WriteCommand(command, command);
        }

        private void WriteCommand(string command, string log)
        {
            Logger.WriteLine("Command: [{0}]", log);
            _process.ExecuteCommand(command, log);
            GotOutput();
        }

        private static void ReadElement(CustomLogReader reader, LogReadFlags flags)
        {
            while (reader.Read(flags))
            {
            }
        }

        private void SessionOptionsToUrlAndSwitches(SessionOptions sessionOptions, bool scanFingerprint, out string command, out string log)
        {
            using (Logger.CreateCallstack())
            {
                if (sessionOptions.WebdavSecure)
                {
                    if (sessionOptions.Protocol != Protocol.Webdav)
                    {
                        throw Logger.WriteException(new ArgumentException("SessionOptions.WebdavSecure is set, but SessionOptions.Protocol is not Protocol.Webdav."));
                    }
                }

                string head;
                switch (sessionOptions.Protocol)
                {
                    case Protocol.Sftp:
                        head = "sftp://";
                        break;

                    case Protocol.Scp:
                        head = "scp://";
                        break;

                    case Protocol.Ftp:
                        head = "ftp://";
                        break;

                    case Protocol.Webdav:
                        if (!sessionOptions.WebdavSecure)
                        {
                            head = "dav://";
                        }
                        else
                        {
                            head = "davs://";
                        }
                        break;

                    default:
                        throw Logger.WriteException(new ArgumentException(string.Format(CultureInfo.CurrentCulture, "{0} is not supported", sessionOptions.Protocol)));
                }

                bool hasUsername;
                if (!scanFingerprint)
                {
                    hasUsername = !string.IsNullOrEmpty(sessionOptions.UserName);
                    if (hasUsername)
                    {
                        head += UriEscape(sessionOptions.UserName);
                    }
                }
                else
                {
                    hasUsername = false;
                }

                string url = head;
                string logUrl = head;

                if ((sessionOptions.SecurePassword != null) && !scanFingerprint)
                {
                    if (!hasUsername)
                    {
                        throw Logger.WriteException(new ArgumentException("SessionOptions.Password is set, but SessionOptions.UserName is not."));
                    }
                    url += ":" + UriEscape(sessionOptions.Password);
                    logUrl += ":***";
                }

                string tail = string.Empty;

                if (hasUsername)
                {
                    tail += "@";
                }

                if (string.IsNullOrEmpty(sessionOptions.HostName))
                {
                    throw Logger.WriteException(new ArgumentException("SessionOptions.HostName is not set."));
                }

                // We should wrap IPv6 literals to square brackets, instead of URL-encoding them,
                // but it works anyway...
                tail += UriEscape(sessionOptions.HostName);

                if (sessionOptions.PortNumber != 0)
                {
                    tail += ":" + sessionOptions.PortNumber.ToString(CultureInfo.InvariantCulture);
                }

                if (!string.IsNullOrEmpty(sessionOptions.WebdavRoot) && !scanFingerprint)
                {
                    if (sessionOptions.Protocol != Protocol.Webdav)
                    {
                        throw Logger.WriteException(new ArgumentException("SessionOptions.WebdavRoot is set, but SessionOptions.Protocol is not Protocol.Webdav."));
                    }

                    tail += sessionOptions.WebdavRoot;
                }

                url += tail;
                logUrl += tail;

                string arguments;
                string logArguments;
                SessionOptionsToSwitches(sessionOptions, scanFingerprint, out arguments, out logArguments);

                const string switchName = "-rawsettings";
                Tools.AddRawParameters(ref arguments, sessionOptions.RawSettings, "-rawsettings");
                Tools.AddRawParameters(ref logArguments, sessionOptions.RawSettings, switchName);

                if (!string.IsNullOrEmpty(arguments))
                {
                    arguments = " " + arguments;
                    logArguments = " " + logArguments;
                }

                // Switches should (and particularly the -rawsettings MUST) come after the URL
                command = "\"" + Tools.ArgumentEscape(url) + "\"" + arguments;
                log = "\"" + Tools.ArgumentEscape(logUrl) + "\"" + logArguments;
            }
        }

        private void SessionOptionsToSwitches(SessionOptions sessionOptions, bool scanFingerprint, out string arguments, out string logArguments)
        {
            using (Logger.CreateCallstack())
            {
                List<string> switches = new List<string>();

                if (!string.IsNullOrEmpty(sessionOptions.SshHostKeyFingerprint) ||
                    (sessionOptions.GiveUpSecurityAndAcceptAnySshHostKey && !scanFingerprint))
                {
                    if (!sessionOptions.IsSsh)
                    {
                        throw Logger.WriteException(new ArgumentException("SessionOptions.SshHostKeyFingerprint or SessionOptions.GiveUpSecurityAndAcceptAnySshHostKey is set, but SessionOptions.Protocol is neither Protocol.Sftp nor Protocol.Scp."));
                    }
                    string sshHostKeyFingerprint = sessionOptions.SshHostKeyFingerprint;
                    if (sessionOptions.GiveUpSecurityAndAcceptAnySshHostKey)
                    {
                        Logger.WriteLine("WARNING! Giving up security and accepting any key as configured");
                        sshHostKeyFingerprint = AddStarToList(sshHostKeyFingerprint);
                    }
                    switches.Add(FormatSwitch("hostkey", sshHostKeyFingerprint));
                }
                else
                {
                    if (sessionOptions.IsSsh && DefaultConfigurationInternal && !scanFingerprint)
                    {
                        throw Logger.WriteException(new ArgumentException("SessionOptions.Protocol is Protocol.Sftp or Protocol.Scp, but SessionOptions.SshHostKeyFingerprint is not set."));
                    }
                }

                if (!string.IsNullOrEmpty(sessionOptions.SshPrivateKeyPath) && !scanFingerprint)
                {
                    if (!sessionOptions.IsSsh)
                    {
                        throw Logger.WriteException(new ArgumentException("SessionOptions.SshPrivateKeyPath is set, but SessionOptions.Protocol is neither Protocol.Sftp nor Protocol.Scp."));
                    }
                    switches.Add(FormatSwitch("privatekey", sessionOptions.SshPrivateKeyPath));
                }

                if (!string.IsNullOrEmpty(sessionOptions.TlsClientCertificatePath) && !scanFingerprint)
                {
                    if (!sessionOptions.IsTls)
                    {
                        throw Logger.WriteException(new ArgumentException("SessionOptions.TlsClientCertificatePath is set, but neither SessionOptions.FtpSecure nor SessionOptions.WebdavSecure is enabled."));
                    }
                    switches.Add(FormatSwitch("clientcert", sessionOptions.TlsClientCertificatePath));
                }

                if (sessionOptions.FtpSecure != FtpSecure.None)
                {
                    if (sessionOptions.Protocol != Protocol.Ftp)
                    {
                        throw Logger.WriteException(new ArgumentException("SessionOptions.FtpSecure is not FtpSecure.None, but SessionOptions.Protocol is not Protocol.Ftp."));
                    }

                    switch (sessionOptions.FtpSecure)
                    {
                        case FtpSecure.Implicit:
                            switches.Add(FormatSwitch("implicit"));
                            break;

                        case FtpSecure.Explicit:
                            switches.Add(FormatSwitch("explicit"));
                            break;

                        default:
                            throw Logger.WriteException(new ArgumentException(string.Format(CultureInfo.CurrentCulture, "{0} is not supported", sessionOptions.FtpSecure)));
                    }
                }

                if ((!string.IsNullOrEmpty(sessionOptions.TlsHostCertificateFingerprint) ||
                     sessionOptions.GiveUpSecurityAndAcceptAnyTlsHostCertificate) &&
                    !scanFingerprint)
                {
                    if (!sessionOptions.IsTls)
                    {
                        throw Logger.WriteException(new ArgumentException("SessionOptions.TlsHostCertificateFingerprint or SessionOptions.GiveUpSecurityAndAcceptAnyTlsHostCertificate is set, but neither SessionOptions.FtpSecure nor SessionOptions.WebdavSecure is enabled."));
                    }
                    string tlsHostCertificateFingerprint = sessionOptions.TlsHostCertificateFingerprint;
                    if (sessionOptions.GiveUpSecurityAndAcceptAnyTlsHostCertificate)
                    {
                        Logger.WriteLine("WARNING! Giving up security and accepting any certificate as configured");
                        tlsHostCertificateFingerprint = AddStarToList(tlsHostCertificateFingerprint);
                    }
                    switches.Add(FormatSwitch("certificate", tlsHostCertificateFingerprint));
                }

                if ((sessionOptions.Protocol == Protocol.Ftp) && !scanFingerprint)
                {
                    switches.Add(FormatSwitch("passive", (sessionOptions.FtpMode == FtpMode.Passive)));
                }

                switches.Add(FormatSwitch("timeout", (int)sessionOptions.Timeout.TotalSeconds));

                List<string> logSwitches = new List<string>(switches);

                if (!string.IsNullOrEmpty(sessionOptions.PrivateKeyPassphrase) && !scanFingerprint)
                {
                    if (string.IsNullOrEmpty(sessionOptions.SshPrivateKeyPath) && string.IsNullOrEmpty(sessionOptions.TlsClientCertificatePath))
                    {
                        throw Logger.WriteException(new ArgumentException("SessionOptions.PrivateKeyPassphrase is set, but neither SessionOptions.SshPrivateKeyPath nor SessionOptions.TlsClientCertificatePath is set."));
                    }
                    switches.Add(FormatSwitch("passphrase", sessionOptions.PrivateKeyPassphrase));
                    logSwitches.Add(FormatSwitch("passphrase", "***"));
                }

                if ((sessionOptions.SecureNewPassword != null) && !scanFingerprint)
                {
                    if (sessionOptions.SecurePassword == null)
                    {
                        throw Logger.WriteException(new ArgumentException("SessionOptions.SecureNewPassword is set, but SessionOptions.SecurePassword is not."));
                    }
                    switches.Add(FormatSwitch("newpassword", sessionOptions.NewPassword));
                    logSwitches.Add(FormatSwitch("newpassword", "***"));
                }

                arguments = string.Join(" ", switches.ToArray());
                logArguments = string.Join(" ", logSwitches.ToArray());
            }
        }

        private static string AddStarToList(string list)
        {
            return (string.IsNullOrEmpty(list) ? string.Empty : list + ";") + "*";
        }

        private RemoteFileInfo DoGetFileInfo(string path)
        {
            using (Logger.CreateCallstack())
            {
                WriteCommand(string.Format(CultureInfo.InvariantCulture, "stat -- \"{0}\"", Tools.ArgumentEscape(path)));

                RemoteFileInfo fileInfo = new RemoteFileInfo();

                using (ElementLogReader groupReader = _reader.WaitForGroupAndCreateLogReader())
                using (ElementLogReader statReader = groupReader.WaitForNonEmptyElementAndCreateLogReader("stat", LogReadFlags.ThrowFailures))
                {
                    while (statReader.Read(0))
                    {
                        string value;
                        if (statReader.GetEmptyElementValue("filename", out value))
                        {
                            string name = value;
                            int p = name.LastIndexOf('/');
                            if (p >= 0)
                            {
                                name = name.Substring(p + 1);
                            }
                            fileInfo.Name = name;
                            fileInfo.FullName = value;
                        }
                        else if (statReader.IsNonEmptyElement("file"))
                        {
                            using (ElementLogReader fileReader = statReader.CreateLogReader())
                            {
                                while (fileReader.Read(0))
                                {
                                    ReadFile(fileInfo, fileReader);
                                }
                            }
                        }
                    }

                    groupReader.ReadToEnd(LogReadFlags.ThrowFailures);
                }

                return fileInfo;
            }
        }

        internal static string FormatSwitch(string key)
        {
            return string.Format(CultureInfo.InvariantCulture, "-{0}", key);
        }

        internal static string FormatSwitch(string key, string value)
        {
            return string.Format(CultureInfo.InvariantCulture, "-{0}=\"{1}\"", key, Tools.ArgumentEscape(value));
        }

        internal static string FormatSwitch(string key, int value)
        {
            return string.Format(CultureInfo.InvariantCulture, "-{0}={1}", key, value.ToString(CultureInfo.InvariantCulture));
        }

        internal static string FormatSwitch(string key, bool value)
        {
            return FormatSwitch(key, (value ? 1 : 0));
        }

        private static string UriEscape(string s)
        {
            return Uri.EscapeDataString(s);
        }

        internal void GotOutput()
        {
            _lastOutput = DateTime.Now;
        }

        private void ProcessOutputDataReceived(object sender, OutputDataReceivedEventArgs e)
        {
            if (e == null)
            {
                Logger.WriteLine("Got incomplete progress output");
            }
            else
            {
                Logger.WriteLine("Scheduling output: [{0}]", e.Data);
                string s = e.Data.TrimEnd(new[] { '\r' });

                lock (Output)
                {
                    Output.InternalAdd(s);
                    if (Output.Count > 1000)
                    {
                        Output.InternalRemoveFirst();
                    }
                    if (e.Error)
                    {
                        _error.InternalAdd(s);
                        if (_error.Count > 1000)
                        {
                            _error.InternalRemoveFirst();
                        }
                    }
                }

                ScheduleEvent(() => RaiseOutputDataReceived(e.Data, e.Error));
            }

            GotOutput();
        }

        private void ScheduleEvent(Action action)
        {
            lock (_events)
            {
                _events.Add(action);
                _eventsEvent.Set();
            }
        }

        internal void CheckForTimeout(string additional = null)
        {
            if (DateTime.Now - _lastOutput > Timeout)
            {
                string message = "Timeout waiting for WinSCP to respond";
                if (additional != null)
                {
                    message += " - " + additional;
                }
                throw Logger.WriteException(new TimeoutException(message));
            }

            if (_aborted)
            {
                throw Logger.WriteException(new SessionLocalException(this, "Aborted."));
            }
        }

        private void RaiseFileTransferredEvent(TransferEventArgs args)
        {
            Logger.WriteLine("FileTransferredEvent: [{0}]", args.FileName);

            if (FileTransferred != null)
            {
                FileTransferred(this, args);
            }
        }

        internal void RaiseFailed(SessionRemoteException e)
        {
            Logger.WriteLine("Failed: [{0}]", e);

            if ((Failed != null) && !_ignoreFailed)
            {
                Failed(this, new FailedEventArgs { Error = e });
            }

            foreach (OperationResultBase operationResult in _operationResults)
            {
                operationResult.AddFailure(e);
            }
        }

        private void CheckNotDisposed()
        {
            if (_disposed)
            {
                throw Logger.WriteException(new InvalidOperationException("Object is disposed"));
            }

            if (_aborted)
            {
                throw Logger.WriteException(new InvalidOperationException("Session was aborted"));
            }
        }

        private void CheckOpened()
        {
            if (!Opened)
            {
                throw Logger.WriteException(new InvalidOperationException("Session is not opened"));
            }
        }

        private void CheckNotOpened()
        {
            if (Opened)
            {
                throw Logger.WriteException(new InvalidOperationException("Session is already opened"));
            }
        }

        private void RaiseOutputDataReceived(string data, bool error)
        {
            Logger.WriteLine("Output: [{0}]", data);

            if (OutputDataReceived != null)
            {
                OutputDataReceived(this, new OutputDataReceivedEventArgs(data, error));
            }
        }

        internal void DispatchEvents(int interval)
        {
            DateTime start = DateTime.Now;
            while (_eventsEvent.WaitOne(interval, false))
            {
                lock (_events)
                {
                    foreach (Action action in _events)
                    {
                        action();
                    }
                    _events.Clear();
                }

                interval -= (int) (DateTime.Now - start).TotalMilliseconds;
                if (interval < 0)
                {
                    break;
                }
                start = DateTime.Now;
            }
        }

        private IDisposable RegisterOperationResult(OperationResultBase operationResult)
        {
            _operationResults.Add(operationResult);
            return new OperationResultGuard(this, operationResult);
        }

        internal void UnregisterOperationResult(OperationResultBase operationResult)
        {
            _operationResults.Remove(operationResult);
        }

        internal bool WantsProgress
        {
            get
            {
                return (_fileTransferProgress != null);
            }
        }

        private IDisposable CreateProgressHandler()
        {
            using (Logger.CreateCallstack())
            {
                _progressHandling++;
                return new ProgressHandler(this);
            }
        }

        internal void DisableProgressHandling()
        {
            using (Logger.CreateCallstack())
            {
                if (_progressHandling <= 0)
                {
                    throw Logger.WriteException(new InvalidOperationException("Progress handling not enabled"));
                }

                // make sure we process all pending progress events
                DispatchEvents(0);

                _progressHandling--;
            }
        }

        internal void ProcessProgress(FileTransferProgressEventArgs args)
        {
            ScheduleEvent(() => Progress(args));
        }

        private void Progress(FileTransferProgressEventArgs args)
        {
            if ((_progressHandling >= 0) && WantsProgress)
            {
                _fileTransferProgress(this, args);

                if (args.Cancel)
                {
                    _process.Cancel();
                }
            }
        }

        private void SetupTempPath()
        {
            using (Logger.CreateCallstack())
            {
                if (!string.IsNullOrEmpty(_xmlLogPath))
                {
                    bool exists = File.Exists(_xmlLogPath);
                    Logger.WriteLine("Configured temporary file: {0} - Exists [{1}]", _xmlLogPath, exists);
                    if (exists)
                    {
                        throw Logger.WriteException(new SessionLocalException(this, string.Format(CultureInfo.CurrentCulture, "Configured temporary file {0} already exists", _xmlLogPath)));
                    }
                }
                else
                {
                    string path = Path.GetTempPath();
                    Logger.WriteLine("Temporary folder: {0}", path);
                    string process = Process.GetCurrentProcess().Id.ToString("X4", CultureInfo.InvariantCulture);
                    string instance = GetHashCode().ToString("X8", CultureInfo.InvariantCulture);
                    string filename;
                    bool exists;
                    do
                    {
                        string uniqueStr = (_logUnique > 0 ? "." + _logUnique.ToString(CultureInfo.InvariantCulture) : string.Empty);
                        ++_logUnique;
                        filename = Path.Combine(path, "wscp" + process + "." + instance + uniqueStr + ".tmp");
                        exists = File.Exists(filename);
                        Logger.WriteLine("Temporary file [{0}] - Exists [{1}]", filename, exists);
                    }
                    while (exists);

                    _xmlLogPath = filename;
                }
            }
        }

        private static string GetTypeLibKey(Type t)
        {
            return "CLSID\\{" + t.GUID.ToString().ToUpperInvariant() + "}\\TypeLib";
        }

        FieldInfo IReflect.GetField(string name, BindingFlags bindingAttr)
        {
            using (Logger.CreateCallstack())
            {
                Logger.WriteLine("Name [{0}]", name);
                FieldInfo result = GetType().GetField(name, bindingAttr);
                Logger.WriteLine("Result [{0}]", result != null ? result.Name : "null");
                return result;
            }
        }

        FieldInfo[] IReflect.GetFields(BindingFlags bindingAttr)
        {
            using (Logger.CreateCallstack())
            {
                FieldInfo[] result = GetType().GetFields(bindingAttr);
                Logger.WriteLine("Fields [{0}]", result.Length);
                return result;
            }
        }

        MemberInfo[] IReflect.GetMember(string name, BindingFlags bindingAttr)
        {
            using (Logger.CreateCallstack())
            {
                Logger.WriteLine("Name [{0}]", name);
                MemberInfo[] result = GetType().GetMember(name, bindingAttr);
                Logger.WriteLine("Result [{0}]", result.Length);
                return result;
            }
        }

        MemberInfo[] IReflect.GetMembers(BindingFlags bindingAttr)
        {
            using (Logger.CreateCallstack())
            {
                MemberInfo[] result = GetType().GetMembers(bindingAttr);
                Logger.WriteLine("Result [{0}]", result.Length);
                return result;
            }
        }

        MethodInfo IReflect.GetMethod(string name, BindingFlags bindingAttr)
        {
            using (Logger.CreateCallstack())
            {
                Logger.WriteLine("Name [{0}]", name);
                MethodInfo result = GetType().GetMethod(name, bindingAttr);
                Logger.WriteLine("Result [{0}]", result != null ? result.Name : "null");
                return result;
            }
        }

        MethodInfo IReflect.GetMethod(string name, BindingFlags bindingAttr, Binder binder, Type[] types, ParameterModifier[] modifiers)
        {
            using (Logger.CreateCallstack())
            {
                Logger.WriteLine("Name [{0}]", name);
                MethodInfo result = GetType().GetMethod(name, bindingAttr, binder, types, modifiers);
                Logger.WriteLine("Result [{0}]", result != null ? result.Name : "null");
                return result;
            }
        }

        MethodInfo[] IReflect.GetMethods(BindingFlags bindingAttr)
        {
            using (Logger.CreateCallstack())
            {
                MethodInfo[] result = GetType().GetMethods(bindingAttr);
                Logger.WriteLine("Result [{0}]", result.Length);
                return result;
            }
        }

        PropertyInfo[] IReflect.GetProperties(BindingFlags bindingAttr)
        {
            using (Logger.CreateCallstack())
            {
                PropertyInfo[] result = GetType().GetProperties(bindingAttr);
                Logger.WriteLine("Result [{0}]", result.Length);
                return result;
            }
        }

        PropertyInfo IReflect.GetProperty(string name, BindingFlags bindingAttr, Binder binder, Type returnType, Type[] types, ParameterModifier[] modifiers)
        {
            using (Logger.CreateCallstack())
            {
                Logger.WriteLine("Name [{0}]", name);
                PropertyInfo result = GetType().GetProperty(name, bindingAttr, binder, returnType, types, modifiers);
                Logger.WriteLine("Result [{0}]", result != null ? result.Name : "null");
                return result;
            }
        }

        PropertyInfo IReflect.GetProperty(string name, BindingFlags bindingAttr)
        {
            using (Logger.CreateCallstack())
            {
                Logger.WriteLine("Name [{0}]", name);
                PropertyInfo result = GetType().GetProperty(name, bindingAttr);
                Logger.WriteLine("Result [{0}]", result != null ? result.Name : "null");
                return result;
            }
        }

        object IReflect.InvokeMember(string name, BindingFlags invokeAttr, Binder binder, object target, object[] args, ParameterModifier[] modifiers, CultureInfo culture, string[] namedParameters)
        {
            using (Logger.CreateCallstack())
            {
                object result;

                try
                {
                    if (Logger.Logging)
                    {
                        Logger.WriteLine("Name [{0}]", name);
                        Logger.WriteLine("BindingFlags [{0}]", invokeAttr);
                        Logger.WriteLine("Binder [{0}]", binder);
                        Logger.WriteLine("Target [{0}]", target);
                        if (args != null)
                        {
                            Logger.WriteLine("Args [{0}] [{1}]", args.Length, modifiers != null ? modifiers.Length.ToString(CultureInfo.InvariantCulture) : "null");
                            for (int i = 0; i < args.Length; ++i)
                            {
                                Logger.WriteLine("Arg [{0}] [{1}] [{1}] [{2}]", i, args[i], (args[i] != null ? args[i].GetType().ToString() : "null"), (modifiers != null ? modifiers[i].ToString() : "null"));
                            }
                        }
                        Logger.WriteLine("Culture [{0}]", culture);
                        if (namedParameters != null)
                        {
                            foreach (string namedParameter in namedParameters)
                            {
                                Logger.WriteLine("NamedParameter [{0}]", namedParameter);
                            }
                        }
                    }

                    if (target == null)
                    {
                        throw Logger.WriteException(new ArgumentNullException("target"));
                    }

                    Type type = target.GetType();

                    // RuntimeType.InvokeMember below calls into Binder.BindToMethod (Binder is OleAutBinder)
                    // that fails to match method, if integer value is provided to enum argument
                    // (SynchronizeDirectories with its SynchronizationMode and SynchronizationCriteria).
                    // This does not happen if we do not implement IReflect, though no idea why.
                    // So as a workaround we check, if the method has no overloads (what is always true for Session),
                    // and call the only instance directly.
                    // Calling MethodInfo.Invoke with int values for enum arguments works.
                    // Only as a fallback, we call InvokeMember (what is currently actually used only when
                    // the method with given name does not exist at all)

                    MethodInfo method = null;
                    PropertyInfo property = null;

                    // would be way too difficult to implement the below involving named arguments
                    if (namedParameters == null)
                    {
                        try
                        {
                            BindingFlags bindingFlags = invokeAttr | BindingFlags.Instance | BindingFlags.Public;
                            method = type.GetMethod(name, bindingFlags);

                            if (args == null)
                            {
                                throw Logger.WriteException(new ArgumentNullException("args"));
                            }

                            if (method != null)
                            {
                                // MethodInfo.Invoke does not fill-in optional arguments (contrary to RuntimeType.InvokeMember)
                                ParameterInfo[] parameters = method.GetParameters();
                                if (args.Length < parameters.Length)
                                {
                                    Logger.WriteLine("Provided less parameters [{0}] than defined [{1}]", args.Length, parameters.Length);
                                    object[] args2 = new object[parameters.Length];

                                    for (int i = 0; i < parameters.Length; i++)
                                    {
                                        if (i < args.Length)
                                        {
                                            args2[i] = args[i];
                                        }
                                        else
                                        {
                                            if (!parameters[i].IsOptional)
                                            {
                                                Logger.WriteLine("Parameter [{0}] of [{1}] is not optional", i, method);
                                                args2 = null;
                                                break;
                                            }
                                            else
                                            {
                                                Logger.WriteLine("Adding default value [{0}] for optional parameter [{1}]", parameters[i].DefaultValue, i);
                                                args2[i] = parameters[i].DefaultValue;
                                            }
                                        }
                                    }

                                    if (args2 != null)
                                    {
                                        args = args2;
                                    }
                                }
                            }
                            else if (args.Length == 1) // sanity check
                            {
                                property = type.GetProperty(name, bindingFlags);
                            }
                        }
                        catch (AmbiguousMatchException e)
                        {
                            Logger.WriteLine("Unexpected ambiguous match [{0}]", e.Message);
                        }
                    }

                    if (method != null)
                    {
                        Logger.WriteLine("Invoking unambiguous method [{0}]", method);
                        result = method.Invoke(target, invokeAttr, binder, args, culture);
                    }
                    else if (property != null)
                    {
                        Logger.WriteLine("Setting unambiguous property [{0}]", property);
                        property.SetValue(target, args[0], invokeAttr, binder, null, culture);
                        result = null;
                    }
                    else
                    {
                        Logger.WriteLine("Invoking ambiguous/non-existing method 2 [{0}]", name);
                        result = type.InvokeMember(name, invokeAttr, binder, target, args, modifiers, culture, namedParameters);
                    }

                    Logger.WriteLine("Result [{0}] [{1}]", result, (result != null ? result.GetType().ToString() : "null"));
                }
                catch (Exception e)
                {
                    Logger.WriteLine("Error [{0}]", e);
                    throw;
                }
                return result;
            }
        }

        Type IReflect.UnderlyingSystemType
        {
            get { return GetType(); }
        }

        internal const string Namespace = "http://winscp.net/schema/session/1.0";
        internal Logger Logger { get; private set; }
        internal bool GuardProcessWithJobInternal { get { return _guardProcessWithJob; } set { CheckNotOpened(); _guardProcessWithJob = value; } }
        internal bool TestHandlesClosedInternal { get; set; }
        internal Dictionary<string, string> RawConfiguration { get; private set; }
        internal bool DefaultConfigurationInternal { get { return _defaultConfiguration; } }
        internal string IniFilePathInternal { get { return _iniFilePath; } }
        internal bool DisableVersionCheckInternal { get { return _disableVersionCheck; } }

        private ExeSessionProcess _process;
        private DateTime _lastOutput;
        private ElementLogReader _reader;
        private SessionLogReader _logReader;
        private readonly IList<OperationResultBase> _operationResults;
        private delegate void Action();
        private readonly IList<Action> _events;
        private AutoResetEvent _eventsEvent;
        private bool _disposed;
        private string _executablePath;
        private string _additionalExecutableArguments;
        private bool _defaultConfiguration;
        private bool _disableVersionCheck;
        private string _iniFilePath;
        private TimeSpan _reconnectTime;
        private string _sessionLogPath;
        private bool _aborted;
        private int _logUnique;
        private string _xmlLogPath;
        private FileTransferProgressEventHandler _fileTransferProgress;
        private int _progressHandling;
        private bool _guardProcessWithJob;
        private string _homePath;
        private string _executableProcessUserName;
        private SecureString _executableProcessPassword;
        private StringCollection _error;
        private bool _ignoreFailed;
    }
}
