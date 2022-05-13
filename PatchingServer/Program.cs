﻿using JsonUtil;
using System.CommandLine;
using System.Security.Cryptography;

namespace PatchingServer
{
    class Program
    {
        static int Main(string[] args)
        {
            // Create a root command with some options

            var dirOption = new Option<DirectoryInfo>(
                    new string[] { "-d", "--dir" },
                    description: "Directory contains content")
            {
                IsRequired = true
            };

            var patcherUrlOption = new Option<string>(
                    new string[] { "-u", "--patcher-url" },
                    description: "Patcher URL")
            {
                IsRequired = true
            };

            var portOption = new Option<int>(
                    new string[] { "-p", "--port" },
                    description: "The port which the server will be listening on")
            {
                IsRequired = true
            };

            var logOption = new Option<FileInfo>(
                    new string[] { "-l", "--log-file" },
                    "The path to the log file")
            {
                IsRequired = true
            };

            var rootCommand = new RootCommand
            {
                dirOption,
                patcherUrlOption,
                portOption,
                logOption,
            };

            rootCommand.Description = "Patching Server";

            rootCommand.SetHandler((DirectoryInfo dir, string patcherUrl, int port, FileInfo logFile) =>
            {
                Program mainProgram = new Program(dir, patcherUrl);
                mainProgram.Init();
                mainProgram.RunServer(port, logFile);
            }, dirOption, patcherUrlOption, portOption, logOption);

            // Parse the incoming args and invoke the handler
            return rootCommand.Invoke(args);
        }


        public DirectoryInfo? Dir { get; private set; }
        public string PatcherUrl { get; private set; }

        public bool IsInited { get; private set; }

        public Json.Value.Object? ResponseRootObject { get; private set; }
        public Json.Value.Object? FileDictObject { get; private set; }
        public string? ResponseContent { get; private set; }

        public string? LogFilePath { get; private set; }


        private FileSystemWatcher? fileWatcher;
        private FileSystemWatcher? dirWatcher;
        private Queue<Action> updateQueue = new Queue<Action>();
        private int refreshRunning = 0;
        private Thread? refreshThread;
        private object refreshLock = new object();
        readonly object logObj = new object();

        public Program(DirectoryInfo dir, string patcherUrl)
        {
            IsInited = false;
            Dir = dir;
            PatcherUrl = patcherUrl;
        }

        void Init()
        {
            IsInited = false;
            while(true)
            {
                try
                {
                    // patcher

                    System.Net.HttpWebRequest request = (System.Net.HttpWebRequest)System.Net.WebRequest.Create(PatcherUrl);
                    request.Method = "GET";
                    string patcherHash;
                    using (System.Net.HttpWebResponse response = (System.Net.HttpWebResponse)request.GetResponse())
                    {
                        using (Stream dataStream = response.GetResponseStream())
                        {
                            HashAlgorithm hashAlgorithm = MD5.Create();
                            byte[] hashByte = hashAlgorithm.ComputeHash(dataStream);
                            patcherHash = BitConverter.ToString(hashByte).Replace("-", "");
                        }
                    }

                    // content watcher

                    refreshRunning = 0;

                    fileWatcher = new FileSystemWatcher(Dir.FullName);
                    fileWatcher.InternalBufferSize = 65536;
                    fileWatcher.NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.Size;
                    fileWatcher.Created += FileWatcher_Created;
                    fileWatcher.Changed += FileWatcher_Changed;
                    fileWatcher.Deleted += FileWatcher_Deleted;
                    fileWatcher.Renamed += FileWatcher_Renamed;
                    fileWatcher.Error += FileWatcher_Error;
                    fileWatcher.IncludeSubdirectories = true;
                    fileWatcher.EnableRaisingEvents = true;

                    dirWatcher = new FileSystemWatcher(Dir.FullName);
                    dirWatcher.InternalBufferSize = 65536;
                    dirWatcher.NotifyFilter = NotifyFilters.DirectoryName | NotifyFilters.LastWrite | NotifyFilters.Size;
                    dirWatcher.Created += DirWatcher_Created;
                    dirWatcher.Changed += DirWatcher_Changed;
                    dirWatcher.Deleted += DirWatcher_Deleted;
                    dirWatcher.Renamed += DirWatcher_Renamed;
                    dirWatcher.Error += DirWatcher_Error;
                    dirWatcher.IncludeSubdirectories = true;
                    dirWatcher.EnableRaisingEvents = true;

                    AppendLog($"Watching {Dir.FullName}");

                    // fetch content

                    long numFiles = 0;

                    List<string> paths = EnumerateFilesDeep(Dir);

                    FileDictObject = new Json.Value.Object();
                    foreach (string path in paths)
                    {
                        string fullpath = Path.Combine(Dir.FullName, path);
                        long size = new FileInfo(fullpath).Length;
                        string hash = FileHashMD5(fullpath);

                        Json.Value.Object fileObj = new Json.Value.Object()
                        {
                            { "size", size },
                            { "hash", hash },
                            { "alg", "md5" },
                        };
                        FileDictObject.Add(path, fileObj);
                        numFiles++;
                    }

                    ResponseRootObject = new Json.Value.Object()
                    {
                        { "patcher", 
                            new Json.Value.Object()
                            {
                                { "url", PatcherUrl },
                                { "hash", patcherHash },
                                { "alg", "md5" },
                            }
                        },
                        { "files", FileDictObject }
                    };

                    ResponseContent = ResponseRootObject.ToString();

                    IsInited = true;
                    ScheduleRefresh();

                    if(refreshThread != null)
                    {
                        refreshThread.Join();
                    }

                    AppendLog($"{numFiles} files fetched");
                    AppendLog($"{FileDictObject.Count} files indexed");
                    break;
                }
                catch (FileNotFoundException)
                {
                    AppendLog("Files changing rapidly, restart init");
                }
            }
        }
        
