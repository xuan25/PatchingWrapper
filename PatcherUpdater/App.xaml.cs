using System;
using System.Collections.Generic;
using System.Configuration;
using System.Data;
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
