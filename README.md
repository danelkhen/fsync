fsync
=====

File watcher and synchronizer between local windows file system and a remote ssh linux path

##### Usage
Create an fsync config file, e.g.: yourproject.fsync
```
[Session]
HostName=your-server-hostname
UserName=your-username
Password=Y0urPassw0rd
[FolderPair1]
AutoConnect=false
AutoStartRealTime=true
IncludeSubdirectories=true
LocalDir=C:\your-project\
BackupDir=C:\backup\your-project
RemoteDir=/usr/local/your-project/
```
##### Run
```
fsync.exe yourproject.fsync
```
Or execute the config file directly, using 'open with' fsync.exe

The app will watch your local dir, any changes to files in that dir will be uploaded to the corrosponding remote directory.

Commands are available inside the app, type help to see all, it's possible ot use short names for commands, for example SyncToLocal can be executed also by typing 'stl'. 

While the program is running, you can initiate a manual file synchornization (using winscp) - remote-to-local, or local-to-remote, preview option is available, and delete files options is also available.

##### Dependencies
This project uses a modified version of WinSCP, as well as danelkhen/corex power extensions library, dependencies are included with the source, and project should compile as/is.

