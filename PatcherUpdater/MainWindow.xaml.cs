using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Navigation;
using System.Windows.Shapes;

namespace PatcherUpdater
{
    /// <summary>
    /// Interaction logic for MainWindow.xaml
    /// </summary>
    public partial class MainWindow : Window
    {

        Downloader downloader;
        public string Path;
        public string Url;
        public string MainArgs;

        Thread DownloadingThread;
        Thread MonitorThread;

        private Downloader.DownloaderStatus Status;
        private long TotalSize = 0;
        private long LastUpdateSize = 0;
        private long DownloadedSize = 0;

        private readonly double updateInterval = 0.1;

        public MainWindow(string path, string url, string mainArgs)
        {
            InitializeComponent();

            Path = path;
            Url = url;
            MainArgs = mainArgs;

            Loaded += MainWindow_Loaded;
            Closing += MainWindow_Closing;
        }

        private void MainWindow_Closing(object sender, System.ComponentModel.CancelEventArgs e)
        {
            if (downloader != null && downloader.IsRunning)
            {
                downloader.CancelDownload();
            }
        }

        private void MainWindow_Loaded(object sender, RoutedEventArgs e)
        {
            DownloadingThread = new Thread(DownloadRunnable);
            DownloadingThread.Start();

            MonitorThread = new Thread(MonitorRunnable) { IsBackground = true };
            MonitorThread.Start();
        }

        private void DownloadRunnable()
        {
            HttpClient httpClient = new HttpClient();
            downloader = new Downloader(httpClient, Path, Url);
            downloader.ProgressUpdated += Downloader_ProgressUpdated;
            downloader.Download();

            // finished
            if(MainArgs != null)
            {
                Process.Start(Path, MainArgs);
            } 
            else
            {
                Process.Start(Path);
            }
            
            Dispatcher.Invoke(() =>
            {
                Close();
            });
        }

        ManualResetEvent speedThresholdEvent = new ManualResetEvent(true);
        double speedThreshold = 0.5 * 1024 * 1024;

        private void Downloader_ProgressUpdated(Downloader sender, long step)
        {
            // Debug: speed limit
            if (System.Diagnostics.Debugger.IsAttached)
            {
                if (((DownloadedSize - LastUpdateSize) * updateInterval) > speedThreshold)
                {
                    speedThresholdEvent.Reset();
                    speedThresholdEvent.WaitOne();
                }
            }

            Interlocked.Exchange(ref TotalSize, sender.Length);
            Interlocked.Add(ref DownloadedSize, step);
            Status = sender.Status;
        }

        private void MonitorRunnable()
        {
            while (true)
            {
                long stepSize = (DownloadedSize - LastUpdateSize) / 10;
                double progress = ((double)DownloadedSize / TotalSize) * 100;
                Dispatcher.Invoke(() =>
                {
                    switch (Status)
                    {
                        case Downloader.DownloaderStatus.Initializing:
                            InfoBox.Text = "Initializing...";
                            break;
                        case Downloader.DownloaderStatus.Waiting:
                            InfoBox.Text = "Waiting for closing...";
                            break;
                        case Downloader.DownloaderStatus.Downloading:
                            InfoBox.Text = $"{progress:0.0}% {FormatBytes(DownloadedSize)} / {FormatBytes(TotalSize)} {FormatBytes(stepSize)}/s"; ;
                            break;
                        case Downloader.DownloaderStatus.Finishing:
                            InfoBox.Text = "Finishing...";
                            break;
                        case Downloader.DownloaderStatus.Finished:
                            InfoBox.Text = "Finished!!!";
                            break;
                    }
                    PBar.Value = ((double)DownloadedSize / TotalSize) * 100;
                });
                LastUpdateSize = DownloadedSize;

                speedThresholdEvent.Set();
                Thread.Sleep((int)(1000 * updateInterval));
            }
        }

        private string FormatBytes(long bytes)
        {
            if (bytes < 1024)
                return $"{bytes:0.0} Byte";
            else if (bytes < 1024 * 1024)
                return $"{(double)bytes / 1024:0.0} KB";
            else
                return $"{(double)bytes / (1024 * 1024):0.0} MB";
        }
    }
}
