using System;
using System.IO;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Windows;
using Microsoft.Web.WebView2.Core;

namespace Ferry
{
    public static class DesktopApp
    {
        public static string BaseDirectory { get; private set; }

        [STAThread]
        public static void Run(
            string baseDirectory,
            Uri appUri,
            CancellationToken shutdownToken,
            Action requestShutdown)
        {
            if (appUri == null)
            {
                throw new ArgumentNullException("appUri");
            }
            if (requestShutdown == null)
            {
                throw new ArgumentNullException("requestShutdown");
            }

            BaseDirectory = Path.GetFullPath(baseDirectory);
            string libraryDirectory = Path.Combine(BaseDirectory, "lib");
            AppDomain.CurrentDomain.AssemblyResolve += delegate(object sender, ResolveEventArgs args)
            {
                string assemblyName = new AssemblyName(args.Name).Name;

                // ferry.ps1 loads the WebView2 assemblies from bytes so Mark
                // of the Web cannot block them. Reuse those loaded copies.
                Assembly[] loaded = AppDomain.CurrentDomain.GetAssemblies();
                for (int index = 0; index < loaded.Length; index++)
                {
                    if (string.Equals(
                        loaded[index].GetName().Name,
                        assemblyName,
                        StringComparison.OrdinalIgnoreCase))
                    {
                        return loaded[index];
                    }
                }

                string assemblyPath = Path.Combine(
                    libraryDirectory,
                    assemblyName + ".dll");
                if (File.Exists(assemblyPath))
                {
                    return Assembly.Load(File.ReadAllBytes(assemblyPath));
                }

                return null;
            };

            if (!IsWebView2Available())
            {
                ShowStartupMessage(
                    "Microsoft Edge WebView2 Runtime が見つかりません。Ferry を起動するには WebView2 Runtime が必要です。",
                    null);
                return;
            }

            Exception startupError = null;
            Thread thread = new Thread(delegate()
            {
                try
                {
                    Application application = new Application();
                    application.ShutdownMode = ShutdownMode.OnMainWindowClose;
                    application.Run(new MainWindow(
                        appUri,
                        shutdownToken,
                        requestShutdown));
                }
                catch (Exception exception)
                {
                    startupError = exception;
                }
            });

            thread.SetApartmentState(ApartmentState.STA);
            thread.Start();
            thread.Join();

            if (startupError != null)
            {
                ShowStartupMessage(
                    "Ferry の画面を開けませんでした。",
                    startupError);
            }
        }

        public static void ShowStartupMessage(string message, Exception error)
        {
            WriteStartupError(message, error);
            if (error != null)
            {
                message = message + Environment.NewLine + Environment.NewLine +
                    error.Message;
            }

            MessageBox.Show(
                message,
                "Ferry",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }

        private static void WriteStartupError(string message, Exception error)
        {
            try
            {
                string localData = Environment.GetFolderPath(
                    Environment.SpecialFolder.LocalApplicationData);
                if (string.IsNullOrWhiteSpace(localData))
                {
                    return;
                }

                string logDirectory = Path.Combine(localData, "Ferry", "logs");
                Directory.CreateDirectory(logDirectory);
                string logPath = Path.Combine(
                    logDirectory,
                    "ferry_" + DateTime.Now.ToString("yyyyMMdd") + ".log");
                string detail = error == null ? message : error.ToString();
                string line = string.Format(
                    "[{0:HH:mm:ss}] [ERROR] desktop startup error: {1}{2}",
                    DateTime.Now,
                    detail,
                    Environment.NewLine);
                File.AppendAllText(
                    logPath,
                    line,
                    new UTF8Encoding(false));
            }
            catch
            {
            }
        }

        private static bool IsWebView2Available()
        {
            try
            {
                string version =
                    CoreWebView2Environment.GetAvailableBrowserVersionString();
                return !string.IsNullOrEmpty(version);
            }
            catch
            {
                return false;
            }
        }
    }
}
