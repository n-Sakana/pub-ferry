using System;
using System.Collections.Generic;
using System.IO;

namespace Ferry
{
    internal static class FolderCatalog
    {
        private static readonly HashSet<string> MarkdownExtensions =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                ".pdf",
                ".doc", ".docx", ".rtf",
                ".xls", ".xlsx", ".xlsm", ".xlsb", ".xlt", ".xltx", ".xltm",
                ".ppt", ".pptx", ".pptm",
                ".txt", ".md", ".csv", ".tsv", ".log", ".json", ".xml", ".html", ".htm", ".yaml", ".yml"
            };

        private static readonly HashSet<string> VbaWorkbookExtensions =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                ".xls", ".xlsm", ".xlsb", ".xla", ".xlam", ".xltm"
            };

        public static FolderSnapshot Inspect(string path)
        {
            var fullPath = RequireDirectory(path);
            var directory = new DirectoryInfo(fullPath);
            var files = new List<FolderFile>();

            foreach (var file in directory.GetFiles("*", SearchOption.TopDirectoryOnly))
            {
                if (!ShouldShow(file))
                {
                    continue;
                }

                try
                {
                    var extension = file.Extension.ToLowerInvariant();
                    files.Add(new FolderFile(
                        file.Name,
                        extension,
                        BadgeFor(extension),
                        KindFor(extension),
                        file.Length,
                        new DateTimeOffset(file.LastWriteTimeUtc, TimeSpan.Zero),
                        MarkdownExtensions.Contains(extension),
                        VbaWorkbookExtensions.Contains(extension)));
                }
                catch (FileNotFoundException)
                {
                    // A file can disappear while a live folder is being enumerated.
                }
            }

            files.Sort(delegate (FolderFile left, FolderFile right)
            {
                return StringComparer.CurrentCultureIgnoreCase.Compare(left.Name, right.Name);
            });
            return new FolderSnapshot(fullPath, files);
        }

        public static DirectoryListing Browse(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                return BrowseRoots();
            }

            var fullPath = RequireDirectory(path);
            var directory = new DirectoryInfo(fullPath);
            var entries = new List<FolderDirectory>();

            foreach (var child in directory.GetDirectories("*", SearchOption.TopDirectoryOnly))
            {
                if (!ShouldShow(child))
                {
                    continue;
                }

                entries.Add(new FolderDirectory(child.Name, child.FullName));
            }

            entries.Sort(delegate (FolderDirectory left, FolderDirectory right)
            {
                return StringComparer.CurrentCultureIgnoreCase.Compare(left.Name, right.Name);
            });
            var parent = directory.Parent == null ? null : directory.Parent.FullName;
            return new DirectoryListing(fullPath, parent, entries);
        }

        private static DirectoryListing BrowseRoots()
        {
            if (!PlatformInfo.IsWindows)
            {
                var root = Path.GetPathRoot(Directory.GetCurrentDirectory());
                return Browse(string.IsNullOrEmpty(root) ? "/" : root);
            }

            var drives = new List<FolderDirectory>();
            foreach (var drive in DriveInfo.GetDrives())
            {
                try
                {
                    if (!drive.IsReady)
                    {
                        continue;
                    }

                    var driveName = drive.Name.TrimEnd(Path.DirectorySeparatorChar);
                    var label = string.IsNullOrWhiteSpace(drive.VolumeLabel)
                        ? driveName
                        : string.Format("{0}  {1}", driveName, drive.VolumeLabel);
                    drives.Add(new FolderDirectory(label, drive.RootDirectory.FullName));
                }
                catch (IOException)
                {
                    // Removable drives can become unavailable during enumeration.
                }
                catch (UnauthorizedAccessException)
                {
                    // Do not make one inaccessible drive hide the other roots.
                }
            }

            return new DirectoryListing(null, null, drives);
        }

        private static string RequireDirectory(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                throw new ArgumentException("A folder path is required.", "path");
            }

            var fullPath = Path.GetFullPath(path);
            if (!Directory.Exists(fullPath))
            {
                throw new DirectoryNotFoundException(string.Format("Folder not found: {0}", fullPath));
            }

            return fullPath;
        }

        private static bool ShouldShow(FileSystemInfo entry)
        {
            if (!PlatformInfo.IsWindows && entry.Name.StartsWith(".", StringComparison.Ordinal))
            {
                return false;
            }

            try
            {
                return (entry.Attributes & (FileAttributes.Hidden | FileAttributes.System)) == 0;
            }
            catch (IOException)
            {
                return false;
            }
            catch (UnauthorizedAccessException)
            {
                return false;
            }
        }

        private static string KindFor(string extension)
        {
            switch (extension)
            {
                case ".pdf":
                    return "PDF";
                case ".doc":
                case ".docx":
                case ".rtf":
                    return "Word";
                case ".xls":
                case ".xlsx":
                case ".xlsm":
                case ".xlsb":
                case ".xla":
                case ".xlam":
                case ".xlt":
                case ".xltx":
                case ".xltm":
                    return "Excel";
                case ".ppt":
                case ".pptx":
                case ".pptm":
                    return "PowerPoint";
                case ".txt":
                case ".md":
                case ".csv":
                case ".tsv":
                case ".log":
                case ".json":
                case ".xml":
                case ".html":
                case ".htm":
                case ".yaml":
                case ".yml":
                    return "Text";
                default:
                    return "Other";
            }
        }

        private static string BadgeFor(string extension)
        {
            var badge = extension.TrimStart('.').ToUpperInvariant();
            return string.IsNullOrEmpty(badge) ? "FILE" : badge.Substring(0, Math.Min(4, badge.Length));
        }
    }

    internal sealed class FolderSnapshot
    {
        public FolderSnapshot(string path, List<FolderFile> files)
        {
            Path = path;
            Files = files;
        }

        public string Path { get; private set; }
        public List<FolderFile> Files { get; private set; }
    }

    internal sealed class FolderFile
    {
        public FolderFile(
            string name,
            string extension,
            string badge,
            string kind,
            long size,
            DateTimeOffset modifiedUtc,
            bool markdownSupported,
            bool vbaWorkbook)
        {
            Name = name;
            Extension = extension;
            Badge = badge;
            Kind = kind;
            Size = size;
            ModifiedUtc = modifiedUtc;
            MarkdownSupported = markdownSupported;
            VbaWorkbook = vbaWorkbook;
        }

        public string Name { get; private set; }
        public string Extension { get; private set; }
        public string Badge { get; private set; }
        public string Kind { get; private set; }
        public long Size { get; private set; }
        public DateTimeOffset ModifiedUtc { get; private set; }
        public bool MarkdownSupported { get; private set; }
        public bool VbaWorkbook { get; private set; }
    }

    internal sealed class DirectoryListing
    {
        public DirectoryListing(string path, string parentPath, List<FolderDirectory> directories)
        {
            Path = path;
            ParentPath = parentPath;
            Directories = directories;
        }

        public string Path { get; private set; }
        public string ParentPath { get; private set; }
        public List<FolderDirectory> Directories { get; private set; }
    }

    internal sealed class FolderDirectory
    {
        public FolderDirectory(string name, string path)
        {
            Name = name;
            Path = path;
        }

        public string Name { get; private set; }
        public string Path { get; private set; }
    }
}
