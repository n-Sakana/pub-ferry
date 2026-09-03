using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Text;

namespace MacroStudio
{
    public sealed class MacroStudioException : Exception
    {
        public string ErrorCode;

        public MacroStudioException(string errorCode, string message)
            : base(message)
        {
            ErrorCode = errorCode;
        }

        public MacroStudioException(
            string errorCode,
            string message,
            Exception innerException)
            : base(message, innerException)
        {
            ErrorCode = errorCode;
        }
    }

    public sealed class BookContent
    {
        public string FilePath;
        public string Extension;
        public bool IsZip;
        public string VbaEntryName;
        public byte[] VbaProjectBytes;
        public bool HasReadWarnings;
        // At this layer a warning only ever means "vbaProject.bin was not
        // where it should be", so it is kept as its own fact: the route
        // used to find the part says nothing about the source inside it.
        public bool ContainerFallback;
        public bool Salvaged;
        public VbaProjectData SalvagedProject;

        public BookContent()
        {
            FilePath = string.Empty;
            Extension = string.Empty;
            VbaEntryName = string.Empty;
            VbaProjectBytes = new byte[0];
            HasReadWarnings = false;
            ContainerFallback = false;
            Salvaged = false;
        }
    }

    public sealed class ModuleBuildResult
    {
        public string Name;
        public string Result;
        public string Message;

        public ModuleBuildResult()
        {
            Name = string.Empty;
            Result = string.Empty;
            Message = string.Empty;
        }
    }

    public sealed class BookBuildResult
    {
        public bool Success;
        public string OutputPath;
        public string ErrorCode;
        public string Message;
        public long ElapsedMilliseconds;
        // The source workbook was signed, and the output is not. The
        // screen has to say so: whoever distributes this file needs to
        // sign it again, and nothing else in the run would tell them.
        public bool SignatureRemoved;
        public List<ModuleBuildResult> Results;

        public BookBuildResult()
        {
            OutputPath = string.Empty;
            ErrorCode = string.Empty;
            Message = string.Empty;
            Results = new List<ModuleBuildResult>();
        }
    }

    public static class BookIO
    {
        private sealed class BuildVerificationException : Exception
        {
            public BuildVerificationException(string message)
                : base(message)
            {
            }

            public BuildVerificationException(
                string message,
                Exception innerException)
                : base(message, innerException)
            {
            }
        }

        public static BookContent ReadVbaProjectBytes(string filePath)
        {
            string fullPath = GetFullPath(filePath);
            string extension = Path.GetExtension(fullPath).ToLowerInvariant();

            byte[] bookBytes = ReadBookBytes(fullPath);
            BookContent content = new BookContent();
            content.FilePath = fullPath;
            content.Extension = extension;
            content.IsZip = false;

            // The extension never blocks the attach: the container kind is
            // decided by the file content. ZIP-based books (xlsm, xlam,
            // xlsb) carry vbaProject.bin inside the archive; OLE2-based
            // books (xls) are themselves the VBA host container.
            bool looksOle2 = HasOle2Signature(bookBytes);
            byte[] vbaBytes = null;
            bool hadWarnings = false;
            bool archiveEnumerated = false;
            string entryName = null;

            // A workbook encrypted as a whole file is also an OLE2
            // container, but it holds the real workbook as one encrypted
            // stream rather than any VBA. Without this it would be handed
            // to the VBA reader, come back with nothing, and be reported
            // as a workbook that was read completely and simply has no
            // modules.
            if (looksOle2 && IsEncryptedOfficePackage(bookBytes))
            {
                throw new MacroStudioException(
                    "E-ATTACH-04",
                    "The workbook file itself is encrypted " +
                    "(password to open).");
            }
            if (!looksOle2)
            {
                vbaBytes = TryReadZipVbaProject(
                    bookBytes,
                    out entryName,
                    out archiveEnumerated,
                    ref hadWarnings);
                if (vbaBytes != null)
                {
                    content.IsZip = true;
                    content.VbaEntryName =
                        entryName == null ? string.Empty : entryName;
                }
            }
            if (vbaBytes == null && looksOle2)
            {
                vbaBytes = bookBytes;
            }
            if (vbaBytes == null)
            {
                // An OLE2 container embedded at some offset, e.g. a stored
                // ZIP member reachable even when both the archive and its
                // directory are unusable.
                vbaBytes = TryFindEmbeddedVbaContainer(bookBytes);
                if (vbaBytes != null)
                {
                    hadWarnings = true;
                }
            }
            if (vbaBytes == null)
            {
                // No container structure survived. If VBA source is still
                // physically present, hand the whole file to the salvage
                // reader instead of reporting a macro-free workbook.
                VbaProjectData salvaged = SalvageProject(bookBytes);
                if (salvaged.Modules.Count > 0)
                {
                    vbaBytes = bookBytes;
                    hadWarnings = true;
                    content.Salvaged = true;
                    content.SalvagedProject = salvaged;
                }
            }
            if (vbaBytes == null && !looksOle2 && !archiveEnumerated)
            {
                // Nothing here read as a workbook container: no OLE2
                // signature, and the ZIP directory never enumerated - a
                // zero-byte file, a truncated download, or something that
                // only wears the extension. Saying "this workbook has no
                // macros" would assert it IS a workbook, which we never
                // established. SPEC 13.4 reserves E-ATTACH-03 for a
                // container that WAS read and holds no VBA anywhere.
                throw new MacroStudioException(
                    "E-ATTACH-02",
                    "The file did not read as a workbook container.");
            }
            if (vbaBytes == null)
            {
                throw new MacroStudioException(
                    "E-ATTACH-03",
                    "vbaProject.bin was not found in the workbook.");
            }

            content.VbaProjectBytes = vbaBytes;
            content.HasReadWarnings = hadWarnings;
            content.ContainerFallback = hadWarnings;
            return content;
        }

        // Whole-file encryption, as written by Office: the package lives
        // in an "EncryptedPackage" stream and its parameters in
        // "EncryptionInfo", both directly under the root storage
        // (MS-OFFCRYPTO). Both are required, and the directory is read
        // through the normal reader rather than scanned for byte
        // patterns, so a workbook that merely mentions those words
        // somewhere in its data cannot be mistaken for an encrypted one.
        //
        // This is not the same thing as a VBA project that is locked for
        // viewing. That lock lives inside vbaProject.bin (DPB / CMG in
        // the PROJECT stream) while the file itself stays readable, so it
        // never reaches this test.
        private static bool IsEncryptedOfficePackage(byte[] bookBytes)
        {
            Ole2File file;

            try
            {
                file = Ole2File.Parse(bookBytes);
            }
            catch (OutOfMemoryException)
            {
                throw;
            }
            catch (Exception)
            {
                // A container that cannot be read is a damaged or
                // unsupported file. Saying "encrypted" about it would be
                // a guess.
                return false;
            }

            if (file.RootEntry == null)
            {
                return false;
            }
            // Object type 2 is a stream: a storage of the same name is
            // not the encrypted package.
            return file.FindChild(file.RootEntry, "EncryptedPackage", 2)
                    != null &&
                file.FindChild(file.RootEntry, "EncryptionInfo", 2)
                    != null;
        }

        public static VbaProjectData ReadProject(string filePath)
        {
            BookContent content = ReadVbaProjectBytes(filePath);
            VbaProjectData project = null;
            if (content.VbaProjectBytes.Length > 0)
            {
                try
                {
                    project = VbaProjectReader.Read(
                        content.VbaProjectBytes);
                }
                catch (OutOfMemoryException)
                {
                    throw;
                }
                catch (Exception)
                {
                    project = null;
                }
            }

            // When the structured readers fail or come back empty, scan
            // the raw container bytes for compressed source containers so
            // the modules that are physically present stay readable.
            if (project == null || project.Modules.Count == 0)
            {
                VbaProjectData salvaged =
                    content.SalvagedProject != null ?
                    content.SalvagedProject :
                    SalvageProject(content.VbaProjectBytes);
                if (project == null)
                {
                    project = salvaged;
                }
                else if (salvaged.Modules.Count > 0)
                {
                    project.Modules = salvaged.Modules;
                    project.CodePage = salvaged.CodePage;
                    project.Encoding = salvaged.Encoding;
                    project.HasReadWarnings = true;
                    project.Salvaged = true;
                }
            }

            project.FilePath = content.FilePath;
            project.IsZip = content.IsZip;
            project.VbaEntryName = content.VbaEntryName;
            project.HasReadWarnings =
                project.HasReadWarnings || content.HasReadWarnings;
            project.ContainerFallback =
                project.ContainerFallback || content.ContainerFallback;
            project.Salvaged = project.Salvaged || content.Salvaged;
            return project;
        }

