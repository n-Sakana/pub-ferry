using System;
using System.IO;

namespace Ferry
{
    internal sealed class AppOptions
    {
        public const int DefaultPort = 18422;

        private AppOptions(int port, bool noBrowser, bool showHelp, string initialPath, string initialMode)
        {
            Port = port;
            NoBrowser = noBrowser;
            ShowHelp = showHelp;
            InitialPath = initialPath;
            InitialMode = initialMode;
        }

        public int Port { get; private set; }
        public bool NoBrowser { get; private set; }
        public bool ShowHelp { get; private set; }
        public string InitialPath { get; private set; }
        public string InitialMode { get; private set; }

        public static AppOptions Parse(string[] args)
        {
            var port = DefaultPort;
            var noBrowser = false;
            var showHelp = false;
            string initialPath = null;
            string initialMode = null;

            if (args == null)
            {
                args = new string[0];
            }

            for (var index = 0; index < args.Length; index++)
            {
                var argument = args[index];
                if (argument == "--help" || argument == "-h")
                {
                    showHelp = true;
                }
                else if (argument == "--no-browser")
                {
                    noBrowser = true;
                }
                else if (argument == "--port")
                {
                    port = ParsePort(NextValue(args, ref index, argument));
                }
                else if (argument == "--path")
                {
                    initialPath = NextValue(args, ref index, argument);
                }
                else if (argument == "--mode")
                {
                    initialMode = ParseMode(NextValue(args, ref index, argument));
                }
                else if (argument.StartsWith("--port=", StringComparison.Ordinal))
                {
                    port = ParsePort(argument.Substring(7));
                }
                else if (argument.StartsWith("--path=", StringComparison.Ordinal))
                {
                    initialPath = argument.Substring(7);
                }
                else if (argument.StartsWith("--mode=", StringComparison.Ordinal))
                {
                    initialMode = ParseMode(argument.Substring(7));
                }
                else
                {
                    throw new ArgumentException(string.Format("Unknown option: {0}", argument));
                }
            }

            return new AppOptions(port, noBrowser, showHelp, initialPath, initialMode);
        }

        public static void WriteHelp(TextWriter writer)
        {
            writer.WriteLine("Ferry");
            writer.WriteLine();
            writer.WriteLine("Usage: ferry [options]");
            writer.WriteLine();
            writer.WriteLine("  --port <number>             Local HTTP port (default: {0})", DefaultPort);
            writer.WriteLine("  --path <folder>             Folder to show when Ferry starts");
            writer.WriteLine("  --mode optical|markdown|vba Page to show when Ferry starts");
            writer.WriteLine("  --no-browser                Do not open the browser automatically");
            writer.WriteLine("  --help                      Show this help");
        }

        private static string NextValue(string[] args, ref int index, string option)
        {
            index++;
            if (index >= args.Length || string.IsNullOrWhiteSpace(args[index]))
            {
                throw new ArgumentException(string.Format("{0} requires a value.", option));
            }

            return args[index];
        }

        private static int ParsePort(string value)
        {
            int port;
            if (!int.TryParse(value, out port) || port < 1 || port > 65535)
            {
                throw new ArgumentException(string.Format("Invalid port: {0}", value));
            }

            return port;
        }

        private static string ParseMode(string value)
        {
            var mode = value.ToLowerInvariant();
            if (mode != "optical" && mode != "markdown" && mode != "vba")
            {
                throw new ArgumentException(string.Format("Invalid mode: {0}", value));
            }

            return mode;
        }
    }
}
