using JsonUtil;
using System;
using System.Collections.Generic;
using System.Configuration;
using System.Data;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;

namespace PatchingWrapper
{
    

    /// <summary>
    /// Interaction logic for App.xaml
    /// </summary>
    public partial class App : Application
    {
        public string IndexEndPoint = "http://127.0.0.1:7000/";

        public string Exeutable = "AviUtl\\AviUtl中文版.exe";

        public List<Regex> NoVerifyMatchers = new List<Regex>()
        {
            new Regex(".+\\.ini$")
        };

        public static string UpdaterPath { get; private set; } = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "PatcherUpdater.exe");

        protected override void OnStartup(StartupEventArgs e)
        {
            base.OnStartup(e);

            List<string> args = new List<string>(e.Args);

            // update cleanup
            if (File.Exists(UpdaterPath))
            {
                while (IsFileInUse(UpdaterPath))
                {
                    Thread.Sleep(1000);
                }
                File.Delete(UpdaterPath);
            }

            // no startup flag
            bool noStartup = false;
            if (args.Count > 0 && args[0] == "noStartup")
            {
                args.RemoveAt(0);
                noStartup = true;
            }

            // main exec startup and wait for exit
            bool startupAfterDownload = false;
            if (!noStartup)
            {
                if (File.Exists(Exeutable))
                {
                    Process mainProcess = Process.Start(Exeutable, string.Join("", e.Args));
                    mainProcess.WaitForExit();
                    noStartup = true;
                }
                else
                {
                    startupAfterDownload = true;
                }               
            }

            // request meta from remote
            Json.Value metaJson;
            try
            {
                HttpClient httpClient = new HttpClient();
                HttpRequestMessage httpRequest = new HttpRequestMessage(HttpMethod.Get, IndexEndPoint);
                HttpResponseMessage httpResponse = httpClient.SendAsync(httpRequest).Result;

                using (Stream responseStream = httpResponse.Content.ReadAsStreamAsync().Result)
                {
                    metaJson = Json.Parser.Parse(responseStream);
                }
            }
            catch (Exception ex)
            {
                if (startupAfterDownload)
                {
                    System.Text.StringBuilder messageBuilder = new System.Text.StringBuilder();
                    Exception currEx = ex;
                    int idx = 0;
                    while (currEx != null)
                    {
                        messageBuilder.Append(new string(' ', idx * 4));
                        messageBuilder.AppendLine(currEx.Message);
                        currEx = currEx.InnerException;
                        idx++;
                    }

                    MessageBox.Show($"Unable to connect to the remote server: \n\n{messageBuilder}", "Network Error", MessageBoxButton.OK, MessageBoxImage.Error);
                }
                
                Environment.Exit(0);
                return;
            }

            string contentEndpoint = metaJson["content_endpoint"];

            string patcherHash = metaJson["patcher"]["hash"];
            string patcherHashAlg = metaJson["patcher"]["alg"];
            string currPatcherHash = FileHashMD5(Process.GetCurrentProcess().MainModule.FileName);

            // update patcher
            if (patcherHashAlg != "md5" || patcherHash != currPatcherHash)
            {
                string patcherUrl = metaJson["patcher"]["url"];

                try
                {
                    // release updater
                    using (Stream stream = Application.GetResourceStream(new Uri("/Resources/PatcherUpdater.exe", UriKind.Relative)).Stream)
                    {
                        using (FileStream fileStream = new FileStream(UpdaterPath, FileMode.OpenOrCreate, FileAccess.Write))
                        {
                            stream.CopyTo(fileStream);
                        }
                    }

                    // start updater
                    List<string> updaterArgs = new List<string>();
                    updaterArgs.Add($"\"{Process.GetCurrentProcess().MainModule.FileName}\"");
                    updaterArgs.Add($"\"{patcherUrl}\"");
                    if (noStartup)
                    {
                        updaterArgs.Add("noStartup");
                    }
                    updaterArgs.AddRange(args);
                    Process.Start(UpdaterPath, ToCmdArgs(updaterArgs));
                }
                catch (UnauthorizedAccessException ex)
                {
                    if (IsAdmin())
                    {
                        MessageBox.Show($"Unable to access directory: \n\n{ex.Message}", "Permission Error", MessageBoxButton.OK, MessageBoxImage.Error);
                        Environment.Exit(0);
                    }

                    // restart with admin
                    List<string> restartArgs = new List<string>();
                    if (noStartup)
                    {
                        restartArgs.Add("noStartup");
                    }
                    restartArgs.AddRange(args);
                    ProcessStartInfo processStartInfo = new ProcessStartInfo(Process.GetCurrentProcess().MainModule.FileName)
                    {
                        Verb = "runas",
                        Arguments = ToCmdArgs(restartArgs)
                    };
                    Process.Start(processStartInfo);
                }

                Environment.Exit(0);
            }

            // build update index
            List<PendingDownload> pendingDownloads = new List<PendingDownload>();

            Json.Value.Object files = (Json.Value.Object)metaJson["files"];
            foreach (string path in files.Keys)
            {
                Json.Value.Object file = (Json.Value.Object)files[path];
                long size = file["size"];
                string hash = file["hash"];
                string alg = file["alg"];

                // new file
                if (!File.Exists(path))
                {
                    pendingDownloads.Add(new PendingDownload()
                    {
                        Path = path,
                        Size = size,
                    });
                    continue;
                }

                bool noVerify = false;
                foreach (Regex regex in NoVerifyMatchers)
                {
                    if (regex.IsMatch(path))
                    {
                        noVerify = true;
                        break;
                    }
                }

                if(!noVerify || hash == null)
                {
                    // validate file
                    string localHash;
                    switch (alg)
                    {
                        case "md5":
                            // TODO: caching
                            localHash = FileHashMD5(path);
                            break;
                        default:
                            throw new Exception($"Invalid alg {alg}");
                    }

                    if (localHash != hash)
                    {
                        pendingDownloads.Add(new PendingDownload()
                        {
                            Path = path,
                            Size = size,
                        });
                    }
                }
            }

            if (pendingDownloads.Count == 0)
            {
                Environment.Exit(0);
            }

            // validate access
            try
            {
                using (FileStream fileStream = File.Create("temp", 1, FileOptions.DeleteOnClose))
                {

                }
            }
            catch (UnauthorizedAccessException ex)
            {
                if (IsAdmin())
                {
                    MessageBox.Show($"Unable to access directory: \n\n{ex.Message}", "Permission Error", MessageBoxButton.OK, MessageBoxImage.Error);
                    Environment.Exit(0);
                }

                // restart with admin
                ProcessStartInfo processStartInfo = new ProcessStartInfo(Process.GetCurrentProcess().MainModule.FileName)
                {
                    Verb = "runas"
                };
                Process.Start(processStartInfo);
                Environment.Exit(0);
            }
            
            Console.WriteLine($"Update: {pendingDownloads.Count}");

            // do update
            MainWindow = new MainWindow(contentEndpoint, pendingDownloads);

            // start main exec after download
            if (startupAfterDownload)
            {
                MainWindow.Closed += (object sender, EventArgs e1) =>
                {
                    Process.Start(Exeutable, string.Join("", e.Args));
                };
            }

            MainWindow.Show();
        }