        public static VbaProjectData SalvageProject(byte[] bytes)
        {
            VbaProjectData project = new VbaProjectData();
            project.Ole2Bytes = bytes == null ? new byte[0] : bytes;
            project.HasReadWarnings = true;
            project.Salvaged = true;
            if (bytes == null || bytes.Length < 3)
            {
                project.CodePage = 932;
                project.Encoding = VbaProjectReader.ResolveEncoding(932);
                return project;
            }

            List<int> offsets = new List<int>();
            List<byte[]> payloads = new List<byte[]>();
            List<bool> isSource = new List<bool>();
            byte[] marker = Encoding.ASCII.GetBytes("Attribute VB_Name");
            int codePage = 0;
            List<VbaDirModule> dirRecords = null;
            int position;
            for (position = 0; position + 3 <= bytes.Length; position++)
            {
                if (bytes[position] != 0x01)
                {
                    continue;
                }
                ushort header = BitConverter.ToUInt16(
                    bytes,
                    position + 1);
                if (((header >> 12) & 0x0007) != 3)
                {
                    continue;
                }

                // Cheap pre-check: decompress only the first chunk and
                // look for the module marker before spending time on the
                // full container.
                int firstChunkEnd;
                byte[] firstChunk = VbaCompression.DecompressUntilInvalid(
                    bytes,
                    position,
                    4096,
                    out firstChunkEnd);
                if (firstChunk.Length == 0)
                {
                    continue;
                }

                bool sourceCandidate =
                    IndexOfBytes(firstChunk, marker) >= 0;
                bool dirCandidate = false;
                if (!sourceCandidate)
                {
                    int foundCodePage;
                    dirCandidate = VbaProjectReader.TryFindCodePage(
                        firstChunk,
                        out foundCodePage);
                }
                if (!sourceCandidate && !dirCandidate)
                {
                    continue;
                }

                int containerEnd;
                byte[] payload = VbaCompression.DecompressUntilInvalid(
                    bytes,
                    position,
                    0,
                    out containerEnd);
                if (payload.Length == 0)
                {
                    continue;
                }
                if (containerEnd > position)
                {
                    // Do not rediscover chunks inside a container that
                    // was already read.
                    position = containerEnd - 1;
                }

                if (sourceCandidate)
                {
                    offsets.Add(position);
                    payloads.Add(payload);
                    isSource.Add(true);
                }
                else
                {
                    int foundCodePage;
                    if (codePage == 0 &&
                        VbaProjectReader.TryFindCodePage(
                            payload,
                            out foundCodePage))
                    {
                        codePage = foundCodePage;
                        try
                        {
                            int dirCodePage;
                            dirRecords = VbaProjectReader.ReadDirModules(
                                payload,
                                out dirCodePage);
                        }
                        catch (Exception)
                        {
                            dirRecords = null;
                        }
                    }
                }
            }

            if (codePage == 0)
            {
                codePage = 932;
            }
            project.Encoding = VbaProjectReader.ResolveEncoding(codePage);
            project.CodePage = project.Encoding.CodePage;

            Dictionary<string, ushort> dirTypes =
                new Dictionary<string, ushort>(
                    StringComparer.OrdinalIgnoreCase);
            if (dirRecords != null)
            {
                int recordIndex;
                for (recordIndex = 0;
                    recordIndex < dirRecords.Count;
                    recordIndex++)
                {
                    VbaDirModule record = dirRecords[recordIndex];
                    if (!dirTypes.ContainsKey(record.Name))
                    {
                        dirTypes.Add(record.Name, record.TypeId);
                    }
                }
            }

            Dictionary<string, int> byName =
                new Dictionary<string, int>(
                    StringComparer.OrdinalIgnoreCase);
            int index;
            for (index = 0; index < payloads.Count; index++)
            {
                if (!isSource[index])
                {
                    continue;
                }

                byte[] payload = payloads[index];
                string fullCode = project.Encoding.GetString(payload);
                string moduleName = ExtractModuleName(fullCode);
                if (moduleName == null || moduleName.Length == 0)
                {
                    moduleName = "Module" + (project.Modules.Count + 1)
                        .ToString();
                }

                int existingIndex;
                if (byName.TryGetValue(moduleName, out existingIndex))
                {
                    // Keep the longest recovered source per module name.
                    if (project.Modules[existingIndex]
                        .FullSourceBytes.Length >= payload.Length)
                    {
                        continue;
                    }
                    FillSalvagedModule(
                        project.Modules[existingIndex],
                        payload,
                        fullCode,
                        offsets[index],
                        dirTypes);
                    continue;
                }

                VbaModule module = new VbaModule();
                module.Name = moduleName;
                module.StreamName = moduleName;
                FillSalvagedModule(
                    module,
                    payload,
                    fullCode,
                    offsets[index],
                    dirTypes);
                byName.Add(moduleName, project.Modules.Count);
                project.Modules.Add(module);
            }

            project.Modules.Sort(delegate(VbaModule left, VbaModule right)
            {
                int typeResult =
                    ((int)left.Kind).CompareTo((int)right.Kind);
                if (typeResult != 0)
                {
                    return typeResult;
                }

                return StringComparer.OrdinalIgnoreCase.Compare(
                    left.Name,
                    right.Name);
            });
            return project;
        }

        private static void FillSalvagedModule(
            VbaModule module,
            byte[] payload,
            string fullCode,
            int sourceOffset,
            Dictionary<string, ushort> dirTypes)
        {
            string attributeHeader;
            string code;
            VbaProjectReader.SplitAttributeHeader(
                fullCode,
                out attributeHeader,
                out code);

            ushort typeId;
            if (!dirTypes.TryGetValue(module.Name, out typeId))
            {
                typeId = 0;
            }
            module.Kind = GuessModuleKind(attributeHeader, typeId);
            module.Extension = GetSalvageExtension(module.Kind);
            module.SourceOffset = (uint)sourceOffset;
            module.FullSourceBytes = payload;
            module.FullCode = fullCode;
            module.AttributeHeader = attributeHeader;
            module.Code = code;
        }