        private void Reload()
        {
            if (IsInited == false)
            {
                return;
            }

            IsInited = false;
            AppendLog("Reload");

            refreshThread.Join();

            fileWatcher.EnableRaisingEvents = false;
            dirWatcher.EnableRaisingEvents = false;
            updateQueue.Clear();

            Init();
        }



        private void FileWatcher_Created(object sender, FileSystemEventArgs e)
        {
            Action action = new Action(() =>
            {
                string path = Path.GetRelativePath(Dir.FullName, e.FullPath);
                AppendLog($"Created: {path}");

                long size = new FileInfo(e.FullPath).Length;
                string hash = FileHashMD5(e.FullPath);

                lock (ResponseRootObject)
                {
                    Json.Value.Object fileObj = new Json.Value.Object()
                    {
                        { "size", size },
                        { "hash", hash },
                        { "alg", "md5" },
                    };
                    if(!FileDictObject.Contains(path))
                    {
                        FileDictObject.Add(path, fileObj);
                    } 
                    else
                    {
                        FileDictObject[path] = fileObj;
                    }
                    
                }
            });
            if (action == null) throw new Exception();
            lock (updateQueue)
            {
                updateQueue.Enqueue(action);
            }
            ScheduleRefresh();
        }

        private void FileWatcher_Changed(object sender, FileSystemEventArgs e)
        {
            Action action = new Action(() =>
            {
                if (e.ChangeType != WatcherChangeTypes.Changed)
                {
                    return;
                }
                if (!File.Exists(e.FullPath))
                {
                    return;
                }

                string path = Path.GetRelativePath(Dir.FullName, e.FullPath);
                AppendLog($"Changed: {path}");

                lock (ResponseRootObject)
                {
                    Json.Value.Object fileObj = (Json.Value.Object)FileDictObject[path];
                    long size = new FileInfo(e.FullPath).Length;
                    string hash = FileHashMD5(e.FullPath);
                    fileObj["size"] = size;
                    fileObj["hash"] = hash;
                }
            });
            if (action == null) throw new Exception();
            lock (updateQueue)
            {
                updateQueue.Enqueue(action);
            }
            ScheduleRefresh();
        }

        private void FileWatcher_Deleted(object sender, FileSystemEventArgs e)
        {
            Action action = new Action(() =>
            {
                string path = Path.GetRelativePath(Dir.FullName, e.FullPath);
                AppendLog($"Deleted: {path}");

                lock (ResponseRootObject)
                {
                    FileDictObject.Remove(path);
                }
            });
            if (action == null) throw new Exception();
            lock (updateQueue)
            {
                updateQueue.Enqueue(action);
            }
            ScheduleRefresh();
        }

