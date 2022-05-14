using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace PatchingWrapper
{
    internal class Downloader
    {
        public enum DownloaderStatus { Initializing, Waiting, Downloading, Finishing, Finished };
        public DownloaderStatus Status { get; set; }

        public delegate void ProgressUpdatedHandler(Downloader sender, long step);
        public event ProgressUpdatedHandler ProgressUpdated;

        private FileStream CheckingFileStream;

        public HttpClient Client { get; private set; }
        public string Path;
        public string Url;
        public bool IsRunning;
        public FileStream OutputFileStream;
        public long Position;
        public long Length;

        public Downloader(HttpClient httpClient, string path, string url)
        {
            Client = httpClient;
            Path = path;
            Url = url;
            IsRunning = false;
            Position = 0;
            Length = -1;

            FileInfo fileInfo = new FileInfo(path);
            if (!fileInfo.Directory.Exists)
            {
                fileInfo.Directory.Create();
            }
            if (File.Exists(Path + ".temp"))
                File.Delete(Path + ".temp");
        }

        public void CancelDownload()
        {
            AbortDownload();
            if (File.Exists(Path + ".temp"))
                File.Delete(Path + ".temp");
        }

        public void AbortDownload()
        {
            if (OutputFileStream != null)
                OutputFileStream.Close();
            if (CheckingFileStream != null)
                CheckingFileStream.Close();
            IsRunning = false;
        }

        public bool IsFileInUse(string fileName)
        {
            bool inUse = true;
            try
            {
                CheckingFileStream = new FileStream(fileName, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
                inUse = false;
            }
            catch
            {

            }
            finally
            {
                if (CheckingFileStream != null)
                    CheckingFileStream.Close();
            }
            return inUse;
        }

        public void Download()
        {
            Status = DownloaderStatus.Initializing;
            ProgressUpdated?.Invoke(this, 0);
            string downloadUrl = Url;

            Status = DownloaderStatus.Downloading;
            ProgressUpdated?.Invoke(this, 0);
            Download(Path + ".temp", downloadUrl);

            Status = DownloaderStatus.Waiting;
            ProgressUpdated?.Invoke(this, 0);
            while (File.Exists(Path) && IsFileInUse(Path))
            {
                Thread.Sleep(1000);
            }

            Status = DownloaderStatus.Finishing;
            ProgressUpdated?.Invoke(this, 0);
            if (File.Exists(Path))
                File.Delete(Path);
            File.Move(Path + ".temp", Path);

            Status = DownloaderStatus.Finished;
            ProgressUpdated?.Invoke(this, 0);
            IsRunning = false;
        }

        private void Download(string filepath, string downloadUrl)
        {
            OutputFileStream = new FileStream(filepath, FileMode.Append);
            Position = OutputFileStream.Position;

            do
            {
                HttpRequestMessage httpRequest = new HttpRequestMessage(HttpMethod.Get, downloadUrl);

                // resume from interrupt
                if (Length > 0)
                {
                    httpRequest.Headers.Range = new RangeHeaderValue(Position, null);
                }

                try
                {
                    HttpResponseMessage httpResponse = Client.SendAsync(httpRequest).Result;
                    long contentLength = (long)httpResponse.Content.Headers.ContentLength;
                    if (Length < 0)
                    {
                        Length = contentLength;
                    }
                    using (Stream responseStream = httpResponse.Content.ReadAsStreamAsync().Result)
                    {
                        long copied = 0;
                        byte[] buffer = new byte[1024];
                        while (copied != contentLength)
                        {
                            int size = responseStream.Read(buffer, 0, buffer.Length);
                            OutputFileStream.Write(buffer, 0, size);
                            copied += size;
                            Position += size;
                            ProgressUpdated?.Invoke(this, size);
                        }
                    }
                }
                catch (Exception ex)
                {

                }
            } while (Length < 0 || Position != Length);
            OutputFileStream.Close();
        }
    }
}
