using System;
using System.Collections.Generic;
using System.Linq;
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

namespace PatchingWrapper
{
    /// <summary>
    /// Interaction logic for MainWindow.xaml
    /// </summary>
    public partial class MainWindow : Window
    {
        private string ContentEndpoint;
        private Queue<PendingDownload> PendingDownloads;

        List<Thread> DownloadingThreads;
        Thread MonitorThread;

        private long TotalSize = 0;
        private long LastUpdateSize = 0;
        private long DownloadedSize = 0;

        private long NumTotalItems = 0;
        private long NumRemainingItems = 0;

        private readonly double updateInterval = 0.1;

        public MainWindow(string contentEndpoint, IEnumerable<PendingDownload> pendingDownloads)
        {
            InitializeComponent();

            ContentEndpoint = contentEndpoint;
            PendingDownloads = new Queue<PendingDownload>(pendingDownloads);
            NumTotalItems = pendingDownloads.LongCount();
            NumRemainingItems = NumTotalItems;

            Loaded += MainWindow_Loaded;
            Closing += MainWindow_Closing;
        }

        private void MainWindow_Closing(object sender, System.ComponentModel.CancelEventArgs e)
        {
            foreach (Thread t in DownloadingThreads)
            {
                t.Abort();
            }
        }

        private void MainWindow_Loaded(object sender, RoutedEventArgs e)
        {
            foreach (PendingDownload pendingDownload in PendingDownloads)
            {
                TotalSize += pendingDownload.Size;
            }

            DownloadingThreads = new List<Thread>();
            for (int i = 0; i < 5; i++)
            {
                Thread t = new Thread(DownloadRunnable) { IsBackground = true };
                DownloadingThreads.Add(t);
                t.Start();
            }

            MonitorThread = new Thread(MonitorRunnable) { IsBackground = true };
            MonitorThread.Start();
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

        private void MonitorRunnable()
        {
            while (true)
            {
                long dataRate = (long)((DownloadedSize - LastUpdateSize) * updateInterval);
                Dispatcher.Invoke(() =>
                {
                    InfoBox.Text = $"[{NumTotalItems - NumRemainingItems} / {NumTotalItems}] {FormatBytes(DownloadedSize)} / {FormatBytes(TotalSize)} {FormatBytes(dataRate)}/s";
                    PBar.Value = ((double)DownloadedSize / TotalSize) * 100;
                });
                LastUpdateSize = DownloadedSize;
                Thread.Sleep((int)(1000 * updateInterval));
            }
        }

        private void DownloadRunnable()
        {
            Downloader downloader = null;
            try
            {
                while (true)
                {
                    if (PendingDownloads.Count == 0)
                    {
                        return;
                    }

                    PendingDownload pendingDownload;
                    lock (PendingDownloads)
                    {
                        if (!PendingDownloads.Any())
                        {
                            return;
                        }

                        pendingDownload = PendingDownloads.Dequeue();
                    }

                    string url = $"{ContentEndpoint}/{pendingDownload.Path}";
                    downloader = new Downloader(pendingDownload.Path, url);
                    downloader.ProgressUpdated += Downloader_ProgressUpdated;
                    downloader.Download();
                    long numRemainingItems = Interlocked.Decrement(ref NumRemainingItems);
                    if (numRemainingItems == 0)
                    {
                        Dispatcher.Invoke(() =>
                        {
                            Close();
                        });
                    }
                }
            }
            catch (ThreadAbortException)
            {
                if (downloader != null)
                {
                    downloader.AbortDownload();
                }
            }
        }

        private void Downloader_ProgressUpdated(Downloader sender, long step)
        {
            // Debug: speed limit
            if (System.Diagnostics.Debugger.IsAttached)
            {
                double speedThreshold = 0.5 * 1024 * 1024;
                while (((DownloadedSize - LastUpdateSize) * updateInterval) > speedThreshold)
                {
                    Thread.Sleep(10);
                }
            }

            Interlocked.Add(ref DownloadedSize, step);
        }
    }
}
