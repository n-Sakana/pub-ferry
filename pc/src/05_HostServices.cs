using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Windows;

namespace PubFerry
{
    // Everything the page cannot do for itself: the OS file dialogs, the
    // settings file, the log, and reading a folder off the disk.
    //
    // Nothing here takes a path from the page. The page names a SETTING or a
    // TOKEN; this side decides what that means on this machine. That is what
    // keeps "the phone never browses a filesystem" true of the desktop app too
    // when its page is the thing being driven.
    public sealed class HostServices
    {
        public const int MaxPickFiles = 2000;
        public const long MaxPickBytes = 64L * 1024 * 1024;

        private readonly Window owner;
        private readonly Dictionary<string, PickedSet> staged =
            new Dictionary<string, PickedSet>(StringComparer.Ordinal);

        public sealed class PickedSet
        {
            public string Label;
            public List<WriteEntry> Files = new List<WriteEntry>();
            public List<string> FullPaths = new List<string>();
            public int Skipped;
            public long TotalSize;
        }

        public HostServices(Window owner)
        {
            this.owner = owner;
        }

        // ---- settings ---------------------------------------------------

        public static string DataDirectory()
        {
            string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            return Path.Combine(local, "PubFerry", "pc");
        }

        public static string LogDirectory()
        {
            return Path.Combine(DataDirectory(), "logs");
        }

        private static string SettingsPath()
        {
            return Path.Combine(DataDirectory(), "settings.json");
        }

        public static string DefaultDestination()
        {
            string documents = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
            return Path.Combine(documents, "Pub Ferry 受信");
        }

        public Dictionary<string, object> LoadSettings()
        {
            Dictionary<string, object> settings = new Dictionary<string, object>();
            settings["destination"] = DefaultDestination();
            settings["deviceLabel"] = Environment.MachineName;
            settings["confirmBeforeSaving"] = true;
            settings["replaceExisting"] = false;
            settings["readerSource"] = "keyboard";
            settings["serialPort"] = "";
            settings["serialBaud"] = 9600;
            settings["framesPerSecond"] = 24;
            settings["bytesPerFrame"] = 1465;

            try
            {
                string path = SettingsPath();
                if (File.Exists(path))
                {
                    Json.Node stored = Json.Parse(File.ReadAllText(path, Encoding.UTF8));
                    foreach (string key in new List<string>(settings.Keys))
                    {
                        object value = stored.Get(key).Raw;
                        if (value != null)
                        {
                            settings[key] = value;
                        }
                    }
                }
            }
            catch
            {
                // Unreadable settings are the defaults. The alternative is
                // refusing to start over a preferences file.
            }

            // A destination that has gone (an unplugged drive, a renamed
            // folder) is reported as it is rather than quietly replaced: the
            // screen says it cannot be written to and offers to change it.
            string destination = Convert.ToString(settings["destination"]);
            // Three states, not two. A folder that does not exist yet is the
            // ordinary first run — it is created when the first transfer
            // arrives — and telling somebody it "cannot be written to" on the
            // day they installed the app sends them looking for a problem that
            // is not there.
            settings["destinationExists"] = Directory.Exists(destination);
            settings["destinationWritable"] = SafeWriter.IsWritable(destination);
            return settings;
        }

        public Dictionary<string, object> SaveSettings(Json.Node incoming)
        {
            Dictionary<string, object> settings = LoadSettings();
            string destination = incoming.String("destination");
            if (!string.IsNullOrEmpty(destination))
            {
                settings["destination"] = Path.GetFullPath(destination);
            }
            string label = incoming.String("deviceLabel");
            if (!string.IsNullOrEmpty(label))
            {
                settings["deviceLabel"] = Clean(label, 40);
            }
            settings["confirmBeforeSaving"] = incoming.Bool(
                "confirmBeforeSaving",
                Convert.ToBoolean(settings["confirmBeforeSaving"]));
            settings["replaceExisting"] = incoming.Bool(
                "replaceExisting",
                Convert.ToBoolean(settings["replaceExisting"]));
            string source = incoming.String("readerSource");
            if (source == "keyboard" || source == "serial")
            {
                settings["readerSource"] = source;
            }
            string port = incoming.String("serialPort");
            if (port != null)
            {
                settings["serialPort"] = Clean(port, 16);
            }
            double baud = incoming.Number("serialBaud");
            if (baud >= 300 && baud <= 921600)
            {
                settings["serialBaud"] = (int)baud;
            }
            double fps = incoming.Number("framesPerSecond");
            if (fps >= 1 && fps <= 60)
            {
                settings["framesPerSecond"] = (int)fps;
            }
            double bytes = incoming.Number("bytesPerFrame");
            if (bytes >= 200 && bytes <= 2953)
            {
                settings["bytesPerFrame"] = (int)bytes;
            }

            Dictionary<string, object> toStore = new Dictionary<string, object>(settings);
            toStore.Remove("destinationWritable");
            toStore.Remove("destinationExists");
            Directory.CreateDirectory(DataDirectory());
            File.WriteAllText(SettingsPath(), Json.Write(toStore), new UTF8Encoding(false));
            return LoadSettings();
        }