        private static VbaModuleKind GuessModuleKind(
            string attributeHeader,
            ushort typeId)
        {
            if (typeId == 0x0021)
            {
                return VbaModuleKind.Standard;
            }
            if (attributeHeader.IndexOf(
                "VB_Base",
                StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return VbaModuleKind.Document;
            }
            if (attributeHeader.IndexOf(
                "VB_PredeclaredId",
                StringComparison.OrdinalIgnoreCase) >= 0 ||
                attributeHeader.IndexOf(
                    "VB_Exposed",
                    StringComparison.OrdinalIgnoreCase) >= 0 ||
                attributeHeader.IndexOf(
                    "VB_Creatable",
                    StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return VbaModuleKind.Class;
            }

            return VbaModuleKind.Standard;
        }

        private static string GetSalvageExtension(VbaModuleKind kind)
        {
            if (kind == VbaModuleKind.Standard)
            {
                return "bas";
            }
            if (kind == VbaModuleKind.Form)
            {
                return "frm";
            }

            return "cls";
        }

        private static string ExtractModuleName(string fullCode)
        {
            int markerIndex = fullCode.IndexOf(
                "Attribute VB_Name",
                StringComparison.OrdinalIgnoreCase);
            if (markerIndex < 0)
            {
                return null;
            }

            int quoteStart = fullCode.IndexOf('"', markerIndex);
            if (quoteStart < 0)
            {
                return null;
            }
            int quoteEnd = fullCode.IndexOf('"', quoteStart + 1);
            if (quoteEnd < 0)
            {
                return null;
            }

            return fullCode.Substring(
                quoteStart + 1,
                quoteEnd - quoteStart - 1).Trim();
        }

        private static int IndexOfBytes(byte[] data, byte[] pattern)
        {
            return IndexOfBytesFrom(data, pattern, 0);
        }

        private static int IndexOfBytesFrom(
            byte[] data,
            byte[] pattern,
            int start)
        {
            int limit = data.Length - pattern.Length;
            int index;
            for (index = start; index <= limit; index++)
            {
                int patternIndex = 0;
                while (patternIndex < pattern.Length &&
                    data[index + patternIndex] == pattern[patternIndex])
                {
                    patternIndex++;
                }
                if (patternIndex == pattern.Length)
                {
                    return index;
                }
            }

            return -1;
        }

        // The VBA the attach step read, written out as one text. The
        // build compares it against the project it reads for itself, so
        // a workbook that was edited and saved in the meantime cannot be
        // overwritten by an answer that was based on the older code.
        public static string CreateSourceSignature(VbaProjectData project)
        {
            if (project == null)
            {
                throw new ArgumentNullException("project");
            }

            // Separators that cannot appear in a module name or in VBA
            // source. Each field is written with its length as well, so
            // the boundaries stay unambiguous whatever the code holds.
            StringBuilder text = new StringBuilder();
            int index;

            text.Append(project.Modules.Count);
            text.Append('\u0001');
            for (index = 0; index < project.Modules.Count; index++)
            {
                VbaModule module = project.Modules[index];
                string code = module.Code == null
                    ? string.Empty
                    : module.Code;
                text.Append(module.Name == null
                    ? string.Empty
                    : module.Name);
                text.Append('\u0000');
                text.Append((int)module.Kind);
                text.Append('\u0000');
                text.Append(code.Length);
                text.Append('\u0000');
                text.Append(code);
                text.Append('\u0001');
            }
            return text.ToString();
        }

        public static BookBuildResult BuildCopy(
            string sourcePath,
            string outputPath,
            IDictionary<string, string> moduleChanges)
        {
            return BuildCopy(
                sourcePath,
                outputPath,
                moduleChanges,
                new List<VbaModuleAddition>());
        }

        public static BookBuildResult BuildCopy(
            string sourcePath,
            string outputPath,
            IDictionary<string, string> moduleChanges,
            IList<VbaModuleAddition> newModules)
        {
            return BuildCopy(
                sourcePath,
                outputPath,
                moduleChanges,
                newModules,
                null,
                false);
        }

        // expectedSourceSignature: what the VBA looked like when the
        // request was made, or null to skip the comparison.
        // replaceExisting: the output already exists and was made by this
        // same run, so this build may take its place. The new workbook is
        // assembled and verified beside it and only swapped in on
        // success, so a failed rebuild never destroys the earlier one.
        public static BookBuildResult BuildCopy(
            string sourcePath,
            string outputPath,
            IDictionary<string, string> moduleChanges,
            IList<VbaModuleAddition> newModules,
            string expectedSourceSignature,
            bool replaceExisting)
        {
            BookBuildResult result = new BookBuildResult();
            DateTime started = DateTime.UtcNow;
            string createdPath = null;
            string asidePath = null;

            try
            {
                if (moduleChanges == null)
                {
                    throw new ArgumentNullException("moduleChanges");
                }
                if (newModules == null)
                {
                    throw new ArgumentNullException("newModules");
                }

                VbaProjectData sourceProject = ReadProject(sourcePath);
                if (sourceProject.Ole2 == null)
                {
                    throw new MacroStudioException(
                        "E-BUILD-01",
                        "The workbook structure could not be parsed " +
                        "for build.");
                }
                if (expectedSourceSignature != null &&
                    !string.Equals(
                        expectedSourceSignature,
                        CreateSourceSignature(sourceProject),
                        StringComparison.Ordinal))
                {
                    throw new MacroStudioException(
                        "E-BUILD-04",
                        "The source workbook macros changed after the " +
                        "request was prepared.");
                }

                string fullOutputPath = GetBuildOutputPath(outputPath);
                result.OutputPath = fullOutputPath;
                if (string.Equals(
                    sourceProject.FilePath,
                    fullOutputPath,
                    StringComparison.OrdinalIgnoreCase))
                {
                    throw new IOException(
                        "The build output path is the source workbook.");
                }

                bool replacing = replaceExisting &&
                    File.Exists(fullOutputPath);
                string workPath = replacing
                    ? fullOutputPath + ".rebuild"
                    : fullOutputPath;

                Dictionary<string, string> changedModules =
                    PrepareBuildChanges(
                        sourceProject,
                        moduleChanges,
                        result.Results);
                List<VbaModuleAddition> additions =
                    PrepareBuildAdditions(
                        sourceProject,
                        newModules,
                        result.Results);

                if (replacing && File.Exists(workPath))
                {
                    File.Delete(workPath);
                }
                File.Copy(
                    sourceProject.FilePath,
                    workPath,
                    false);
                createdPath = workPath;

                byte[] rebuiltProject =
                    VbaProjectWriter.RebuildProject(
                        sourceProject,
                        changedModules,
                        additions);
                result.SignatureRemoved = WriteZipVbaProject(
                    workPath,
                    rebuiltProject,
                    sourceProject.IsZip,
                    sourceProject.VbaEntryName);
                VerifyBuild(
                    sourceProject,
                    workPath,
                    changedModules,
                    additions);

                // The earlier workbook is moved aside rather than
                // deleted, so there is no moment where neither
                // generation exists. It is dropped once the new one is
                // in place, and put back if anything here fails.
                if (replacing)
                {
                    asidePath = fullOutputPath + ".previous";
                    if (File.Exists(asidePath))
                    {
                        File.Delete(asidePath);
                    }
                    File.Move(fullOutputPath, asidePath);
                    File.Move(workPath, fullOutputPath);
                    createdPath = fullOutputPath;
                    try
                    {
                        File.Delete(asidePath);
                    }
                    catch (Exception)
                    {
                        // Keeping the old copy is untidy, never a
                        // failure: the new workbook is already in place.
                    }
                    asidePath = null;
                }

                SetPendingResults(
                    result.Results,
                    "written",
                    string.Empty);
                result.Success = true;
                return FinishBuildResult(result, started);
            }
            catch (BuildVerificationException ex)
            {
                SetPendingResults(
                    result.Results,
                    "verify_failed",
                    ex.Message);
                result.ErrorCode = "E-BUILD-02";
                result.Message = ex.Message;
            }
            catch (MacroStudioException ex)
            {
                RemovePendingResults(result.Results);
                result.ErrorCode = ex.ErrorCode;
                result.Message = ex.Message;
            }
            catch (Exception ex)
            {
                if (IsBuildIoException(ex))
                {
                    SetPendingResults(
                        result.Results,
                        "io_error",
                        ex.Message);
                    result.ErrorCode = "E-BUILD-03";
                }
                else
                {
                    RemovePendingResults(result.Results);
                    result.ErrorCode = "E-BUILD-01";
                }
                result.Message = ex.Message;
            }

            // Only what this build made is removed. When a rebuild fails
            // that is the workpiece beside the earlier output, so the
            // workbook the previous build produced stays where it is.
            if (createdPath != null)
            {
                try
                {
                    if (File.Exists(createdPath))
                    {
                        File.Delete(createdPath);
                    }
                }
                catch (Exception cleanupException)
                {
                    result.ErrorCode = "E-BUILD-03";
                    result.Message =
                        result.Message +
                        " Output cleanup failed: " +
                        cleanupException.Message;
                }
            }
            // A rebuild that failed after moving the earlier workbook
            // aside puts it back, so the run folder keeps the generation
            // it had before this attempt.
            if (asidePath != null)
            {
                try
                {
                    if (File.Exists(asidePath))
                    {
                        if (File.Exists(result.OutputPath))
                        {
                            File.Delete(result.OutputPath);
                        }
                        File.Move(asidePath, result.OutputPath);
                    }
                }
                catch (Exception restoreException)
                {
                    result.ErrorCode = "E-BUILD-03";
                    result.Message =
                        result.Message +
                        " The earlier output is still at " +
                        asidePath + ": " +
                        restoreException.Message;
                }
            }

            result.Success = false;
            return FinishBuildResult(result, started);
        }

        private static BookBuildResult FinishBuildResult(
            BookBuildResult result,
            DateTime started)
        {
            result.ElapsedMilliseconds = (long)(
                DateTime.UtcNow - started).TotalMilliseconds;
            return result;
        }

        private static string GetBuildOutputPath(string outputPath)
        {
            if (string.IsNullOrEmpty(outputPath))
            {
                throw new IOException(
                    "The build output path is empty.");
            }

            try
            {
                return Path.GetFullPath(outputPath);
            }
            catch (Exception ex)
            {
                if (!(ex is ArgumentException) &&
                    !IsBuildIoException(ex))
                {
                    throw;
                }
                throw new IOException(
                    "The build output path is invalid.",
                    ex);
            }
        }

        private static Dictionary<string, string> PrepareBuildChanges(
            VbaProjectData project,
            IDictionary<string, string> requestedChanges,
            List<ModuleBuildResult> results)
        {
            Dictionary<string, VbaModule> modules =
                new Dictionary<string, VbaModule>(
                    StringComparer.OrdinalIgnoreCase);
            int index;
            for (index = 0; index < project.Modules.Count; index++)
            {
                modules.Add(
                    project.Modules[index].Name,
                    project.Modules[index]);
            }

            Dictionary<string, string> changed =
                new Dictionary<string, string>(
                    StringComparer.OrdinalIgnoreCase);
            HashSet<string> seen =
                new HashSet<string>(
                    StringComparer.OrdinalIgnoreCase);
            foreach (KeyValuePair<string, string> requested
                in requestedChanges)
            {
                VbaModule module;
                if (!modules.TryGetValue(requested.Key, out module))
                {
                    throw new InvalidDataException(
                        "VBA module was not found: " + requested.Key);
                }
                if (requested.Value == null)
                {
                    throw new ArgumentException(
                        "A VBA module change is null.",
                        "requestedChanges");
                }
                if (!seen.Add(module.Name))
                {
                    throw new InvalidDataException(
                        "Duplicate VBA module change: " + module.Name);
                }

                ModuleBuildResult item = new ModuleBuildResult();
                item.Name = module.Name;
                if (string.Equals(
                    requested.Value,
                    module.FullCode,
                    StringComparison.Ordinal))
                {
                    item.Result = "skipped_no_change";
                }
                else
                {
                    changed.Add(module.Name, requested.Value);
                }
                results.Add(item);
            }

            return changed;
        }

        private static List<VbaModuleAddition> PrepareBuildAdditions(
            VbaProjectData project,
            IList<VbaModuleAddition> requestedAdditions,
            List<ModuleBuildResult> results)
        {
            HashSet<string> names = new HashSet<string>(
                StringComparer.OrdinalIgnoreCase);
            int index;
            for (index = 0; index < project.Modules.Count; index++)
            {
                names.Add(project.Modules[index].Name);
            }

            List<VbaModuleAddition> additions =
                new List<VbaModuleAddition>();
            for (index = 0;
                index < requestedAdditions.Count;
                index++)
            {
                VbaModuleAddition requested =
                    requestedAdditions[index];
                if (requested == null)
                {
                    throw new ArgumentException(
                        "A VBA module addition is null.",
                        "requestedAdditions");
                }
                VbaProjectWriter.ValidateNewModuleName(
                    requested.Name);
                if (requested.Code == null)
                {
                    throw new ArgumentException(
                        "A VBA module addition code is null.",
                        "requestedAdditions");
                }
                if (!names.Add(requested.Name))
                {
                    throw new InvalidDataException(
                        "Duplicate VBA module name: " +
                        requested.Name);
                }

                additions.Add(
                    new VbaModuleAddition(
                        requested.Name,
                        requested.Code));
                ModuleBuildResult item =
                    new ModuleBuildResult();
                item.Name = requested.Name;
                results.Add(item);
            }
            return additions;
        }

        private static void SetPendingResults(
            List<ModuleBuildResult> results,
            string status,
            string message)
        {
            int index;
            for (index = 0; index < results.Count; index++)
            {
                if (results[index].Result.Length == 0)
                {
                    results[index].Result = status;
                    results[index].Message = message;
                }
            }
        }

        private static void RemovePendingResults(
            List<ModuleBuildResult> results)
        {
            int index;
            for (index = results.Count - 1; index >= 0; index--)
            {
                if (results[index].Result.Length == 0)
                {
                    results.RemoveAt(index);
                }
            }
        }

        // A signature signs the VBA project. This build rewrote the VBA
        // project, so any signature the source carried no longer matches
        // what it is attached to.
        //
        // The output is a copy of the original file, so parts nobody
        // touches ride along - which is right for every other part and
        // wrong for this one: the copy would carry a signature that
        // cannot verify. "It has a signature" is exactly the claim a
        // reader uses to decide the file is safe to run, so leaving a
        // broken one behind is worse than having none.
        //
        // Removing the .bin alone would leave a relationship pointing at
        // a part that no longer exists and a content-type override for
        // it, which is a malformed package. All three go together.
        private const string SignaturePartMarker = "vbaprojectsignature";

        private static bool IsSignaturePart(string fullName)
        {
            if (string.IsNullOrEmpty(fullName))
            {
                return false;
            }
            return fullName.Replace('\\', '/').ToLowerInvariant().IndexOf(
                SignaturePartMarker,
                StringComparison.Ordinal) >= 0;
        }

        private static string ReadZipEntryText(ZipArchiveEntry entry)
        {
            using (Stream input = entry.Open())
            using (StreamReader reader = new StreamReader(
                input,
                new UTF8Encoding(false),
                true))
            {
                return reader.ReadToEnd();
            }
        }

        // Office writes these parts as UTF-8 with no byte order mark, and
        // they are read back by the same package readers that wrote them,
        // so they go back the same way.
        private static void WriteZipEntryText(
            ZipArchiveEntry entry,
            string text)
        {
            byte[] bytes = new UTF8Encoding(false).GetBytes(text);

            using (Stream output = entry.Open())
            {
                output.SetLength(0);
                output.Write(bytes, 0, bytes.Length);
                output.Flush();
            }
        }

        // Drops every <elementName .../> whose text names a signature
        // part. These parts hold attribute-only elements whose values are
        // part names and URIs, so a '>' can only end the element - any
        // in a value would be written as &gt;.
        private static string RemoveSignatureElements(
            string xml,
            string elementName)
        {
            StringBuilder builder = new StringBuilder(xml.Length);
            int position = 0;

            while (true)
            {
                int start = xml.IndexOf(
                    elementName,
                    position,
                    StringComparison.Ordinal);
                int end;
                string element;

                if (start < 0)
                {
                    break;
                }
                end = xml.IndexOf('>', start);
                if (end < 0)
                {
                    break;
                }
                element = xml.Substring(start, end - start + 1);
                builder.Append(xml, position, start - position);
                if (!IsSignaturePart(element))
                {
                    builder.Append(element);
                }
                position = end + 1;
            }
            builder.Append(xml, position, xml.Length - position);
            return builder.ToString();
        }

        // Returns true when the output carried a signature that has now
        // been taken out of it.
        private static bool RemoveVbaSignature(ZipArchive archive)
        {
            List<ZipArchiveEntry> signatureParts =
                new List<ZipArchiveEntry>();
            List<ZipArchiveEntry> relationshipParts =
                new List<ZipArchiveEntry>();
            ZipArchiveEntry contentTypes = null;
            int index;

            for (index = 0; index < archive.Entries.Count; index++)
            {
                ZipArchiveEntry entry = archive.Entries[index];
                string name = entry.FullName.Replace('\\', '/');

                if (IsSignaturePart(name))
                {
                    signatureParts.Add(entry);
                }
                else if (name.EndsWith(
                    ".rels",
                    StringComparison.OrdinalIgnoreCase))
                {
                    relationshipParts.Add(entry);
                }
                else if (string.Equals(
                    name,
                    "[Content_Types].xml",
                    StringComparison.OrdinalIgnoreCase))
                {
                    contentTypes = entry;
                }
            }
            if (signatureParts.Count == 0)
            {
                // Nothing to do, and nothing rewritten: an unsigned
                // workbook comes through this build untouched.
                return false;
            }

            for (index = 0; index < signatureParts.Count; index++)
            {
                signatureParts[index].Delete();
            }

            // A relationship left pointing at a deleted part is what
            // makes a package refuse to open, so these go with it.
            for (index = 0; index < relationshipParts.Count; index++)
            {
                ZipArchiveEntry entry = relationshipParts[index];
                string xml = ReadZipEntryText(entry);
                string cleaned;

                if (!IsSignaturePart(xml))
                {
                    continue;
                }
                cleaned = RemoveSignatureElements(xml, "<Relationship ");
                if (cleaned.IndexOf(
                    "<Relationship ",
                    StringComparison.Ordinal) < 0)
                {
                    // An empty relationship part is what a workbook that
                    // was never signed does not have at all.
                    entry.Delete();
                }
                else
                {
                    WriteZipEntryText(entry, cleaned);
                }
            }

            if (contentTypes != null)
            {
                string xml = ReadZipEntryText(contentTypes);

                if (IsSignaturePart(xml))
                {
                    WriteZipEntryText(
                        contentTypes,
                        RemoveSignatureElements(xml, "<Override "));
                }
            }
            return true;
        }

        // Returns true when a VBA signature was taken out of the output.
        private static bool WriteZipVbaProject(
            string outputPath,
            byte[] projectBytes,
            bool isZip,
            string entryName)
        {
            if (!isZip)
            {
                // An OLE2-era workbook is the VBA project; it carries no
                // package parts, so there is no signature to remove.
                File.WriteAllBytes(outputPath, projectBytes);
                return false;
            }

            using (FileStream file = new FileStream(
                outputPath,
                FileMode.Open,
                FileAccess.ReadWrite,
                FileShare.None))
            using (ZipArchive archive = new ZipArchive(
                file,
                ZipArchiveMode.Update,
                false))
            {
                string target =
                    string.IsNullOrEmpty(entryName) ?
                    "vbaProject.bin" :
                    entryName;
                ZipArchiveEntry found = null;
                int index;
                for (index = 0; index < archive.Entries.Count; index++)
                {
                    if (string.Equals(
                        archive.Entries[index].Name,
                        target,
                        StringComparison.OrdinalIgnoreCase))
                    {
                        found = archive.Entries[index];
                        break;
                    }
                }
                if (found == null)
                {
                    throw new InvalidDataException(
                        "vbaProject.bin was not found in the output.");
                }

                using (Stream output = found.Open())
                {
                    output.SetLength(0);
                    output.Write(
                        projectBytes,
                        0,
                        projectBytes.Length);
                    output.Flush();
                }

                // After the project is in place, because what makes the
                // signature stale is the project having changed.
                return RemoveVbaSignature(archive);
            }
        }

        private static void VerifyBuild(
            VbaProjectData sourceProject,
            string outputPath,
            Dictionary<string, string> changedModules,
            IList<VbaModuleAddition> newModules)
        {
            VbaProjectData outputProject;
            try
            {
                outputProject = ReadProject(outputPath);
            }
            catch (Exception ex)
            {
                throw new BuildVerificationException(
                    "The output VBA project could not be read.",
                    ex);
            }

            Dictionary<string, VbaModule> sourceModules =
                BuildModuleMap(sourceProject);
            Dictionary<string, VbaModule> outputModules =
                BuildModuleMap(outputProject);
            if (outputModules.Count !=
                sourceModules.Count + newModules.Count)
            {
                throw new BuildVerificationException(
                    "The output VBA module count is incorrect.");
            }

            HashSet<string> changedPaths =
                new HashSet<string>(StringComparer.Ordinal);
            HashSet<string> addedPaths =
                new HashSet<string>(StringComparer.Ordinal);

            foreach (KeyValuePair<string, string> change
                in changedModules)
            {
                VbaModule sourceModule;
                VbaModule outputModule;
                if (!sourceModules.TryGetValue(
                    change.Key,
                    out sourceModule) ||
                    !outputModules.TryGetValue(
                        change.Key,
                        out outputModule))
                {
                    throw new BuildVerificationException(
                        "A changed VBA module is missing: " +
                        change.Key);
                }

                if (!string.Equals(
                    NormalizeCrLf(outputModule.FullCode),
                    NormalizeCrLf(change.Value),
                    StringComparison.Ordinal))
                {
                    throw new BuildVerificationException(
                        "Changed VBA source mismatch: " +
                        change.Key);
                }

                changedPaths.Add(
                    GetEntryPath(
                        sourceProject.Ole2,
                        sourceModule.StreamEntry));
            }

            int index;
            for (index = 0; index < newModules.Count; index++)
            {
                VbaModuleAddition addition = newModules[index];
                VbaModule outputModule;
                if (sourceModules.ContainsKey(addition.Name) ||
                    !outputModules.TryGetValue(
                        addition.Name,
                        out outputModule))
                {
                    throw new BuildVerificationException(
                        "An added VBA module is missing: " +
                        addition.Name);
                }
                if (outputModule.Kind !=
                        VbaModuleKind.Standard ||
                    outputModule.SourceOffset != 0 ||
                    !string.Equals(
                        NormalizeCrLf(outputModule.FullCode),
                        NormalizeCrLf(
                            VbaProjectWriter.
                                CreateNewModuleFullCode(
                                    addition.Name,
                                    addition.Code)),
                        StringComparison.Ordinal))
                {
                    throw new BuildVerificationException(
                        "Added VBA module mismatch: " +
                        addition.Name);
                }

                addedPaths.Add(
                    GetEntryPath(
                        outputProject.Ole2,
                        outputModule.StreamEntry));
            }

            if (newModules.Count > 0)
            {
                if (sourceProject.ProjectEntry == null ||
                    sourceProject.ProjectWmEntry == null)
                {
                    throw new BuildVerificationException(
                        "VBA project metadata streams are missing.");
                }
                changedPaths.Add(
                    GetEntryPath(
                        sourceProject.Ole2,
                        sourceProject.ProjectEntry));
                changedPaths.Add(
                    GetEntryPath(
                        sourceProject.Ole2,
                        sourceProject.ProjectWmEntry));
            }

            if (changedModules.Count > 0 || newModules.Count > 0)
            {
                VerifyCompiledStateDropped(
                    sourceProject,
                    outputProject,
                    changedModules,
                    changedPaths);
            }

            VerifyLogicalEntries(
                sourceProject.Ole2,
                outputProject.Ole2,
                changedPaths,
                addedPaths);
        }

        // A build that rewrites any code drops the whole project's compiled
        // state, because Excel runs that state in preference to the source
        // and a project where only some modules still carry it will not
        // open. So the streams holding it are expected to differ, and the
        // byte comparison in VerifyLogicalEntries cannot cover them.
        //
        // Exempting them without putting anything in their place is what
        // hid the original defect: the build read its own source back,
        // agreed with itself, and reported success while Excel showed the
        // code we had replaced. These checks are the replacement - the
        // untouched modules must still carry identical source, and the
        // compiled state must genuinely be gone rather than merely allowed
        // to differ.
        private static void VerifyCompiledStateDropped(
            VbaProjectData sourceProject,
            VbaProjectData outputProject,
            Dictionary<string, string> changedModules,
            HashSet<string> changedPaths)
        {
            if (sourceProject.DirEntry == null)
            {
                throw new BuildVerificationException(
                    "VBA project metadata streams are missing.");
            }
            changedPaths.Add(
                GetEntryPath(
                    sourceProject.Ole2,
                    sourceProject.DirEntry));

            Dictionary<string, VbaModule> outputModules =
                BuildModuleMap(outputProject);
            int index;
            for (index = 0; index < sourceProject.Modules.Count; index++)
            {
                VbaModule before = sourceProject.Modules[index];
                VbaModule after;
                if (!outputModules.TryGetValue(before.Name, out after))
                {
                    throw new BuildVerificationException(
                        "A VBA module is missing after build: " +
                        before.Name);
                }

                if (before.StreamEntry != null)
                {
                    changedPaths.Add(
                        GetEntryPath(
                            sourceProject.Ole2,
                            before.StreamEntry));
                }
                if (after.SourceOffset != 0)
                {
                    throw new BuildVerificationException(
                        "A VBA module kept its compiled code: " +
                        before.Name);
                }
                if (!changedModules.ContainsKey(before.Name) &&
                    !string.Equals(
                        NormalizeCrLf(before.FullCode),
                        NormalizeCrLf(after.FullCode),
                        StringComparison.Ordinal))
                {
                    throw new BuildVerificationException(
                        "An unchanged VBA module was altered: " +
                        before.Name);
                }
            }

            if (outputProject.VbaStorage == null ||
                sourceProject.VbaStorage == null)
            {
                throw new BuildVerificationException(
                    "The VBA storage is missing after build.");
            }

            for (index = 0;
                index < sourceProject.VbaStorage.Children.Count;
                index++)
            {
                Ole2DirectoryEntry child = sourceProject.Ole2.Entries[
                    sourceProject.VbaStorage.Children[index]];
                if (child.ObjectType != 2)
                {
                    continue;
                }
                if (child.Name.StartsWith(
                        "__SRP_",
                        StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(
                        child.Name,
                        "_VBA_PROJECT",
                        StringComparison.OrdinalIgnoreCase))
                {
                    changedPaths.Add(
                        GetEntryPath(sourceProject.Ole2, child));
                }
            }

            bool sawVersionStamp = false;
            for (index = 0;
                index < outputProject.VbaStorage.Children.Count;
                index++)
            {
                Ole2DirectoryEntry child = outputProject.Ole2.Entries[
                    outputProject.VbaStorage.Children[index]];
                if (child.ObjectType != 2)
                {
                    continue;
                }
                if (child.Name.StartsWith(
                    "__SRP_",
                    StringComparison.OrdinalIgnoreCase))
                {
                    if (child.Size != 0)
                    {
                        throw new BuildVerificationException(
                            "A VBA compiled cache stream survived: " +
                            child.Name);
                    }
                }
                else if (string.Equals(
                    child.Name,
                    "_VBA_PROJECT",
                    StringComparison.OrdinalIgnoreCase))
                {
                    byte[] stamp = outputProject.Ole2.ReadStream(child);
                    if (stamp.Length < 4 ||
                        stamp[2] != 0xFF ||
                        stamp[3] != 0xFF)
                    {
                        throw new BuildVerificationException(
                            "The output VBA project still claims a " +
                            "compiled version.");
                    }
                    sawVersionStamp = true;
                }
            }

            if (!sawVersionStamp)
            {
                throw new BuildVerificationException(
                    "The output _VBA_PROJECT stream is missing.");
            }
        }

        private static Dictionary<string, VbaModule> BuildModuleMap(
            VbaProjectData project)
        {
            Dictionary<string, VbaModule> result =
                new Dictionary<string, VbaModule>(
                    StringComparer.OrdinalIgnoreCase);
            int index;
            for (index = 0; index < project.Modules.Count; index++)
            {
                VbaModule module = project.Modules[index];
                if (result.ContainsKey(module.Name))
                {
                    throw new BuildVerificationException(
                        "Duplicate VBA module after build: " +
                        module.Name);
                }
                result.Add(module.Name, module);
            }
            return result;
        }

        private static void VerifyLogicalEntries(
            Ole2File source,
            Ole2File output,
            HashSet<string> changedPaths,
            HashSet<string> addedPaths)
        {
            Dictionary<string, Ole2DirectoryEntry> sourceEntries =
                BuildEntryMap(source);
            Dictionary<string, Ole2DirectoryEntry> outputEntries =
                BuildEntryMap(output);
            if (outputEntries.Count !=
                sourceEntries.Count + addedPaths.Count)
            {
                throw new BuildVerificationException(
                    "OLE2 logical entry count changed.");
            }

            foreach (KeyValuePair<string, Ole2DirectoryEntry> pair
                in sourceEntries)
            {
                Ole2DirectoryEntry after;
                if (!outputEntries.TryGetValue(pair.Key, out after))
                {
                    throw new BuildVerificationException(
                        "OLE2 logical entry is missing: " + pair.Key);
                }

                Ole2DirectoryEntry before = pair.Value;
                if (!string.Equals(
                    before.Name,
                    after.Name,
                    StringComparison.Ordinal) ||
                    before.ObjectType != after.ObjectType ||
                    before.ClassId != after.ClassId ||
                    before.StateBits != after.StateBits ||
                    before.CreationTimeRaw != after.CreationTimeRaw ||
                    before.ModifiedTimeRaw != after.ModifiedTimeRaw)
                {
                    throw new BuildVerificationException(
                        "OLE2 logical metadata changed: " + pair.Key);
                }

                if (before.ObjectType == 2 &&
                    !changedPaths.Contains(pair.Key))
                {
                    byte[] beforeBytes = source.ReadStream(before);
                    byte[] afterBytes = output.ReadStream(after);
                    int difference = FirstByteDifference(
                        beforeBytes,
                        afterBytes);
                    if (difference != -1)
                    {
                        throw new BuildVerificationException(
                            "OLE2 stream changed: " +
                            pair.Key +
                            " at byte " +
                            difference);
                    }
                }
            }

            foreach (KeyValuePair<string, Ole2DirectoryEntry> pair
                in outputEntries)
            {
                if (sourceEntries.ContainsKey(pair.Key))
                {
                    continue;
                }
                if (!addedPaths.Contains(pair.Key) ||
                    pair.Value.ObjectType != 2)
                {
                    throw new BuildVerificationException(
                        "Unexpected OLE2 logical entry: " +
                        pair.Key);
                }
            }
        }

        private static Dictionary<string, Ole2DirectoryEntry> BuildEntryMap(
            Ole2File file)
        {
            Dictionary<string, Ole2DirectoryEntry> result =
                new Dictionary<string, Ole2DirectoryEntry>(
                    StringComparer.Ordinal);
            int index;
            for (index = 0; index < file.Entries.Count; index++)
            {
                Ole2DirectoryEntry entry = file.Entries[index];
                if (entry.ObjectType == 0)
                {
                    continue;
                }

                string path = GetEntryPath(file, entry);
                if (result.ContainsKey(path))
                {
                    throw new BuildVerificationException(
                        "Duplicate OLE2 logical path: " + path);
                }
                result.Add(path, entry);
            }
            return result;
        }

        private static string GetEntryPath(
            Ole2File file,
            Ole2DirectoryEntry entry)
        {
            if (entry.Id == 0)
            {
                return "\\";
            }

            List<string> parts = new List<string>();
            HashSet<int> visited = new HashSet<int>();
            Ole2DirectoryEntry current = entry;
            while (current.Id != 0)
            {
                if (!visited.Add(current.Id) ||
                    current.ParentId < 0 ||
                    current.ParentId >= file.Entries.Count)
                {
                    throw new BuildVerificationException(
                        "Invalid OLE2 parent chain: " + entry.Name);
                }

                parts.Insert(0, current.Name);
                current = file.Entries[current.ParentId];
            }

            return "\\" + string.Join("\\", parts.ToArray());
        }

        private static int FirstByteDifference(
            byte[] left,
            byte[] right)
        {
            int length = Math.Min(left.Length, right.Length);
            int index;
            for (index = 0; index < length; index++)
            {
                if (left[index] != right[index])
                {
                    return index;
                }
            }
            return left.Length == right.Length ? -1 : length;
        }

        private static string NormalizeCrLf(string value)
        {
            return value.Replace("\r\n", "\n")
                .Replace("\r", "\n")
                .Replace("\n", "\r\n");
        }

        private static bool IsBuildIoException(Exception ex)
        {
            return ex is IOException ||
                ex is UnauthorizedAccessException ||
                ex is NotSupportedException ||
                ex is PathTooLongException;
        }

        private static string GetFullPath(string filePath)
        {
            if (string.IsNullOrEmpty(filePath))
            {
                throw new MacroStudioException(
                    "E-ATTACH-02",
                    "The workbook path is empty.");
            }

            try
            {
                return Path.GetFullPath(filePath);
            }
            catch (Exception ex)
            {
                if (!(ex is ArgumentException) &&
                    !(ex is NotSupportedException) &&
                    !(ex is PathTooLongException))
                {
                    throw;
                }

                throw new MacroStudioException(
                    "E-ATTACH-02",
                    "The workbook path is invalid.",
                    ex);
            }
        }


        private static byte[] ReadBookBytes(string fullPath)
        {
            try
            {
                using (FileStream input = new FileStream(
                    fullPath,
                    FileMode.Open,
                    FileAccess.Read,
                    FileShare.ReadWrite | FileShare.Delete))
                {
                    if (input.Length > int.MaxValue)
                    {
                        throw new MacroStudioException(
                            "E-ATTACH-02",
                            "The workbook is too large to read.");
                    }

                    using (MemoryStream memory = new MemoryStream(
                        (int)input.Length))
                    {
                        input.CopyTo(memory);
                        return memory.ToArray();
                    }
                }
            }
            catch (MacroStudioException)
            {
                throw;
            }
            catch (Exception ex)
            {
                if (!(ex is IOException) &&
                    !(ex is UnauthorizedAccessException) &&
                    !(ex is NotSupportedException))
                {
                    throw;
                }

                throw new MacroStudioException(
                    "E-ATTACH-02",
                    "The workbook could not be opened.",
                    ex);
            }
        }


        private static bool HasOle2Signature(byte[] bytes)
        {
            byte[] signature = new byte[]
            {
                0xD0, 0xCF, 0x11, 0xE0, 0xA1, 0xB1, 0x1A, 0xE1
            };
            if (bytes.Length < signature.Length)
            {
                return false;
            }

            int index;
            for (index = 0; index < signature.Length; index++)
            {
                if (bytes[index] != signature[index])
                {
                    return false;
                }
            }
            return true;
        }

        private static byte[] TryFindEmbeddedVbaContainer(byte[] bytes)
        {
            byte[] signature = new byte[]
            {
                0xD0, 0xCF, 0x11, 0xE0, 0xA1, 0xB1, 0x1A, 0xE1
            };
            int position = 0;
            while (position + signature.Length <= bytes.Length)
            {
                int found = IndexOfBytesFrom(bytes, signature, position);
                if (found < 0)
                {
                    return null;
                }

                int length = bytes.Length - found;
                byte[] slice = new byte[length];
                Buffer.BlockCopy(bytes, found, slice, 0, length);
                if (LooksLikeVbaProject(slice))
                {
                    return slice;
                }
                position = found + 1;
            }

            return null;
        }

        private static bool LooksLikeVbaProject(byte[] bytes)
        {
            if (!HasOle2Signature(bytes))
            {
                return false;
            }

            // A VBA project container names its "VBA" storage in UTF-16
            // inside the directory sectors.
            byte[] marker = new byte[]
            {
                0x56, 0x00, 0x42, 0x00, 0x41, 0x00
            };
            return IndexOfBytes(bytes, marker) >= 0;
        }

        private static int GetVbaEntryRank(string name)
        {
            if (name == null)
            {
                return -1;
            }
            if (string.Equals(name, "vbaProject.bin", StringComparison.Ordinal))
            {
                return 0;
            }
            if (string.Equals(
                name,
                "vbaProject.bin",
                StringComparison.OrdinalIgnoreCase))
            {
                return 1;
            }
            if (name.EndsWith(".bin", StringComparison.OrdinalIgnoreCase))
            {
                return 2;
            }

            return -1;
        }

        private static string GetZipFileName(string fullName)
        {
            if (fullName == null)
            {
                return string.Empty;
            }

            int cut = fullName.LastIndexOfAny(
                new char[] { '/', '\\' });
            return cut < 0 ? fullName : fullName.Substring(cut + 1);
        }

        private static string DecodeZipName(
            byte[] bytes,
            int offset,
            int length)
        {
            try
            {
                return Encoding.UTF8.GetString(bytes, offset, length);
            }
            catch (ArgumentException)
            {
                return string.Empty;
            }
        }

        private static int ReadZipUInt16(byte[] bytes, long offset)
        {
            if (offset < 0 || offset + 2 > bytes.Length)
            {
                throw new InvalidDataException(
                    "Unexpected end of ZIP data.");
            }

            return BitConverter.ToUInt16(bytes, (int)offset);
        }

        private static long ReadZipUInt32(byte[] bytes, long offset)
        {
            if (offset < 0 || offset + 4 > bytes.Length)
            {
                throw new InvalidDataException(
                    "Unexpected end of ZIP data.");
            }

            return BitConverter.ToUInt32(bytes, (int)offset);
        }

        // archiveEnumerated reports whether the file read as an archive at
        // all, separately from whether it carried VBA. The caller needs the
        // difference: a readable container with no VBA is a macro-free
        // workbook, while a file that never enumerated is not a workbook we
        // managed to read, and the two must not be described the same way.
        private static byte[] TryReadZipVbaProject(
            byte[] bookBytes,
            out string entryName,
            out bool archiveEnumerated,
            ref bool hadWarnings)
        {
            bool enumerated;
            byte[] result = TryReadZipVbaProjectArchive(
                bookBytes,
                out enumerated,
                out entryName,
                ref hadWarnings);
            archiveEnumerated = enumerated;
            if (result != null)
            {
                return result;
            }
            if (enumerated)
            {
                // The archive was fully readable and holds no VBA part.
                return null;
            }

            // The standard ZIP reader could not enumerate the archive.
            // Retry via the central directory, then via a raw scan for
            // local file headers, so damaged-but-recoverable archives
            // still yield their VBA project.
            result = TryReadZipVbaProjectCentral(bookBytes, out entryName);
            if (result != null)
            {
                hadWarnings = true;
                return result;
            }

            result = TryReadZipVbaProjectScan(bookBytes, out entryName);
            if (result != null)
            {
                hadWarnings = true;
            }
            return result;
        }

        private static byte[] TryReadZipVbaProjectArchive(
            byte[] bookBytes,
            out bool enumerated,
            out string entryName,
            ref bool hadWarnings)
        {
            enumerated = false;
            entryName = null;
            try
            {
                using (MemoryStream memory = new MemoryStream(
                    bookBytes,
                    false))
                using (ZipArchive archive = new ZipArchive(
                    memory,
                    ZipArchiveMode.Read,
                    false))
                {
                    List<ZipArchiveEntry> entries =
                        new List<ZipArchiveEntry>();
                    int index;
                    for (index = 0;
                        index < archive.Entries.Count;
                        index++)
                    {
                        entries.Add(archive.Entries[index]);
                    }
                    enumerated = true;

                    int rank;
                    for (rank = 0; rank <= 2; rank++)
                    {
                        for (index = 0; index < entries.Count; index++)
                        {
                            ZipArchiveEntry entry = entries[index];
                            if (GetVbaEntryRank(entry.Name) != rank)
                            {
                                continue;
                            }

                            byte[] data;
                            try
                            {
                                data = ReadZipEntry(entry);
                            }
                            catch (InvalidDataException)
                            {
                                hadWarnings = true;
                                enumerated = false;
                                continue;
                            }

                            if (rank == 0 || LooksLikeVbaProject(data))
                            {
                                if (rank > 0)
                                {
                                    hadWarnings = true;
                                }
                                entryName = entry.Name;
                                return data;
                            }
                        }
                    }
                }
            }
            catch (InvalidDataException)
            {
                hadWarnings = true;
            }
            catch (NotSupportedException)
            {
                hadWarnings = true;
            }
            catch (IOException)
            {
                hadWarnings = true;
            }
            return null;
        }

        private static byte[] ReadZipEntry(ZipArchiveEntry entry)
        {
            if (entry.Length > int.MaxValue)
            {
                throw new InvalidDataException(
                    "vbaProject.bin is too large to read.");
            }

            using (Stream input = entry.Open())
            using (MemoryStream output = new MemoryStream())
            {
                input.CopyTo(output);
                return output.ToArray();
            }
        }

        private static long FindZipEndOfCentralDirectory(byte[] bytes)
        {
            long lowest = (long)bytes.Length - 22L - 65557L;
            if (lowest < 0)
            {
                lowest = 0;
            }

            long position;
            for (position = (long)bytes.Length - 22L;
                position >= lowest;
                position--)
            {
                if (bytes[position] == 0x50 &&
                    bytes[position + 1] == 0x4B &&
                    bytes[position + 2] == 0x05 &&
                    bytes[position + 3] == 0x06)
                {
                    return position;
                }
            }

            return -1;
        }

        private static byte[] TryReadZipVbaProjectCentral(
            byte[] bytes,
            out string entryName)
        {
            entryName = null;
            try
            {
                long endOfDirectory =
                    FindZipEndOfCentralDirectory(bytes);
                if (endOfDirectory < 0)
                {
                    return null;
                }

                int entryCount = ReadZipUInt16(
                    bytes,
                    endOfDirectory + 10);
                long position = ReadZipUInt32(
                    bytes,
                    endOfDirectory + 16);
                List<string> names = new List<string>();
                List<int> methods = new List<int>();
                List<long> compressedSizes = new List<long>();
                List<long> localOffsets = new List<long>();
                int parsed = 0;
                while (parsed < entryCount &&
                    position + 46 <= bytes.Length &&
                    ReadZipUInt32(bytes, position) == 0x02014B50L)
                {
                    int method = ReadZipUInt16(bytes, position + 10);
                    long compressedSize = ReadZipUInt32(
                        bytes,
                        position + 20);
                    int nameLength = ReadZipUInt16(bytes, position + 28);
                    int extraLength = ReadZipUInt16(bytes, position + 30);
                    int commentLength = ReadZipUInt16(
                        bytes,
                        position + 32);
                    long localOffset = ReadZipUInt32(
                        bytes,
                        position + 42);
                    if (position + 46 + nameLength > bytes.Length)
                    {
                        break;
                    }

                    names.Add(GetZipFileName(DecodeZipName(
                        bytes,
                        (int)(position + 46),
                        nameLength)));
                    methods.Add(method);
                    compressedSizes.Add(compressedSize);
                    localOffsets.Add(localOffset);
                    position += 46 + nameLength + extraLength +
                        commentLength;
                    parsed++;
                }

                int rank;
                for (rank = 0; rank <= 2; rank++)
                {
                    int index;
                    for (index = 0; index < names.Count; index++)
                    {
                        if (GetVbaEntryRank(names[index]) != rank)
                        {
                            continue;
                        }

                        byte[] data = ReadZipLocalData(
                            bytes,
                            localOffsets[index],
                            methods[index],
                            compressedSizes[index]);
                        if (data == null)
                        {
                            continue;
                        }
                        if (rank == 0 || LooksLikeVbaProject(data))
                        {
                            entryName = names[index];
                            return data;
                        }
                    }
                }
            }
            catch (OutOfMemoryException)
            {
                throw;
            }
            catch (Exception)
            {
            }
            return null;
        }

        private static byte[] TryReadZipVbaProjectScan(
            byte[] bytes,
            out string entryName)
        {
            entryName = null;
            byte[] best = null;
            int bestRank = 3;
            try
            {
                long position;
                for (position = 0;
                    position + 30 <= bytes.Length;
                    position++)
                {
                    if (bytes[position] != 0x50 ||
                        bytes[position + 1] != 0x4B ||
                        bytes[position + 2] != 0x03 ||
                        bytes[position + 3] != 0x04)
                    {
                        continue;
                    }

                    int flags = ReadZipUInt16(bytes, position + 6);
                    int method = ReadZipUInt16(bytes, position + 8);
                    long compressedSize = ReadZipUInt32(
                        bytes,
                        position + 18);
                    int nameLength = ReadZipUInt16(bytes, position + 26);
                    if (position + 30 + nameLength > bytes.Length)
                    {
                        continue;
                    }

                    string name = GetZipFileName(DecodeZipName(
                        bytes,
                        (int)(position + 30),
                        nameLength));
                    int rank = GetVbaEntryRank(name);
                    if (rank < 0 || rank >= bestRank)
                    {
                        continue;
                    }

                    if ((flags & 0x0008) != 0)
                    {
                        // A data descriptor hides the sizes: inflate to
                        // the end of the deflate stream instead.
                        compressedSize = 0;
                    }
                    byte[] data = ReadZipLocalDataAt(
                        bytes,
                        position,
                        method,
                        compressedSize);
                    if (data == null)
                    {
                        continue;
                    }
                    if (rank > 0 && !LooksLikeVbaProject(data))
                    {
                        continue;
                    }

                    best = data;
                    bestRank = rank;
                    entryName = name;
                    if (rank == 0)
                    {
                        break;
                    }
                }
            }
            catch (OutOfMemoryException)
            {
                throw;
            }
            catch (Exception)
            {
            }
            return best;
        }

        private static byte[] ReadZipLocalData(
            byte[] bytes,
            long localOffset,
            int method,
            long compressedSize)
        {
            if (localOffset < 0 ||
                localOffset + 30 > bytes.Length ||
                ReadZipUInt32(bytes, localOffset) != 0x04034B50L)
            {
                return null;
            }

            return ReadZipLocalDataAt(
                bytes,
                localOffset,
                method,
                compressedSize);
        }

        private static byte[] ReadZipLocalDataAt(
            byte[] bytes,
            long headerPosition,
            int method,
            long compressedSize)
        {
            int nameLength = ReadZipUInt16(bytes, headerPosition + 26);
            int extraLength = ReadZipUInt16(bytes, headerPosition + 28);
            long dataStart = headerPosition + 30 + nameLength +
                extraLength;
            if (dataStart < 0 || dataStart > bytes.Length)
            {
                return null;
            }

            long available = (long)bytes.Length - dataStart;
            if (method == 0)
            {
                if (compressedSize <= 0 || compressedSize > available)
                {
                    return null;
                }

                byte[] stored = new byte[(int)compressedSize];
                Buffer.BlockCopy(
                    bytes,
                    (int)dataStart,
                    stored,
                    0,
                    stored.Length);
                return stored;
            }
            if (method != 8)
            {
                return null;
            }

            long inputLength =
                compressedSize > 0 && compressedSize <= available ?
                compressedSize :
                available;
            if (inputLength <= 0)
            {
                return null;
            }

            try
            {
                using (MemoryStream input = new MemoryStream(
                    bytes,
                    (int)dataStart,
                    (int)inputLength,
                    false))
                using (DeflateStream inflater = new DeflateStream(
                    input,
                    CompressionMode.Decompress))
                using (MemoryStream output = new MemoryStream())
                {
                    byte[] buffer = new byte[65536];
                    long total = 0;
                    while (true)
                    {
                        int read = inflater.Read(
                            buffer,
                            0,
                            buffer.Length);
                        if (read <= 0)
                        {
                            break;
                        }

                        total += read;
                        if (total > 268435456L)
                        {
                            return null;
                        }
                        output.Write(buffer, 0, read);
                    }
                    return output.ToArray();
                }
            }
            catch (InvalidDataException)
            {
                return null;
            }
        }
    }
}
