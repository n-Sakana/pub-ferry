using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using MacroStudio;

namespace Ferry
{
    internal static class VbaService
    {
        private static readonly UTF8Encoding Utf8NoBom = new UTF8Encoding(false);
        private static readonly string Banner = new string('=', 80);
        private static readonly string IndexLine = new string('-', 40);
        private static readonly object EncodingGate = new object();
        private static bool _encodingReady;

        public static VbaBookInfo Inspect(FolderSnapshot source, string fileName)
        {
            EnsureLegacyEncodings();
            var file = RequireWorkbook(source, fileName);
            try
            {
                var project = BookIO.ReadProject(file.FullPath);
                return Describe(file, project);
            }
            catch (MacroStudioException exception)
            {
                throw new InvalidDataException(
                    string.Format("{0} を読めません: {1}", file.Name, exception.Message),
                    exception);
            }
        }

        public static VbaExtractionResult Extract(
            FolderSnapshot source,
            IList<string> selectedNames,
            string outputRoot)
        {
            EnsureLegacyEncodings();
            if (source == null)
            {
                throw new ArgumentNullException("source");
            }
            if (selectedNames == null || selectedNames.Count == 0)
            {
                throw new ArgumentException(
                    "VBA を取り出すブックを1つ以上選んでください。",
                    "selectedNames");
            }

            var selected = new List<FolderFile>();
            var seen = new HashSet<string>(StringComparer.CurrentCultureIgnoreCase);
            foreach (var name in selectedNames)
            {
                if (!seen.Add(name))
                {
                    continue;
                }
                selected.Add(RequireWorkbook(source, name));
            }

            var outputDirectory = OutputLayout.CreateRunDirectory(outputRoot, source);
            var outputPath = FindAvailableFile(
                Path.Combine(outputDirectory, "_vba.md"));
            var books = new List<VbaExtractedBook>();
            var moduleCount = 0;
            var lineCount = 0;
            var output = new StringBuilder();

            foreach (var file in selected)
            {
                VbaProjectData project;
                try
                {
                    project = BookIO.ReadProject(file.FullPath);
                }
                catch (MacroStudioException exception)
                {
                    throw new InvalidDataException(
                        string.Format("{0} を読めません: {1}", file.Name, exception.Message),
                        exception);
                }

                if (project.Modules.Count == 0)
                {
                    throw new InvalidDataException(
                        string.Format("{0} に取り出せる VBA モジュールがありません。", file.Name));
                }

                if (output.Length > 0)
                {
                    output.AppendLine();
                }
                var bookLines = AppendBook(output, file.Name, project);
                moduleCount = checked(moduleCount + project.Modules.Count);
                lineCount = checked(lineCount + bookLines);
                books.Add(new VbaExtractedBook(
                    file.Name,
                    outputPath,
                    project.Modules.Count,
                    bookLines,
                    project.HasReadWarnings));
            }

            WriteTextAtomically(outputPath, NormalizeCrLf(output.ToString()));

            return new VbaExtractionResult(
                outputPath,
                books.Count,
                moduleCount,
                lineCount,
                books);
        }

        private static int AppendBook(
            StringBuilder output,
            string bookName,
            VbaProjectData project)
        {
            var ordered = new List<VbaModule>();
            var bookLines = 0;

            output.AppendLine(Banner);
            output.Append(' ').Append(bookName).AppendLine(" - VBA Source Code");
            output.Append(" Generated: ").AppendLine(
                DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture));
            output.AppendLine(Banner);
            output.AppendLine();
            output.AppendLine("MODULE INDEX");
            output.AppendLine(IndexLine);
            output.AppendLine();

            AppendModuleGroup(output, project.Modules, VbaModuleKind.Standard, "Standard Modules", ordered, ref bookLines);
            AppendModuleGroup(output, project.Modules, VbaModuleKind.Class, "Class Modules", ordered, ref bookLines);
            AppendModuleGroup(output, project.Modules, VbaModuleKind.Form, "UserForms", ordered, ref bookLines);
            AppendModuleGroup(output, project.Modules, VbaModuleKind.Document, "Document Modules", ordered, ref bookLines);

            output.Append("  Total: ")
                .Append(bookLines.ToString(CultureInfo.InvariantCulture))
                .Append(" lines across ")
                .Append(ordered.Count.ToString(CultureInfo.InvariantCulture))
                .AppendLine(" modules");
            output.AppendLine();

            foreach (var module in ordered)
            {
                output.AppendLine(Banner);
                output.Append(' ').AppendLine(ModuleFileName(module));
                output.AppendLine(Banner);
                output.AppendLine();
                var code = NormalizeCrLf(module.Code ?? string.Empty).TrimEnd('\r', '\n');
                if (code.Length > 0)
                {
                    output.AppendLine(code);
                    output.AppendLine();
                }
            }