        private static string Clean(string value, int max)
        {
            StringBuilder builder = new StringBuilder();
            foreach (char c in value)
            {
                if (c < 0x20 || c == 0x7f)
                {
                    continue;
                }
                builder.Append(c);
                if (builder.Length >= max)
                {
                    break;
                }
            }
            return builder.ToString().Trim();
        }

        // ---- picking ------------------------------------------------------

        public Dictionary<string, object> PickFiles()
        {
            Microsoft.Win32.OpenFileDialog dialog = new Microsoft.Win32.OpenFileDialog();
            dialog.Title = "送るファイルを選ぶ";
            dialog.Multiselect = true;
            dialog.CheckFileExists = true;
            bool? chosen = owner == null ? dialog.ShowDialog() : dialog.ShowDialog(owner);
            if (chosen != true || dialog.FileNames.Length == 0)
            {
                return Cancelled();
            }

            PickedSet set = new PickedSet();
            set.Label = dialog.FileNames.Length == 1
                ? Path.GetFileNameWithoutExtension(dialog.FileNames[0])
                : "選んだファイル";
            foreach (string full in dialog.FileNames)
            {
                AddFile(set, full, Path.GetFileName(full));
            }
            return Stage(set);
        }

        public Dictionary<string, object> PickFolder()
        {
            // WPF has no folder dialog and the WinForms one is a tree from
            // Windows 98. The common Save dialog with a fixed file name is the
            // usual way to get the modern browser without an extra assembly:
            // the user navigates INTO the folder they want and presses the
            // button, and only the directory part is used.
            Microsoft.Win32.SaveFileDialog dialog = new Microsoft.Win32.SaveFileDialog();
            dialog.Title = "送るフォルダーを開いて［このフォルダーを選ぶ］を押してください";
            dialog.FileName = "このフォルダーを選ぶ";
            dialog.Filter = "フォルダー|*.this-folder";
            dialog.CheckPathExists = true;
            dialog.OverwritePrompt = false;
            bool? chosen = owner == null ? dialog.ShowDialog() : dialog.ShowDialog(owner);
            if (chosen != true)
            {
                return Cancelled();
            }
            string folder = Path.GetDirectoryName(dialog.FileName);
            if (string.IsNullOrEmpty(folder) || !Directory.Exists(folder))
            {
                throw new HostActionException("no-folder", "フォルダーを読み取れませんでした。");
            }

            PickedSet set = new PickedSet();
            set.Label = new DirectoryInfo(folder).Name;
            Walk(set, folder, folder);
            if (set.Files.Count == 0)
            {
                throw new HostActionException("empty-folder", "そのフォルダーに送れるファイルがありません。");
            }
            return Stage(set);
        }

        private void Walk(PickedSet set, string root, string directory)
        {
            foreach (string entry in Directory.GetFileSystemEntries(directory))
            {
                FileAttributes attributes;
                try
                {
                    attributes = File.GetAttributes(entry);
                }
                catch
                {
                    set.Skipped++;
                    continue;
                }
                // A link points into a filesystem the other side does not have,
                // and following one is how a "folder" transfer walks off
                // somewhere else entirely. Counted, not followed.
                if ((attributes & FileAttributes.ReparsePoint) != 0)
                {
                    set.Skipped++;
                    continue;
                }
                if ((attributes & FileAttributes.Directory) != 0)
                {
                    Walk(set, root, entry);
                    continue;
                }
                string relative = entry.Substring(root.Length).TrimStart(Path.DirectorySeparatorChar);
                AddFile(set, entry, relative.Replace(Path.DirectorySeparatorChar, '/'));
            }
        }