        private void FileWatcher_Renamed(object sender, RenamedEventArgs e)
        {
            Action action = new Action(() =>
            {
                string oldpath = Path.GetRelativePath(Dir.FullName, e.OldFullPath);
                string path = Path.GetRelativePath(Dir.FullName, e.FullPath);

                AppendLog($"Renamed: {oldpath} -> {path}");

                lock (ResponseRootObject)
                {
                    Json.Value.Object fileObj = (Json.Value.Object)FileDictObject[oldpath];
                    FileDictObject.Remove(oldpath);
                    FileDictObject.Add(path, fileObj);
                }
            });
            if (action == null) throw new Exception();
            lock (updateQueue)
            {
                updateQueue.Enqueue(action);
            }
            ScheduleRefresh();
        }

        private void FileWatcher_Error(object sender, ErrorEventArgs e)
        {
            AppendLog($"FileWatcherError: {e.GetException().Message}");
            
            Reload();
        }

        private void DirWatcher_Created(object sender, FileSystemEventArgs e)
        {
            Action action = new Action(() =>
            {
                string path = Path.GetRelativePath(Dir.FullName, e.FullPath);
                AppendLog($"Dir Created: {path}");
            });
            if (action == null) throw new Exception();
            lock (updateQueue)
            {
                updateQueue.Enqueue(action);
            }
            ScheduleRefresh();
        }

        private void DirWatcher_Changed(object sender, FileSystemEventArgs e)
        {
            Action action = new Action(() =>
            {
                if (e.ChangeType != WatcherChangeTypes.Changed)
                {
                    return;
                }
                if (!Directory.Exists(e.FullPath))
                {
                    return;
                }

                string path = Path.GetRelativePath(Dir.FullName, e.FullPath);
                AppendLog($"Dir Changed: {path}");
            });
            if (action == null) throw new Exception();
            lock (updateQueue)
            {
                updateQueue.Enqueue(action);
            }
            ScheduleRefresh();
        }

        private void DirWatcher_Deleted(object sender, FileSystemEventArgs e)
        {
            Action action = new Action(() => 
            {
                string path = Path.GetRelativePath(Dir.FullName, e.FullPath);
                AppendLog($"Dir Deleted: {path}");

                lock (ResponseRootObject)
                {
                    int count = 0;
                    List<string> paths = new List<string>(FileDictObject.Keys);
                    foreach (string oldFilePath in paths)
                    {
                        if (oldFilePath.StartsWith(path))
                        {
                            FileDictObject.Remove(oldFilePath);
                            count++;
                        }
                    }
                    AppendLog($"{count} indices updated");
                }
            });
            if (action == null) throw new Exception();
            lock (updateQueue)
            {
                updateQueue.Enqueue(action);
            }
            ScheduleRefresh();
        }

        private void DirWatcher_Renamed(object sender, RenamedEventArgs e)
        {
            Action action = new Action(() =>
            {
                string oldpath = Path.GetRelativePath(Dir.FullName, e.OldFullPath);
                string path = Path.GetRelativePath(Dir.FullName, e.FullPath);

                AppendLog($"Dir Renamed: {oldpath} -> {path}");

                lock (ResponseRootObject)
                {
                    int count = 0;
                    List<string> paths = new List<string>(FileDictObject.Keys);

                    foreach (string oldFilePath in paths)
                    {
                        if (oldFilePath.StartsWith(oldpath))
                        {
                            Json.Value.Object fileObj = (Json.Value.Object)FileDictObject[oldFilePath];
                            FileDictObject.Remove(oldFilePath);
                            string newFilePath = Path.Combine(path, Path.GetRelativePath(oldpath, oldFilePath));
                            FileDictObject.Add(newFilePath, fileObj);
                            count++;
                        }
                    }
                    AppendLog($"{count} indices updated");
                }
            });
            if (action == null) throw new Exception();
            lock (updateQueue)
            {
                updateQueue.Enqueue(action);
            } 
            ScheduleRefresh();
        }

