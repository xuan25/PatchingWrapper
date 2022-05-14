using System;
using System.Collections.Generic;
using System.Configuration;
using System.Data;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;

namespace PatcherUpdater
{
    /// <summary>
    /// Interaction logic for App.xaml
    /// </summary>
    public partial class App : Application
    {
        public App()
        {
            this.DispatcherUnhandledException += (object sender, System.Windows.Threading.DispatcherUnhandledExceptionEventArgs e) =>
            {
                Exception ex = e.Exception;
                MessageBox.Show("An unexpected problem has occourred. \r\nSome operation has been terminated.\r\n\r\n" + string.Format("Captured an unhandled exception：\r\n{0}\r\n\r\nException Message：\r\n{1}\r\n\r\nException StackTrace：\r\n{2}", ex.GetType(), ex.Message, ex.StackTrace), "Some operation has been terminated.", MessageBoxButton.OK, MessageBoxImage.Warning);
                e.Handled = true;
            };

            AppDomain.CurrentDomain.UnhandledException += (object sender, UnhandledExceptionEventArgs e) =>
            {
                Exception ex = (Exception)e.ExceptionObject;
                MessageBox.Show("An unexpected and unrecoverable problem has occourred. \r\nThe software will now crash.\r\n\r\n" + string.Format("Captured an unhandled exception：\r\n{0}\r\n\r\nException Message：\r\n{1}\r\n\r\nException StackTrace：\r\n{2}", ex.GetType(), ex.Message, ex.StackTrace), "The software will now crash.", MessageBoxButton.OK, MessageBoxImage.Error);
                if (!Debugger.IsAttached)
                {
                    Environment.Exit(1);
                }
            };
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

        protected override void OnStartup(StartupEventArgs e)
        {
            base.OnStartup(e);

            List<string> args = new List<string>(e.Args);

            string path = args[0];
            args.RemoveAt(0);
            string url = args[0];
            args.RemoveAt(0);

            string mainArgs = null;
            if(args.Count > 0)
            {
                mainArgs = ToCmdArgs(args);
            }

            MainWindow = new MainWindow(path, url, mainArgs);
            MainWindow.Show();
        }
    }
}