        private void AddFile(PickedSet set, string fullPath, string relative)
        {
            string normalised = relative.Normalize(NormalizationForm.FormC);
            string reason;
            if (!SafePath.Check(normalised, out reason))
            {
                set.Skipped++;
                return;
            }
            FileInfo info = new FileInfo(fullPath);
            if (info.Length > int.MaxValue)
            {
                throw new HostActionException("file-too-large", "1 つのファイルが大きすぎます。");
            }
            set.TotalSize += info.Length;
            if (set.Files.Count >= MaxFilesAllowed() || set.TotalSize > MaxPickBytes)
            {
                throw new HostActionException(
                    "too-large",
                    "選んだものが大きすぎます。もっと少ない量に分けて送ってください。");
            }
            WriteEntry file = new WriteEntry();
            file.Path = normalised;
            file.Size = (int)info.Length;
            set.Files.Add(file);
            set.FullPaths.Add(fullPath);
        }

        private static int MaxFilesAllowed()
        {
            return MaxPickFiles;
        }

        private Dictionary<string, object> Cancelled()
        {
            Dictionary<string, object> result = new Dictionary<string, object>();
            result["cancelled"] = true;
            return result;
        }

        /// Stages a pick and returns its shape. The BYTES are not returned
        /// here: they go over a stream the page fetches by token, so a 16 MB
        /// folder does not become a 21 MB JSON string.
        private Dictionary<string, object> Stage(PickedSet set)
        {
            string conflict = SafePath.FindSetConflict(set.Files.ConvertAll(delegate(WriteEntry f) { return f.Path; }));
            if (conflict != null)
            {
                throw new HostActionException("path-conflict", conflict);
            }
            string token = Guid.NewGuid().ToString("N");
            staged[token] = set;
            // Only the newest pick is ever fetched, and holding older ones
            // would hold their file handles' worth of intent open too.
            if (staged.Count > 4)
            {
                List<string> keys = new List<string>(staged.Keys);
                for (int i = 0; i < keys.Count - 4; i++)
                {
                    staged.Remove(keys[i]);
                }
            }

            List<object> files = new List<object>();
            foreach (WriteEntry file in set.Files)
            {
                Dictionary<string, object> item = new Dictionary<string, object>();
                item["path"] = file.Path;
                item["size"] = file.Size;
                files.Add(item);
            }
            Dictionary<string, object> result = new Dictionary<string, object>();
            result["cancelled"] = false;
            result["token"] = token;
            result["label"] = set.Label;
            result["files"] = files;
            result["skipped"] = set.Skipped;
            result["totalSize"] = set.TotalSize;
            return result;
        }

        /// The bytes of a staged pick, concatenated in the order the file list
        /// gave. Read here and not in the page, because the page has no path.
        public byte[] ReadStaged(string token)
        {
            PickedSet set;
            if (token == null || !staged.TryGetValue(token, out set))
            {
                throw new HostActionException("no-such-pick", "選んだものが見つかりません。選び直してください。");
            }
            using (MemoryStream buffer = new MemoryStream((int)Math.Min(set.TotalSize, int.MaxValue)))
            {
                for (int i = 0; i < set.FullPaths.Count; i++)
                {
                    byte[] bytes = File.ReadAllBytes(set.FullPaths[i]);
                    if (bytes.Length != set.Files[i].Size)
                    {
                        // The file changed between being listed and being read.
                        // Sending a mixture of two versions would produce a
                        // transfer whose hashes are self-consistent and whose
                        // contents are nobody's file.
                        throw new HostActionException(
                            "changed",
                            "選んだあとにファイルが変わりました。選び直してください。");
                    }
                    buffer.Write(bytes, 0, bytes.Length);
                }
                return buffer.ToArray();
            }
        }

        // ---- destination --------------------------------------------------