            return bookLines;
        }

        private static void AppendModuleGroup(
            StringBuilder output,
            IList<VbaModule> modules,
            VbaModuleKind kind,
            string heading,
            List<VbaModule> ordered,
            ref int totalLines)
        {
            var members = new List<VbaModule>();
            foreach (var module in modules)
            {
                if (module.Kind == kind)
                {
                    members.Add(module);
                }
            }
            if (members.Count == 0)
            {
                return;
            }

            output.Append("  ").Append(heading).AppendLine(":");
            foreach (var module in members)
            {
                var lines = CountLines(module.Code);
                totalLines = checked(totalLines + lines);
                output.Append("    ")
                    .Append(ModuleFileName(module))
                    .Append(" (")
                    .Append(lines.ToString(CultureInfo.InvariantCulture))
                    .AppendLine(" lines)");
                ordered.Add(module);
            }
            output.AppendLine();
        }

        private static string ModuleFileName(VbaModule module)
        {
            var extension = string.IsNullOrWhiteSpace(module.Extension)
                ? "bas"
                : module.Extension.TrimStart('.').ToLowerInvariant();
            return module.Name + "." + extension;
        }

        private static string NormalizeCrLf(string value)
        {
            return (value ?? string.Empty)
                .Replace("\r\n", "\n")
                .Replace("\r", "\n")
                .Replace("\n", "\r\n")
                .TrimEnd('\r', '\n') + "\r\n";
        }

        private static void EnsureLegacyEncodings()
        {
            lock (EncodingGate)
            {
                if (_encodingReady)
                {
                    return;
                }

                var providerType = Type.GetType(
                    "System.Text.CodePagesEncodingProvider, System.Text.Encoding.CodePages",
                    false);
                if (providerType != null)
                {
                    var instance = providerType.GetProperty("Instance").GetValue(null, null);
                    var register = typeof(Encoding).GetMethod(
                        "RegisterProvider",
                        new[] { providerType.BaseType });
                    if (register != null)
                    {
                        register.Invoke(null, new[] { instance });
                    }
                }

                Encoding.GetEncoding(932);
                _encodingReady = true;
            }
        }

        private static VbaBookInfo Describe(FolderFile file, VbaProjectData project)
        {
            var modules = new List<VbaModuleInfo>();
            var totalLines = 0;
            foreach (var module in project.Modules)
            {
                var lines = CountLines(module.Code);
                totalLines = checked(totalLines + lines);
                modules.Add(new VbaModuleInfo(
                    module.Name,
                    module.Extension,
                    ModuleKind(module.Kind),
                    lines));
            }

            byte[] bytes = null;
            try
            {
                bytes = File.ReadAllBytes(file.FullPath);
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }

            var inventory = BookInventoryReader.Read(file.FullPath, bytes, project);
            return new VbaBookInfo(
                file.Name,
                totalLines,
                project.HasReadWarnings,
                project.HasSourceDoubt(),
                modules,
                new VbaInventoryInfo(
                    inventory.References,
                    inventory.Connections,
                    inventory.BarcodeFonts,
                    inventory.HasPowerQuery,
                    inventory.ActiveXCount,
                    inventory.ExternalLinkCount,
                    inventory.HasVbaSignature,
                    inventory.Complete));
        }

        private static FolderFile RequireWorkbook(FolderSnapshot source, string fileName)
        {
            if (source == null)
            {
                throw new ArgumentNullException("source");
            }
            if (string.IsNullOrWhiteSpace(fileName))
            {
                throw new ArgumentException("ブック名が空です。", "fileName");
            }

            foreach (var file in source.Files)
            {
                if (string.Equals(
                        file.Name,
                        fileName,
                        StringComparison.CurrentCultureIgnoreCase))
                {
                    if (!file.VbaWorkbook)
                    {
                        break;
                    }
                    return file;
                }
            }

            throw new FileNotFoundException(
                string.Format("選んだブックが見つかりません: {0}", fileName));
        }

        private static string ModuleKind(VbaModuleKind kind)
        {
            switch (kind)
            {
                case VbaModuleKind.Form:
                    return "フォーム";
                case VbaModuleKind.Standard:
                    return "標準モジュール";
                case VbaModuleKind.Document:
                    return "ドキュメント";
                default:
                    return "クラス";
            }
        }

