using JsonUtil;
using System;
using System.Collections.Generic;
using System.Configuration;
using System.Data;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Windows;

namespace PatchingWrapper
{
    

    /// <summary>
    /// Interaction logic for App.xaml
    /// </summary>
    public partial class App : Application
    {
        public string PatcherVer = "202205111";
        public string IndexEndPoint = "http://127.0.0.1:7000/";
        public string DownloadEndPoint = "http://127.0.0.1:5500/content";

        public string Exeutable = "AviUtl\\AviUtl中文版.exe";

        public List<Regex> NoVerifyMatchers = new List<Regex>()
        {
            new Regex(".+\\.ini$")
        };

        public static string UpdaterPath { get; private set; } = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "PatcherUpdater.exe");

        string FileHashMD5(string path)
        {
            var hash = MD5.Create();
            var stream = new FileStream(path, FileMode.Open, FileAccess.Read);
            byte[] hashByte = hash.ComputeHash(stream);
            stream.Close();
            return BitConverter.ToString(hashByte).Replace("-", "");
        }

        public void RunUpdater(string url)
        {
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
                Process.Start(UpdaterPath, $"\"{Process.GetCurrentProcess().MainModule.FileName}\", \"{url}\"");
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
            }
        }

        protected override void OnStartup(StartupEventArgs e)
        {
            base.OnStartup(e);

            bool firstStartup = false;
            if (File.Exists(Exeutable))
            {
                Process mainProcess = Process.Start(Exeutable, string.Join("", e.Args));
                mainProcess.WaitForExit();
            } 
            else
            {
                firstStartup = true;
            }

            Json.Value jsonRes;
            try
            {
                HttpWebRequest metaReq = (HttpWebRequest)WebRequest.Create(IndexEndPoint);
                HttpWebResponse metaRes = (HttpWebResponse)metaReq.GetResponse();

                using (Stream streamRes = metaRes.GetResponseStream())
                {
                    jsonRes = Json.Parser.Parse(streamRes);
                }
            }
            catch (WebException ex)
            {
                MessageBox.Show($"Unable to connect to the remote server: \n\n{ex.Message}", "Network Error", MessageBoxButton.OK, MessageBoxImage.Error);
                Environment.Exit(0);
                return;
            }

            string patcherHash = jsonRes["patcher"]["hash"];
            string patcherHashAlg = jsonRes["patcher"]["alg"];
            string currPatcherHash = FileHashMD5(Process.GetCurrentProcess().MainModule.FileName);

            if (patcherHashAlg != "md5" || patcherHash != currPatcherHash)
            {
                // Update patcher
                string patcherUrl = jsonRes["patcher"]["url"];
                RunUpdater(patcherUrl);
                Environment.Exit(0);
            }

            List<PendingDownload> pendingDownloads = new List<PendingDownload>();

            Json.Value.Object files = (Json.Value.Object)jsonRes["files"];
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

            // Access
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

            MainWindow = new MainWindow(DownloadEndPoint, pendingDownloads);

            if (firstStartup)
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
    }
}