        private static bool IsAdmin()
        {
            WindowsIdentity windowsIdentity = WindowsIdentity.GetCurrent();
            WindowsPrincipal windowsPrincipal = new WindowsPrincipal(windowsIdentity);
            return windowsPrincipal.IsInRole(WindowsBuiltInRole.Administrator);
        }
        
        private string FileHashMD5(string path)
        {
            var hash = MD5.Create();
            var stream = new FileStream(path, FileMode.Open, FileAccess.Read);
            byte[] hashByte = hash.ComputeHash(stream);
            stream.Close();
            return BitConverter.ToString(hashByte).Replace("-", "");
        }

        private string ToCmdArgs(IEnumerable<object> args)
        {
            List<string> result = new List<string>();
            foreach (object arg in args)
            {
                string argStr = arg.ToString();
                if (!argStr.Contains(" "))
                {
                    result.Add($"{argStr}");
                }
                else
                {
                    result.Add($"\"{argStr.Replace("\"", "\"\"")}\"");
                }
            }
            return string.Join(" ", result);
        }

        private bool IsFileInUse(string fileName)
        {
            bool inUse = true;
            FileStream fileCheckingStream = null;
            try
            {
                fileCheckingStream = new FileStream(fileName, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
                inUse = false;
            }
            catch
            {

            }
            finally
            {
                if (fileCheckingStream != null)
                    fileCheckingStream.Close();
            }
            return inUse;
        }

    }
}
