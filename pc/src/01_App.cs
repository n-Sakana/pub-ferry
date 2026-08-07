using System;
using System.IO;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Windows;
using Microsoft.Web.WebView2.Core;

namespace PubTransfer
{
    // Entry point. Its whole job is to fail understandably: by the time
    // MainWindow runs, WebView2 is known to be present and the assemblies are
    // known to be loadable, so a failure after this point is a real bug rather
    // than a missing prerequisite.
    public static class App
    {
        public const string ProductName = "Pub Transfer";
        public static string BaseDir;

        [STAThread]
        public static void Run(string baseDir)
        {
            BaseDir = Path.GetFullPath(baseDir);

            string libDir = Path.Combine(BaseDir, "lib");
            AppDomain.CurrentDomain.AssemblyResolve += delegate(
                object sender,
                ResolveEventArgs args)
            {
                string wanted = new AssemblyName(args.Name).Name;

                // pub-transfer.ps1 loads the WebView2 assemblies from their bytes so
                // that a Mark of the Web on a downloaded copy cannot block the
                // load. That leaves them outside the default binding context,
                // so hand back the copy already loaded rather than loading a
                // second one — the same type existing twice is a cast failure
                // with a baffling message.
                Assembly[] loaded = AppDomain.CurrentDomain.GetAssemblies();
                for (int i = 0; i < loaded.Length; i++)
                {
                    if (string.Equals(
                        loaded[i].GetName().Name,
                        wanted,
                        StringComparison.OrdinalIgnoreCase))
                    {
                        return loaded[i];
                    }
                }

                string candidate = Path.Combine(libDir, wanted + ".dll");
                if (File.Exists(candidate))
                {
                    return Assembly.Load(File.ReadAllBytes(candidate));
                }
                return null;
            };

            if (!IsWebView2Available())
            {
                ShowStartupFailure(
                    "WebView2 ランタイムが見つかりません。",
                    "Microsoft Edge WebView2 ランタイムを入れてから、もう一度起動してください。" +
                        Environment.NewLine +
                        "https://developer.microsoft.com/microsoft-edge/webview2/",
                    null);
                return;
            }

            Exception failure = null;
            Thread thread = new Thread(delegate()
            {
                try
                {
                    Application application = new Application();
                    application.ShutdownMode = ShutdownMode.OnMainWindowClose;
                    application.Run(new MainWindow());
                }
                catch (Exception error)
                {
                    failure = error;
                }
            });
            thread.SetApartmentState(ApartmentState.STA);
            thread.Start();
            thread.Join();

            if (failure != null)
            {
                ShowStartupFailure(
                    "画面を開けませんでした。",
                    "ログを見てください: " + HostServices.LogDirectory(),
                    failure);
            }
        }

        public static void ShowStartupFailure(string title, string advice, Exception error)
        {
            StringBuilder message = new StringBuilder();
            message.AppendLine(title);
            message.AppendLine();
            message.AppendLine(advice);
            if (error != null)
            {
                message.AppendLine();
                message.AppendLine(error.Message);
            }
            try
            {
                HostServices.WriteStartupLog(title + " " + (error == null ? "" : error.ToString()));
            }
            catch
            {
            }
            MessageBox.Show(
                message.ToString(),
                ProductName,
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }

        private static bool IsWebView2Available()
        {
            try
            {
                string version = CoreWebView2Environment.GetAvailableBrowserVersionString();
                return !string.IsNullOrEmpty(version);
            }
            catch
            {
                return false;
            }
        }
    }
}
