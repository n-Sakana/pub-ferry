using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;

namespace Ferry
{
    public static class Program
    {
        public static int Main(string[] args)
        {
            return Run(AppDomain.CurrentDomain.BaseDirectory, args);
        }

        public static int Run(string appDirectory, string[] args)
        {
            Console.OutputEncoding = new UTF8Encoding(false);

            AppOptions options;
            try
            {
                options = AppOptions.Parse(args);
            }
            catch (ArgumentException exception)
            {
                Console.Error.WriteLine(exception.Message);
                Console.Error.WriteLine("Use --help to see the available options.");
                return 2;
            }

            if (options.ShowHelp)
            {
                AppOptions.WriteHelp(Console.Out);
                return 0;
            }

            AppState state;
            try
            {
                StaticAssets.Initialize(appDirectory);
                state = new AppState(options.InitialPath);
            }
            catch (ArgumentException exception)
            {
                Console.Error.WriteLine(exception.Message);
                return 2;
            }
            catch (DirectoryNotFoundException exception)
            {
                Console.Error.WriteLine(exception.Message);
                return 2;
            }
            catch (IOException exception)
            {
                Console.Error.WriteLine(exception.Message);
                return 2;
            }

            using (var server = new LocalServer(options.Port, state, options.InitialMode))
            {
                Uri appUri;
                try
                {
                    appUri = server.Start();
                }
                catch (Exception exception)
                {
                    Console.Error.WriteLine(
                        "Ferry could not listen on localhost:{0}: {1}",
                        options.Port,
                        exception.Message);
                    return 1;
                }

                Console.WriteLine("Ferry is listening at {0}", appUri);
                Console.WriteLine("Folder: {0}", state.FolderPath);
                Console.WriteLine("Press Ctrl+C to stop.");

                if (!options.NoBrowser)
                {
                    TryOpenBrowser(appUri);
                }

                using (var shutdown = new CancellationTokenSource())
                {
                    ConsoleCancelEventHandler handler = delegate (object sender, ConsoleCancelEventArgs eventArgs)
                    {
                        eventArgs.Cancel = true;
                        shutdown.Cancel();
                    };
                    Console.CancelKeyPress += handler;

                    try
                    {
                        server.RunAsync(shutdown.Token).GetAwaiter().GetResult();
                    }
                    catch (OperationCanceledException)
                    {
                        // Normal Ctrl+C shutdown.
                    }
                    finally
                    {
                        Console.CancelKeyPress -= handler;
                    }
                }
            }

            return 0;
        }

        private static void TryOpenBrowser(Uri uri)
        {
            try
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = uri.AbsoluteUri,
                    UseShellExecute = true
                });
            }
            catch (Exception exception)
            {
                Console.Error.WriteLine(
                    "The browser did not open automatically: {0}",
                    exception.Message);
                Console.Error.WriteLine("Open {0} manually.", uri);
            }
        }
    }
}
