using System;
using System.Globalization;
using System.IO;

namespace Ferry
{
    internal static class OutputLayout
    {
        public static string CreateRunDirectory(
            string outputRoot,
            FolderSnapshot source)
        {
            if (string.IsNullOrWhiteSpace(outputRoot))
            {
                throw new ArgumentException("出力フォルダが指定されていません。", "outputRoot");
            }
            if (source == null || source.SourceKind == "none")
            {
                throw new ArgumentException("入力が選ばれていません。", "source");
            }

            return CreateRunDirectory(outputRoot, TargetName(source));
        }

        public static string CreateRunDirectory(string outputRoot, string targetName)
        {
            if (string.IsNullOrWhiteSpace(outputRoot))
            {
                throw new ArgumentException("出力フォルダが指定されていません。", "outputRoot");
            }
            if (string.IsNullOrWhiteSpace(targetName))
            {
                throw new ArgumentException("出力名が指定されていません。", "targetName");
            }

            var root = Path.GetFullPath(outputRoot);
            Directory.CreateDirectory(root);
            var folderName = SafeFileName(targetName) + "_" +
                DateTime.Now.ToString("yyyyMMdd-HHmm", CultureInfo.InvariantCulture);
            var outputDirectory = FindAvailableDirectory(Path.Combine(root, folderName));
            Directory.CreateDirectory(outputDirectory);
            return outputDirectory;
        }

        private static string TargetName(FolderSnapshot source)
        {
            if (source.SourceKind == "files" && source.Files.Count == 1)
            {
                return source.Files[0].Name;
            }

            var directory = new DirectoryInfo(source.DirectoryPath);
            if (!string.IsNullOrWhiteSpace(directory.Name))
            {
                return directory.Name;
            }

            var root = (Path.GetPathRoot(source.DirectoryPath) ?? string.Empty)
                .Trim(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                .TrimEnd(':');
            return string.IsNullOrWhiteSpace(root) ? "output" : root;
        }

        private static string SafeFileName(string value)
        {
            var result = value ?? string.Empty;
            foreach (var character in Path.GetInvalidFileNameChars())
            {
                result = result.Replace(character, '_');
            }
            return string.IsNullOrWhiteSpace(result) ? "output" : result.Trim();
        }

        private static string FindAvailableDirectory(string requestedPath)
        {
            if (!File.Exists(requestedPath) && !Directory.Exists(requestedPath))
            {
                return requestedPath;
            }

            for (var index = 2; index < int.MaxValue; index++)
            {
                var candidate = string.Format(
                    CultureInfo.InvariantCulture,
                    "{0} ({1})",
                    requestedPath,
                    index);
                if (!File.Exists(candidate) && !Directory.Exists(candidate))
                {
                    return candidate;
                }
            }

            throw new IOException("空いている出力フォルダ名を作れませんでした。");
        }
    }
}
