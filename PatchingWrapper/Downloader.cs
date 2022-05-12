using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
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

        public string Path;
        public string Url;
        public bool IsRunning;
        public FileStream OutputFileStream;
        public long Position;
        public long Length;

        public Downloader(string path, string url)
        {
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
            ServicePointManager.SecurityProtocol = SecurityProtocolType.Tls12;
            OutputFileStream = new FileStream(filepath, FileMode.Append);
            Position = OutputFileStream.Position;
            do
            {
                HttpWebRequest request = (HttpWebRequest)WebRequest.Create(downloadUrl);
                request.Method = "GET";
                // resume from interrupt
                if (Length > 0)
                    request.AddRange(Position);
                try
                {
                    using (HttpWebResponse response = (HttpWebResponse)request.GetResponse())
                    {
                        if (Length < 0)
                            Length = response.ContentLength;
                        using (Stream dataStream = response.GetResponseStream())
                        {
                            long copied = 0;
                            byte[] buffer = new byte[1024 * 1024 * 10];
                            while (copied != response.ContentLength)
                            {
                                int size = dataStream.Read(buffer, 0, (int)buffer.Length);
                                OutputFileStream.Write(buffer, 0, size);
                                copied += size;
                                Position += size;
                                ProgressUpdated?.Invoke(this, size);
                            }
                        }
                    }
                }
                catch (WebException)
                {

                }
                catch (IOException)
                {

                }
            } while (Length < 0 || Position != Length);
            OutputFileStream.Close();
        }
    }
}