        private static int CountLines(string text)
        {
            if (string.IsNullOrEmpty(text))
            {
                return 0;
            }

            var count = 1;
            for (var index = 0; index < text.Length; index++)
            {
                if (text[index] == '\r')
                {
                    count++;
                    if (index + 1 < text.Length && text[index + 1] == '\n')
                    {
                        index++;
                    }
                }
                else if (text[index] == '\n')
                {
                    count++;
                }
            }
            return count;
        }

        private static string FindAvailableFile(string desiredPath)
        {
            if (!Directory.Exists(desiredPath) && !File.Exists(desiredPath))
            {
                return desiredPath;
            }

            var directory = Path.GetDirectoryName(desiredPath);
            var baseName = Path.GetFileNameWithoutExtension(desiredPath);
            var extension = Path.GetExtension(desiredPath);
            for (var suffix = 2; suffix < 10000; suffix++)
            {
                var candidate = Path.Combine(
                    directory,
                    baseName + " (" + suffix.ToString(CultureInfo.InvariantCulture) + ")" + extension);
                if (!Directory.Exists(candidate) && !File.Exists(candidate))
                {
                    return candidate;
                }
            }

            throw new IOException("空いている出力先を作れませんでした。");
        }

        private static void WriteTextAtomically(string path, string text)
        {
            var temporaryPath = path + ".ferry-" + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                File.WriteAllText(temporaryPath, text, Utf8NoBom);
                File.Move(temporaryPath, path);
            }
            finally
            {
                if (File.Exists(temporaryPath))
                {
                    File.Delete(temporaryPath);
                }
            }
        }
    }

    internal sealed class VbaBookInfo
    {
        public VbaBookInfo(
            string name,
            int totalLines,
            bool hasWarnings,
            bool sourceDoubt,
            List<VbaModuleInfo> modules,
            VbaInventoryInfo inventory)
        {
            Name = name;
            TotalLines = totalLines;
            HasWarnings = hasWarnings;
            SourceDoubt = sourceDoubt;
            Modules = modules;
            Inventory = inventory;
        }

        public string Name { get; private set; }
        public int TotalLines { get; private set; }
        public bool HasWarnings { get; private set; }
        public bool SourceDoubt { get; private set; }
        public List<VbaModuleInfo> Modules { get; private set; }
        public VbaInventoryInfo Inventory { get; private set; }
    }

    internal sealed class VbaModuleInfo
    {
        public VbaModuleInfo(string name, string extension, string kind, int lineCount)
        {
            Name = name;
            Extension = extension;
            Kind = kind;
            LineCount = lineCount;
        }

        public string Name { get; private set; }
        public string Extension { get; private set; }
        public string Kind { get; private set; }
        public int LineCount { get; private set; }
    }

    internal sealed class VbaInventoryInfo
    {
        public VbaInventoryInfo(
            List<string> references,
            List<string> connections,
            List<string> barcodeFonts,
            bool hasPowerQuery,
            int activeXCount,
            int externalLinkCount,
            bool hasVbaSignature,
            bool complete)
        {
            References = references;
            Connections = connections;
            BarcodeFonts = barcodeFonts;
            HasPowerQuery = hasPowerQuery;
            ActiveXCount = activeXCount;
            ExternalLinkCount = externalLinkCount;
            HasVbaSignature = hasVbaSignature;
            Complete = complete;
        }

        public List<string> References { get; private set; }
        public List<string> Connections { get; private set; }
        public List<string> BarcodeFonts { get; private set; }
        public bool HasPowerQuery { get; private set; }
        public int ActiveXCount { get; private set; }
        public int ExternalLinkCount { get; private set; }
        public bool HasVbaSignature { get; private set; }
        public bool Complete { get; private set; }
    }

    internal sealed class VbaExtractionResult
    {
        public VbaExtractionResult(
            string outputPath,
            int bookCount,
            int moduleCount,
            int lineCount,
            List<VbaExtractedBook> books)
        {
            OutputPath = outputPath;
            BookCount = bookCount;
            ModuleCount = moduleCount;
            LineCount = lineCount;
            Books = books;
        }

        public string OutputPath { get; private set; }
        public int BookCount { get; private set; }
        public int ModuleCount { get; private set; }
        public int LineCount { get; private set; }
        public List<VbaExtractedBook> Books { get; private set; }
    }

    internal sealed class VbaExtractedBook
    {
        public VbaExtractedBook(
            string name,
            string outputPath,
            int moduleCount,
            int lineCount,
            bool hasWarnings)
        {
            Name = name;
            OutputPath = outputPath;
            ModuleCount = moduleCount;
            LineCount = lineCount;
            HasWarnings = hasWarnings;
        }

        public string Name { get; private set; }
        public string OutputPath { get; private set; }
        public int ModuleCount { get; private set; }
        public int LineCount { get; private set; }
        public bool HasWarnings { get; private set; }
    }
}
