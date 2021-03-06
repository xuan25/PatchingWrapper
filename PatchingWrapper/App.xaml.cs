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
using System.Windows.Navigation;

namespace PatchingWrapper
{
    

    /// <summary>
    /// Interaction logic for App.xaml
    /// </summary>
    public partial class App : Application
    {

        public App() : base()
        {
            this.DispatcherUnhandledException += (object sender, System.Windows.Threading.DispatcherUnhandledExceptionEventArgs e) =>
            {
                Exception ex = e.Exception;
                MessageBox.Show("An unexpected problem has occourred. \r\nSome operation has been terminated.\r\n\r\n" + string.Format("Captured an unhandled exception: \r\n{0}\r\n\r\nException Message: \r\n{1}\r\n\r\nException StackTrace: \r\n{2}", ex.GetType(), ex.Message, ex.StackTrace), "Some operation has been terminated.", MessageBoxButton.OK, MessageBoxImage.Warning);
                e.Handled = true;
            };

            AppDomain.CurrentDomain.UnhandledException += (object sender, UnhandledExceptionEventArgs e) =>
            {
                Exception ex = (Exception)e.ExceptionObject;
                MessageBox.Show("An unexpected and unrecoverable problem has occourred. \r\nThe software will now crash.\r\n\r\n" + string.Format("Captured an unhandled exception: \r\n{0}\r\n\r\nException Message: \r\n{1}\r\n\r\nException StackTrace: \r\n{2}", ex.GetType(), ex.Message, ex.StackTrace), "The software will now crash.", MessageBoxButton.OK, MessageBoxImage.Error);
                if (!Debugger.IsAttached)
                {
                    Environment.Exit(1);
                }
            };
        }

        public static string UpdaterPath { get; private set; } = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "PatcherUpdater.exe");

        private void Application_Startup(object sender, StartupEventArgs e)
        {
            string indexEndpoint = (string)Application.Current.Resources["IndexEndpoint"];

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

            // get executable from config
            string executable = null;
            if(File.Exists("config.json"))
            {
                using (FileStream configStream = new FileStream("config.json", FileMode.Open, FileAccess.Read))
                {
                    Json.Value config = Json.Parser.Parse(configStream);
                    executable = config["executable"];
                }
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
                if (executable != null && File.Exists(executable))
                {
                    Process mainProcess = Process.Start(Path.GetFullPath(executable), ToCmdArgs(args));
                    mainProcess.WaitForExit();
                    noStartup = true;
                }
                else
                {
                    startupAfterDownload = true;
                }               
            }

            // request meta from remote
            HttpClient httpClient = new HttpClient();
            Json.Value metaJson;
            try
            {
                HttpRequestMessage httpRequest = new HttpRequestMessage(HttpMethod.Get, indexEndpoint);
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

            // update executable from remote
            executable = metaJson["executable"];
            using(StreamWriter configWriter = new StreamWriter(new FileStream("config.json", FileMode.Create, FileAccess.Write)))
            {
                Json.Value.Object config = new Json.Value.Object()
                {
                    { "executable", executable }
                };
                configWriter.WriteLine(config.ToString());
            }


            string contentEndpoint = metaJson["content_endpoint"];

            string patcherHash = metaJson["patcher"]["hash"];
            string patcherHashAlg = metaJson["patcher"]["alg"];
            string currPatcherHash = FileHashMD5(Process.GetCurrentProcess().MainModule.FileName);

            if(patcherHash == null)
            {
                if (startupAfterDownload)
                {
                    MessageBox.Show($"Unable to fetch patcher info from the remote", "Patcher Error", MessageBoxButton.OK, MessageBoxImage.Error);
                }
                Environment.Exit(0);
                return;
            }

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

            // build verify exclusion
            List<Regex> verifyExclusion = new List<Regex>();
            foreach (Json.Value ve in metaJson["verify_exclusion"])
            {
                verifyExclusion.Add(new Regex(".+\\.ini$"));
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
                foreach (Regex regex in verifyExclusion)
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
            MainWindow mainWindow = new MainWindow(httpClient, contentEndpoint, pendingDownloads);
            MainWindow = mainWindow;

            // start main exec after download
            if (startupAfterDownload)
            {
                mainWindow.Closed += (object sender1, EventArgs e1) =>
                {
                    if (!mainWindow.Canceled)
                    {
                        Process.Start(Path.GetFullPath(executable), ToCmdArgs(args));
                    }
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