        /// Stage a folder by path, for the screen tests.
        ///
        /// The OS file dialog cannot be operated from the page, so without this
        /// the sending screens could only ever be photographed with made-up
        /// data. Everything downstream of the dialog is the real path: the same
        /// walk, the same exclusions, the same staging, the same bytes. Only
        /// reachable when the app was started for testing — see MainWindow.
        public Dictionary<string, object> StageForTest(string path)
        {
            string full = Path.GetFullPath(path);
            PickedSet set = new PickedSet();
            if (Directory.Exists(full))
            {
                set.Label = new DirectoryInfo(full).Name;
                Walk(set, full, full);
            }
            else if (File.Exists(full))
            {
                set.Label = Path.GetFileNameWithoutExtension(full);
                AddFile(set, full, Path.GetFileName(full));
            }
            else
            {
                throw new HostActionException("no-such-path", "指定された場所がありません。");
            }
            if (set.Files.Count == 0)
            {
                throw new HostActionException("empty-folder", "送れるファイルがありません。");
            }
            return Stage(set);
        }

        public Dictionary<string, object> ChooseDestination()
        {
            Microsoft.Win32.SaveFileDialog dialog = new Microsoft.Win32.SaveFileDialog();
            dialog.Title = "保存先のフォルダーを開いて［このフォルダーを選ぶ］を押してください";
            dialog.FileName = "このフォルダーを選ぶ";
            dialog.Filter = "フォルダー|*.this-folder";
            dialog.OverwritePrompt = false;
            bool? chosen = owner == null ? dialog.ShowDialog() : dialog.ShowDialog(owner);
            if (chosen != true)
            {
                return Cancelled();
            }
            string folder = Path.GetDirectoryName(dialog.FileName);
            if (string.IsNullOrEmpty(folder))
            {
                throw new HostActionException("no-folder", "フォルダーを読み取れませんでした。");
            }
            Directory.CreateDirectory(folder);
            Dictionary<string, object> incoming = new Dictionary<string, object>();
            incoming["destination"] = folder;
            return SaveSettings(new Json.Node(incoming));
        }

        public Dictionary<string, object> Receive(byte[] block)
        {
            Dictionary<string, object> settings = LoadSettings();
            string destination = Convert.ToString(settings["destination"]);
            Directory.CreateDirectory(destination);
            WriteRequest request = SafeWriter.Parse(block);
            WriteOutcome outcome = SafeWriter.Write(destination, request);
            WriteLog("INFO", "saved " + outcome.FileCount.ToString() + " files");
            Dictionary<string, object> result = new Dictionary<string, object>();
            result["savedAs"] = outcome.SavedAs;
            result["fileCount"] = outcome.FileCount;
            result["totalSize"] = outcome.TotalSize;
            result["destinationLabel"] = ShortenForDisplay(outcome.FullPath);
            return result;
        }

        /// A path for a person to read. The full path is shown — this is the
        /// user's own machine and hiding where their file went would be worse
        /// than useless — but a very deep one is elided in the middle so it
        /// cannot push the layout around.
        public static string ShortenForDisplay(string path)
        {
            if (string.IsNullOrEmpty(path) || path.Length <= 64)
            {
                return path;
            }
            return path.Substring(0, 28) + " … " + path.Substring(path.Length - 32);
        }

        public void OpenFolder(string which)
        {
            Dictionary<string, object> settings = LoadSettings();
            string target = Convert.ToString(settings["destination"]);
            if (which == "logs")
            {
                target = LogDirectory();
            }
            Directory.CreateDirectory(target);
            // Explorer, pointed at a FOLDER. Nothing that arrived is ever
            // opened, launched, or handed to a shell verb — the folder is as
            // far as this goes.
            Process.Start("explorer.exe", "\"" + target + "\"");
        }

        // ---- log ------------------------------------------------------------

        public static void WriteStartupLog(string message)
        {
            WriteLogLine("ERROR", message);
        }

        public void WriteLog(string level, string message)
        {
            WriteLogLine(level, message);
        }

        private static void WriteLogLine(string level, string message)
        {
            try
            {
                Directory.CreateDirectory(LogDirectory());
                string path = Path.Combine(
                    LogDirectory(),
                    "pub-ferry-" + DateTime.Now.ToString("yyyyMMdd") + ".log");
                // No file contents, no paths inside a transfer, no tokens: a
                // log is a record of what happened, not a copy of what moved.
                string line = "[" + DateTime.Now.ToString("HH:mm:ss") + "] [" + level + "] " + message;
                File.AppendAllText(path, line + Environment.NewLine, new UTF8Encoding(false));
            }
            catch
            {
            }
        }
    }
}
