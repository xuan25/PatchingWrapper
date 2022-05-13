using JsonUtil;
using System.CommandLine;
using System.Security.Cryptography;

namespace PatchingServer
{
    class Program
    {
        static int Main(string[] args)
        {
            // Create a root command with some options

            Option<DirectoryInfo> dirOption = new Option<DirectoryInfo>(
                new string[] { "-d", "--dir" },
                "Path to the content directory")
            {
                IsRequired = true
            };

            Option<FileInfo> configOption = new Option<FileInfo>(
                new string[] { "-c", "--config" },
                "Path to the config file")
            {
                IsRequired = true
            };

            Option<int> portOption = new Option<int>(
                new string[] { "-p", "--port" },
                "Listening port")
            {
                IsRequired = true
            };

            Option<FileInfo> logOption = new Option<FileInfo>(
                new string[] { "-l", "--log" },
                "Path to the log file")
            {
                IsRequired = true
            };

            var rootCommand = new RootCommand
            {
                dirOption,
                configOption,
                portOption,
                logOption,
            };

            rootCommand.Description = "Patching Server";

            rootCommand.SetHandler((DirectoryInfo dir, FileInfo configFile, int port, FileInfo logFile) =>
            {
                Program mainProgram = new Program(dir, configFile);
                mainProgram.Init();
                mainProgram.RunServer(port, logFile);
            }, dirOption, configOption, portOption, logOption);

            // Parse the incoming args and invoke the handler
            return rootCommand.Invoke(args);
        }


        public DirectoryInfo? Dir { get; private set; }
        public FileInfo ConfigFile { get; private set; }

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

        public Program(DirectoryInfo dir, FileInfo configFile)
        {
            IsInited = false;
            Dir = dir;

            ConfigFile = configFile;
        }

        void Init()
        {
            IsInited = false;

            string patcherUrl;
            string contentEndPoint;

            using (FileStream configStream = ConfigFile.Open(FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                Json.Value config = Json.Parser.Parse(configStream);
                patcherUrl = config["patcher_url"];
                contentEndPoint = config["content_end_point"];
            }

            // patcher
            HttpClient httpClient = new HttpClient();
            HttpRequestMessage httpRequest = new HttpRequestMessage(HttpMethod.Get, patcherUrl);
            HttpResponseMessage httpResponse = httpClient.Send(httpRequest);
            string patcherHash;
            using (Stream responseStream = httpResponse.Content.ReadAsStream())
            {
                HashAlgorithm hashAlgorithm = MD5.Create();
                byte[] hashByte = hashAlgorithm.ComputeHash(responseStream);
                patcherHash = BitConverter.ToString(hashByte).Replace("-", "");
            }

            while (true)
            {
                try
                {
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
                                { "url", patcherUrl },
                                { "hash", patcherHash },
                                { "alg", "md5" },
                            }
                        },
                        { "content_end_point", contentEndPoint },
                        { "files", FileDictObject }
                    };

                    ResponseContent = ResponseRootObject.ToString();

                    IsInited = true;
                    ScheduleRefresh();

                    if (refreshThread != null)
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

            if (refreshThread != null)
            {
                refreshThread.Join();
            }

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
                    if (!FileDictObject.Contains(path))
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
            lock (refreshLock)
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

                string[] segments = request.RequestUri.Segments;

                string xForwardFor = null;
                if (request.Headers.ContainsKey("X-Forwarded-For"))
                {
                    xForwardFor = request.Headers["X-Forwarded-For"];
                }

                AppendLog($"[Request] {(xForwardFor == null ? string.Empty : xForwardFor + ", ")}{context.Request.RemoteEndpoint} ({context.Request.Method}) {context.Request.RequestUri}");

                switch (request.RequestUri.AbsolutePath)
                {
                    case "/":
                        try
                        {
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
                        break;
                    case "/reload":
                        Reload();
                        response.StatusCode = 200;
                        response.ReasonPhrase = "OK";
                        response.Close();
                        break;
                    default:
                        response.NotFound();
                        response.Close();
                        break;
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