        private void DirWatcher_Error(object sender, ErrorEventArgs e)
        {
            AppendLog($"DirWatcherError: {e.GetException().Message}");

            Reload();
        }



        private void ScheduleRefresh()
        {
            if (refreshRunning == 1 || !IsInited || !updateQueue.Any())
            {
                return;
            }
            lock(refreshLock)
            {
                if (refreshRunning == 1)
                {
                    return;
                }

                refreshThread = new Thread(RefreshRunable);
                refreshThread.Start();

                Interlocked.Exchange(ref refreshRunning, 1);
            }
        }

        private void RefreshRunable()
        {
            while (IsInited)
            {
                Action action;
                lock (updateQueue)
                {
                    if (IsInited && !updateQueue.Any())
                    {
                        Thread.Sleep(1000);
                        if (IsInited && !updateQueue.Any())
                        {
                            ResponseContent = ResponseRootObject.ToString();
                            AppendLog("Index refreshed");
                            if (IsInited && !updateQueue.Any())
                            {
                                Interlocked.Exchange(ref refreshRunning, 0);
                                break;
                            }
                        }
                    }
                    action = updateQueue.Dequeue();
                }
                action.Invoke();
            }
        }



        List<string> EnumerateFilesDeep(DirectoryInfo dir, List<string>? paths = null, string prefix = "")
        {
            if (paths == null)
            {
                paths = new List<string>();
            }

            foreach (FileInfo file in dir.EnumerateFiles())
            {
                string filepath = Path.Combine(prefix, file.Name);
                paths.Add(filepath);
            }

            foreach (DirectoryInfo subdir in dir.EnumerateDirectories())
            {
                EnumerateFilesDeep(subdir, paths, Path.Combine(prefix, subdir.Name));
            }

            return paths;
        }

        string FileHashMD5(string path)
        {
            try
            {
                byte[] hashByte = null;
                using (FileStream stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                {
                    var hash = MD5.Create();
                    hashByte = hash.ComputeHash(stream);
                }
                return BitConverter.ToString(hashByte).Replace("-", "");
            }
            catch (IOException)
            {
                return null;
            }
        }



        void RunServer(int port, FileInfo logFile)
        {
            AppendLog($"Port:\t{port}");
            AppendLog($" Log:\t{logFile.FullName}");
            AppendLog("");

            LogFilePath = logFile.FullName;

            HttpListener httpListener = new HttpListener(new System.Net.IPAddress(0), port);
            httpListener.Request += HttpListener_Request;
            httpListener.Start();
            AppendLog($"Start listening on port {port}");

            ManualResetEvent manualResetEvent = new ManualResetEvent(false);
            manualResetEvent.WaitOne();
        }

        void HttpListener_Request(object sender, HttpListenerRequestEventArgs context)
        {
            try
            {
                HttpListenerRequest request = context.Request;
                HttpListenerResponse response = context.Response;

                try
                {
                    string[] segments = request.RequestUri.Segments;

                    string xForwardFor = null;
                    if (request.Headers.ContainsKey("X-Forwarded-For"))
                    {
                        xForwardFor = request.Headers["X-Forwarded-For"];
                    }

                    AppendLog($"[Request] {(xForwardFor == null ? string.Empty : xForwardFor + ", ")}{context.Request.RemoteEndpoint} ({context.Request.Method}) {context.Request.RequestUri}");
                    response.WriteContent(ResponseContent);
                }
                catch (Exception ex)
                {
                    AppendError(ex.ToString());
                    response.InternalServerError();
                }
                finally
                {
                    response.Close();
                }
            }
            catch (Exception ex)
            {
                AppendError(ex.ToString());
            }
        }

        

        void Log(string text)
        {
            if (LogFilePath != null)
            {
                lock (logObj)
                {
                    using (StreamWriter streamWriter = File.AppendText(LogFilePath))
                    {
                        streamWriter.WriteLine(text);
                    }
                }
            }
        }

        void AppendLog(string text)
        {

            string str = $"[{DateTime.Now}] [Info] {text}";
            Console.WriteLine(str);
            Log(str);
        }

        void AppendError(string text)
        {
            string str = $"[{DateTime.Now}] [Error] {text}";
            Console.Error.WriteLine(str);
            Log(str);
        }

    }
}