using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace MacroStudio
{
    public enum VbaModuleKind
    {
        Document = 0,
        Form = 1,
        Standard = 2,
        Class = 3
    }

    public sealed class VbaDirModule
    {
        public string Name;
        public string StreamName;
        public uint SourceOffset;
        public ushort TypeId;
        // Where MODULEOFFSET's value sits inside the decompressed dir
        // stream. The writer has to set it to zero when it drops a
        // module's compiled cache, and re-walking dir there would mean a
        // second, looser parser; this lets it reuse the strict one.
        // -1 when the record was not read from dir.
        public int SourceOffsetPosition;

        public VbaDirModule()
        {
            Name = string.Empty;
            StreamName = string.Empty;
            SourceOffsetPosition = -1;
        }
    }

    public sealed class VbaModule
    {
        public string Name;
        public string StreamName;
        public VbaModuleKind Kind;
        public string Extension;
        public uint SourceOffset;
        public byte[] StreamData;
        public byte[] FullSourceBytes;
        public string FullCode;
        public string AttributeHeader;
        public string Code;
        public Ole2DirectoryEntry StreamEntry;

        public VbaModule()
        {
            Name = string.Empty;
            StreamName = string.Empty;
            Extension = string.Empty;
            StreamData = new byte[0];
            FullSourceBytes = new byte[0];
            FullCode = string.Empty;
            AttributeHeader = string.Empty;
            Code = string.Empty;
        }
    }

    public sealed class VbaProjectData
    {
        public byte[] Ole2Bytes;
        public Ole2File Ole2;
        public int CodePage;
        public Encoding Encoding;
        public byte[] ProjectBytes;
        public string ProjectText;
        public byte[] ProjectWmBytes;
        public List<string> ProjectWmNames;
        public byte[] DirCompressed;
        public byte[] DirDecompressed;
        public int ProjectModulesOffset;
        public Ole2DirectoryEntry ProjectEntry;
        public Ole2DirectoryEntry ProjectWmEntry;
        public Ole2DirectoryEntry DirEntry;
        public Ole2DirectoryEntry VbaStorage;
        public List<VbaModule> Modules;
        // The dir records as read, kept so the writer can find each
        // MODULEOFFSET field by position when it drops compiled caches.
        public List<VbaDirModule> DirModules;
        public string FilePath;
        public bool IsZip;
        public string VbaEntryName;
        public bool HasReadWarnings;
        // HasReadWarnings says only "something did not hold", and it is
        // set in about seventy places across three layers. These fields
        // record the few findings that decide whether the VBA source
        // itself is trustworthy, so the screen can tell the user what
        // happened instead of warning about everything at once.
        //
        // PartialSourceModules: the compressed source did not decompress
        // strictly and a best-effort pass was used, so the code of those
        // modules may be cut short. This is the one finding that asks the
        // user to compare the code before and after.
        // RecoveredOffsetModules: dir's MODULEOFFSET did not point at the
        // source, which was then located by scanning the stream. The code
        // decompressed completely; only the recorded position was wrong.
        // UnreadableModules: the project lists the module but no source
        // could be read for it at all, so it is missing from the list.
        // ContainerFallback: vbaProject.bin was not where it should be
        // and was taken from a fallback route.
        // Salvaged: no container structure survived and the modules were
        // recovered from the raw bytes.
        public List<string> PartialSourceModules;
        public List<string> RecoveredOffsetModules;
        public List<string> UnreadableModules;
        public bool ContainerFallback;
        public bool Salvaged;

        public VbaProjectData()
        {
            Ole2Bytes = new byte[0];
            ProjectBytes = new byte[0];
            ProjectText = string.Empty;
            ProjectWmBytes = new byte[0];
            ProjectWmNames = new List<string>();
            DirCompressed = new byte[0];
            DirDecompressed = new byte[0];
            Modules = new List<VbaModule>();
            DirModules = new List<VbaDirModule>();
            FilePath = string.Empty;
            VbaEntryName = string.Empty;
            ProjectModulesOffset = -1;
            HasReadWarnings = false;
            PartialSourceModules = new List<string>();
            RecoveredOffsetModules = new List<string>();
            UnreadableModules = new List<string>();
            ContainerFallback = false;
            Salvaged = false;
        }

        // Whether anything found while reading can have changed the VBA
        // source that the request and the write-back are built from.
        public bool HasSourceDoubt()
        {
            return PartialSourceModules.Count > 0 ||
                UnreadableModules.Count > 0 ||
                Salvaged ||
                (Ole2 != null && Ole2.HasShortStreamRead);
        }
    }

    public static class VbaProjectReader
    {
        public static VbaProjectData Read(byte[] ole2Bytes)
        {
            if (ole2Bytes == null)
            {
                throw new ArgumentNullException("ole2Bytes");
            }

            // A healthy workbook takes exactly the strict path below.
            // Anything that does not hold is recorded as a warning and the
            // reader keeps going, so a structural defect never costs the
            // VBA source that is physically present.
            VbaProjectData project = new VbaProjectData();
            Ole2File ole2 = Ole2File.Parse(ole2Bytes);
            project.Ole2Bytes = ole2Bytes;
            project.Ole2 = ole2;
            project.HasReadWarnings = ole2.HasReadWarnings;

            Ole2DirectoryEntry vbaStorage = FindEntry(
                project,
                null,
                "VBA",
                1);
            Ole2DirectoryEntry projectEntry = FindEntry(
                project,
                null,
                "PROJECT",
                2);
            Ole2DirectoryEntry projectWmEntry = FindEntry(
                project,
                null,
                "PROJECTwm",
                2);
            Ole2DirectoryEntry dirEntry = FindEntry(
                project,
                vbaStorage,
                "dir",
                2);
            if (projectEntry == null || vbaStorage == null ||
                dirEntry == null)
            {
                project.HasReadWarnings = true;
            }

            byte[] dirCompressed = new byte[0];
            byte[] dirDecompressed = new byte[0];
            int codePage = 932;
            int projectModulesOffset = -1;
            bool strictDir = false;
            List<VbaDirModule> dirModules = new List<VbaDirModule>();
            if (dirEntry != null)
            {
                dirCompressed = ole2.ReadStream(dirEntry);
                dirDecompressed = DecompressDir(project, dirCompressed);
                dirModules = ReadDirModulesPermissive(
                    dirDecompressed,
                    out codePage,
                    out projectModulesOffset,
                    out strictDir);
                if (!strictDir)
                {
                    project.HasReadWarnings = true;
                }
            }

            Encoding encoding = ResolveEncoding(codePage);
            project.CodePage = encoding.CodePage;
            project.Encoding = encoding;

            byte[] projectBytes = new byte[0];
            string projectText = string.Empty;
            if (projectEntry != null)
            {
                projectBytes = ole2.ReadStream(projectEntry);
                projectText = encoding.GetString(projectBytes);
            }
            Dictionary<string, VbaModuleKind> projectTypes =
                ParseProjectTypes(projectText);

            byte[] projectWmBytes = new byte[0];
            List<string> projectWmNames = new List<string>();
            bool projectWmMatches = false;
            if (projectWmEntry != null)
            {
                projectWmBytes = ole2.ReadStream(projectWmEntry);
                projectWmNames = ReadProjectWmNamesPermissive(
                    projectWmBytes,
                    encoding);
                projectWmMatches = MatchesDirNames(
                    projectWmNames,
                    dirModules);
            }

            project.ProjectBytes = projectBytes;
            project.ProjectText = projectText;
            project.ProjectWmBytes = projectWmBytes;
            project.ProjectWmNames = projectWmNames;
            project.DirCompressed = dirCompressed;
            project.DirDecompressed = dirDecompressed;
            project.ProjectEntry = projectEntry;
            project.ProjectWmEntry = projectWmEntry;
            project.DirEntry = dirEntry;
            project.VbaStorage = vbaStorage;
            project.DirModules = dirModules;

            if (projectTypes.Count != dirModules.Count ||
                (projectWmEntry != null && !projectWmMatches))
            {
                project.HasReadWarnings = true;
            }

            HashSet<int> usedStreamIds = new HashSet<int>();
            HashSet<string> usedModuleNames =
                new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            bool dirModulesComplete = true;
            int index;
            for (index = 0; index < dirModules.Count; index++)
            {
                VbaDirModule record = dirModules[index];
                VbaModuleKind kind;
                if (!projectTypes.TryGetValue(record.Name, out kind))
                {
                    kind = record.TypeId == 0x0022 ?
                        VbaModuleKind.Class :
                        VbaModuleKind.Standard;
                    project.HasReadWarnings = true;
                }
                else if (!MatchesModuleType(record, kind))
                {
                    project.HasReadWarnings = true;
                }

                if (!TryAddModule(
                    project,
                    record,
                    kind,
                    true,
                    usedStreamIds,
                    usedModuleNames))
                {
                    dirModulesComplete = false;
                }
            }

            // Modules that dir lost but PROJECT still lists.
            foreach (KeyValuePair<string, VbaModuleKind> pair
                in projectTypes)
            {
                if (usedModuleNames.Contains(pair.Key))
                {
                    continue;
                }

                VbaDirModule record = new VbaDirModule();
                record.Name = pair.Key;
                record.StreamName = pair.Key;
                record.SourceOffset = uint.MaxValue;
                if (TryAddModule(
                    project,
                    record,
                    pair.Value,
                    false,
                    usedStreamIds,
                    usedModuleNames))
                {
                    dirModulesComplete = false;
                    project.HasReadWarnings = true;
                }
            }

            // Streams under the VBA storage that neither dir nor PROJECT
            // named. Reached only when the project metadata itself is
            // damaged.
            if (vbaStorage != null)
            {
                int childIndex;
                for (childIndex = 0;
                    childIndex < vbaStorage.Children.Count;
                    childIndex++)
                {
                    Ole2DirectoryEntry child = ole2.Entries[
                        vbaStorage.Children[childIndex]];
                    if (child.ObjectType != 2 ||
                        usedStreamIds.Contains(child.Id) ||
                        usedModuleNames.Contains(child.Name) ||
                        IsReservedVbaStreamName(child.Name))
                    {
                        continue;
                    }

                    VbaDirModule record = new VbaDirModule();
                    record.Name = child.Name;
                    record.StreamName = child.Name;
                    record.SourceOffset = uint.MaxValue;
                    if (TryAddModule(
                        project,
                        record,
                        VbaModuleKind.Standard,
                        false,
                        usedStreamIds,
                        usedModuleNames))
                    {
                        dirModulesComplete = false;
                        project.HasReadWarnings = true;
                    }
                }
            }

            // Adding a module appends to dir, PROJECT and PROJECTwm and
            // the writer checks the recorded PROJECTMODULES count against
            // the module list. Keep the dir offset only while that
            // assumption holds: dir read strictly and every module in the
            // list came from a dir record.
            project.ProjectModulesOffset =
                strictDir &&
                dirModulesComplete &&
                project.Modules.Count == dirModules.Count ?
                projectModulesOffset :
                -1;

            project.HasReadWarnings =
                project.HasReadWarnings || ole2.HasReadWarnings;
            project.Modules.Sort(delegate(VbaModule left, VbaModule right)
            {
                int typeResult = ((int)left.Kind).CompareTo((int)right.Kind);
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

        public static List<VbaDirModule> ReadDirModules(
            byte[] dirDecompressed,
            out int codePage)
        {
            int projectModulesOffset;
            return ReadDirModules(
                dirDecompressed,
                out codePage,
                out projectModulesOffset);
        }

        public static Encoding ResolveEncoding(int codePage)
        {
            try
            {
                return Encoding.GetEncoding(codePage);
            }
            catch (ArgumentException)
            {
            }
            catch (NotSupportedException)
            {
            }

            try
            {
                return Encoding.GetEncoding(932);
            }
            catch (ArgumentException)
            {
            }
            catch (NotSupportedException)
            {
            }

            return Encoding.UTF8;
        }

        public static bool TryFindCodePage(byte[] data, out int codePage)
        {
            codePage = 0;
            if (data == null)
            {
                return false;
            }

            int position = 0;
            while (position + 6 <= data.Length)
            {
                ushort id = BitConverter.ToUInt16(data, position);
                int size = BitConverter.ToInt32(data, position + 2);
                if (size < 0 ||
                    (long)position + 6L + (long)size > data.Length)
                {
                    return false;
                }
                if (id == 0x0003 && size >= 2)
                {
                    codePage = BitConverter.ToUInt16(data, position + 6);
                    return true;
                }
                position += 6 + size;
                if (id == 0x0009 && position + 2 <= data.Length)
                {
                    position += 2;
                }
            }

            return false;
        }

        private static byte[] DecompressDir(
            VbaProjectData project,
            byte[] dirCompressed)
        {
            try
            {
                return VbaCompression.Decompress(dirCompressed);
            }
            catch (InvalidDataException)
            {
            }

            project.HasReadWarnings = true;
            bool hadWarnings;
            return VbaCompression.DecompressBestEffort(
                dirCompressed,
                out hadWarnings);
        }

        private static List<VbaDirModule> ReadDirModulesPermissive(
            byte[] dirDecompressed,
            out int codePage,
            out int projectModulesOffset,
            out bool strict)
        {
            try
            {
                List<VbaDirModule> modules = ReadDirModules(
                    dirDecompressed,
                    out codePage,
                    out projectModulesOffset);
                strict = true;
                return modules;
            }
            catch (InvalidDataException)
            {
            }
            catch (DecoderFallbackException)
            {
            }

            // The strict record walk failed. Fall back to a tolerant walk
            // that keeps whatever module records are readable; the exact
            // dir offset is dropped so the writer will not rewrite dir.
            strict = false;
            projectModulesOffset = -1;
            if (!TryFindCodePage(dirDecompressed, out codePage) ||
                codePage == 0)
            {
                codePage = 932;
            }

            Encoding encoding = ResolveEncoding(codePage);
            List<int> offsets = FindProjectModulesOffsets(dirDecompressed);
            int index;
            for (index = 0; index < offsets.Count; index++)
            {
                List<VbaDirModule> modules = ParseDirModulesTolerant(
                    dirDecompressed,
                    offsets[index],
                    encoding);
                if (modules.Count > 0)
                {
                    return modules;
                }
            }

            return new List<VbaDirModule>();
        }

        private static List<VbaDirModule> ParseDirModulesTolerant(
            byte[] data,
            int modulesOffset,
            Encoding encoding)
        {
            // Records are walked as plain id/size/payload entries, so an
            // unexpected, reordered or truncated record ends the walk
            // instead of discarding the modules already recovered.
            List<VbaDirModule> modules = new List<VbaDirModule>();
            int position = modulesOffset;
            while (position + 2 <= data.Length &&
                ReadUInt16(data, position) != 0x0019)
            {
                position += 2;
                if (position + 4 > data.Length)
                {
                    return modules;
                }

                uint size = ReadUInt32(data, position);
                position += 4;
                int available = data.Length - position;
                int step = size > (uint)available ? available : (int)size;
                position += step;
            }

            while (position + 2 <= data.Length &&
                ReadUInt16(data, position) == 0x0019)
            {
                VbaDirModule module = ParseDirModuleTolerant(
                    data,
                    ref position,
                    encoding);
                if (module == null)
                {
                    break;
                }
                modules.Add(module);
            }

            return modules;
        }

        private static VbaDirModule ParseDirModuleTolerant(
            byte[] data,
            ref int position,
            Encoding encoding)
        {
            string name = null;
            string unicodeName = null;
            string streamName = null;
            string unicodeStreamName = null;
            uint sourceOffset = 0;
            bool hasSourceOffset = false;
            ushort typeId = 0;

            while (position + 6 <= data.Length)
            {
                ushort id = ReadUInt16(data, position);
                if (id == 0x0010 || (id == 0x0019 && name != null))
                {
                    break;
                }

                position += 2;
                uint size = ReadUInt32(data, position);
                position += 4;
                int available = data.Length - position;
                int byteLength =
                    size > (uint)available ? available : (int)size;
                bool truncated = byteLength < (long)size;

                if (id == 0x0019)
                {
                    name = encoding.GetString(data, position, byteLength);
                }
                else if (id == 0x0047)
                {
                    unicodeName = Encoding.Unicode.GetString(
                        data,
                        position,
                        byteLength & ~1);
                }
                else if (id == 0x001A)
                {
                    streamName = encoding.GetString(
                        data,
                        position,
                        byteLength);
                }
                else if (id == 0x0032)
                {
                    unicodeStreamName = Encoding.Unicode.GetString(
                        data,
                        position,
                        byteLength & ~1);
                }
                else if (id == 0x0031 && byteLength >= 4)
                {
                    sourceOffset = ReadUInt32(data, position);
                    hasSourceOffset = true;
                }
                else if (id == 0x0021 || id == 0x0022)
                {
                    typeId = id;
                }

                position += byteLength;
                if (id == 0x002B || truncated)
                {
                    break;
                }
            }

            string resolvedName =
                unicodeName != null && unicodeName.Length > 0 ?
                unicodeName :
                name;
            if (string.IsNullOrEmpty(resolvedName))
            {
                return null;
            }

            string resolvedStreamName =
                unicodeStreamName != null && unicodeStreamName.Length > 0 ?
                unicodeStreamName :
                streamName;
            VbaDirModule module = new VbaDirModule();
            module.Name = resolvedName;
            module.StreamName =
                string.IsNullOrEmpty(resolvedStreamName) ?
                resolvedName :
                resolvedStreamName;
            module.SourceOffset =
                hasSourceOffset ? sourceOffset : uint.MaxValue;
            module.TypeId = typeId;
            return module;
        }

        private static List<string> ReadProjectWmNamesPermissive(
            byte[] projectWmBytes,
            Encoding encoding)
        {
            try
            {
                return ReadProjectWmNames(projectWmBytes, encoding);
            }
            catch (InvalidDataException)
            {
                return new List<string>();
            }
            catch (DecoderFallbackException)
            {
                return new List<string>();
            }
        }

        private static bool MatchesDirNames(
            List<string> projectWmNames,
            List<VbaDirModule> dirModules)
        {
            if (projectWmNames.Count != dirModules.Count)
            {
                return false;
            }

            int index;
            for (index = 0; index < dirModules.Count; index++)
            {
                if (!string.Equals(
                    projectWmNames[index],
                    dirModules[index].Name,
                    StringComparison.Ordinal))
                {
                    return false;
                }
            }

            return true;
        }

        private static bool MatchesModuleType(
            VbaDirModule record,
            VbaModuleKind kind)
        {
            ushort expectedType =
                kind == VbaModuleKind.Standard ?
                (ushort)0x0021 :
                (ushort)0x0022;
            return record.TypeId == expectedType;
        }

        private static Ole2DirectoryEntry FindEntry(
            VbaProjectData project,
            Ole2DirectoryEntry parent,
            string name,
            byte objectType)
        {
            if (parent != null)
            {
                Ole2DirectoryEntry scoped = project.Ole2.FindChild(
                    parent,
                    name,
                    objectType);
                if (scoped != null)
                {
                    return scoped;
                }
            }
            else
            {
                Ole2DirectoryEntry rootChild = project.Ole2.FindChild(
                    project.Ole2.RootEntry,
                    name,
                    objectType);
                if (rootChild != null)
                {
                    return rootChild;
                }
            }

            // The directory tree may be damaged; fall back to a flat scan
            // so an unlinked entry is still found.
            Ole2DirectoryEntry found = null;
            int index;
            for (index = 0; index < project.Ole2.Entries.Count; index++)
            {
                Ole2DirectoryEntry entry = project.Ole2.Entries[index];
                if (entry.ObjectType != objectType ||
                    !string.Equals(
                        entry.Name,
                        name,
                        StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }
                if (found != null)
                {
                    project.HasReadWarnings = true;
                    continue;
                }

                found = entry;
                project.HasReadWarnings = true;
            }

            return found;
        }

        private static bool TryAddModule(
            VbaProjectData project,
            VbaDirModule record,
            VbaModuleKind kind,
            bool useDirOffset,
            HashSet<int> usedStreamIds,
            HashSet<string> usedModuleNames)
        {
            if (record == null ||
                string.IsNullOrEmpty(record.Name) ||
                usedModuleNames.Contains(record.Name))
            {
                project.HasReadWarnings = true;
                return false;
            }

            try
            {
                return AddModuleCore(
                    project,
                    record,
                    kind,
                    useDirOffset,
                    usedStreamIds,
                    usedModuleNames);
            }
            catch (OutOfMemoryException)
            {
                throw;
            }
            catch (Exception)
            {
                // One unreadable module must not abort the others.
                project.HasReadWarnings = true;
                Record(project.UnreadableModules, record.Name);
                return false;
            }
        }

        // The same module can be reached by more than one route, so the
        // finding lists stay free of repeats.
        private static void Record(List<string> names, string name)
        {
            if (string.IsNullOrEmpty(name))
            {
                return;
            }
            int index;
            for (index = 0; index < names.Count; index++)
            {
                if (string.Equals(
                    names[index],
                    name,
                    StringComparison.OrdinalIgnoreCase))
                {
                    return;
                }
            }
            names.Add(name);
        }

        private static bool AddModuleCore(
            VbaProjectData project,
            VbaDirModule record,
            VbaModuleKind kind,
            bool useDirOffset,
            HashSet<int> usedStreamIds,
            HashSet<string> usedModuleNames)
        {
            string streamName =
                string.IsNullOrEmpty(record.StreamName) ?
                record.Name :
                record.StreamName;
            Ole2DirectoryEntry streamEntry = FindEntry(
                project,
                project.VbaStorage,
                streamName,
                2);
            if (streamEntry == null &&
                !string.Equals(
                    record.Name,
                    streamName,
                    StringComparison.OrdinalIgnoreCase))
            {
                streamEntry = FindEntry(
                    project,
                    project.VbaStorage,
                    record.Name,
                    2);
            }
            if (streamEntry == null ||
                !usedStreamIds.Add(streamEntry.Id))
            {
                project.HasReadWarnings = true;
                Record(project.UnreadableModules, record.Name);
                return false;
            }

            byte[] streamData = project.Ole2.ReadStream(streamEntry);
            int sourceOffset = -1;
            byte[] fullSourceBytes = null;
            string fullCode = null;
            byte[] offsetSourceBytes = null;
            string offsetCode = null;
            int offsetSourceOffset = -1;
            bool sourceHadWarnings = false;
            // Two different findings, kept apart: source that had to be
            // decompressed best-effort may be cut short, while a source
            // the recorded offset failed to point at is complete once it
            // has been found by scanning.
            bool partialSource = false;
            bool recoveredOffset = false;
            bool attemptHadWarnings;
            if (useDirOffset &&
                record.SourceOffset <= int.MaxValue &&
                record.SourceOffset < streamData.Length)
            {
                sourceOffset = (int)record.SourceOffset;
                TryReadSource(
                    streamData,
                    sourceOffset,
                    project.Encoding,
                    out fullSourceBytes,
                    out fullCode,
                    out attemptHadWarnings);
                sourceHadWarnings =
                    sourceHadWarnings || attemptHadWarnings;
                if (fullCode != null && HasModuleMarker(fullCode))
                {
                    partialSource = partialSource || attemptHadWarnings;
                }
                else
                {
                    // MODULEOFFSET did not yield module source. Keep the
                    // bytes as a last resort but do not trust the offset:
                    // scan the stream for the real compressed container.
                    offsetSourceBytes = fullSourceBytes;
                    offsetCode = fullCode;
                    offsetSourceOffset = sourceOffset;
                    fullSourceBytes = null;
                    fullCode = null;
                    sourceOffset = -1;
                    sourceHadWarnings = true;
                    recoveredOffset = true;
                }
            }
            if (fullSourceBytes == null)
            {
                if (TryFindSource(
                    streamData,
                    project.Encoding,
                    out sourceOffset,
                    out fullSourceBytes,
                    out fullCode,
                    out attemptHadWarnings))
                {
                    sourceHadWarnings =
                        sourceHadWarnings || attemptHadWarnings;
                    partialSource = partialSource || attemptHadWarnings;
                }
                else if (offsetCode != null && offsetCode.Length > 0)
                {
                    // Nothing better was found, so the bytes the recorded
                    // offset gave are used even though they do not look
                    // like module source.
                    fullSourceBytes = offsetSourceBytes;
                    fullCode = offsetCode;
                    sourceOffset = offsetSourceOffset;
                    sourceHadWarnings = true;
                    partialSource = true;
                }
                else
                {
                    project.HasReadWarnings = true;
                    Record(project.UnreadableModules, record.Name);
                    return false;
                }
            }
            if (sourceHadWarnings)
            {
                project.HasReadWarnings = true;
            }
            if (partialSource)
            {
                Record(project.PartialSourceModules, record.Name);
            }
            else if (recoveredOffset)
            {
                Record(project.RecoveredOffsetModules, record.Name);
            }

            string attributeHeader;
            string code;
            SplitAttributeHeader(
                fullCode,
                out attributeHeader,
                out code);

            VbaModule module = new VbaModule();
            module.Name = record.Name;
            module.StreamName = streamEntry.Name;
            module.Kind = kind;
            module.Extension = GetExtension(kind);
            module.SourceOffset = (uint)sourceOffset;
            module.StreamData = streamData;
            module.FullSourceBytes = fullSourceBytes;
            module.FullCode = fullCode;
            module.AttributeHeader = attributeHeader;
            module.Code = code;
            module.StreamEntry = streamEntry;
            project.Modules.Add(module);
            usedModuleNames.Add(module.Name);
            return true;
        }

        private static bool TryFindSource(
            byte[] streamData,
            Encoding encoding,
            out int sourceOffset,
            out byte[] fullSourceBytes,
            out string fullCode,
            out bool hadWarnings)
        {
            sourceOffset = -1;
            fullSourceBytes = null;
            fullCode = null;
            hadWarnings = false;
            int offset;
            for (offset = streamData.Length - 3; offset >= 0; offset--)
            {
                if (streamData[offset] != 0x01)
                {
                    continue;
                }
                ushort header = BitConverter.ToUInt16(
                    streamData,
                    offset + 1);
                if (((header >> 12) & 0x0007) != 3)
                {
                    continue;
                }

                byte[] candidateBytes;
                string candidateCode;
                bool candidateWarnings;
                if (TryReadSource(
                    streamData,
                    offset,
                    encoding,
                    out candidateBytes,
                    out candidateCode,
                    out candidateWarnings) &&
                    HasModuleMarker(candidateCode))
                {
                    sourceOffset = offset;
                    fullSourceBytes = candidateBytes;
                    fullCode = candidateCode;
                    hadWarnings = candidateWarnings;
                    return true;
                }
            }
            return false;
        }

        private static bool TryReadSource(
            byte[] streamData,
            int sourceOffset,
            Encoding encoding,
            out byte[] fullSourceBytes,
            out string fullCode,
            out bool hadWarnings)
        {
            fullSourceBytes = null;
            fullCode = null;
            hadWarnings = false;
            try
            {
                fullSourceBytes = VbaCompression.Decompress(
                    streamData,
                    sourceOffset);
                fullCode = encoding.GetString(fullSourceBytes);
                return true;
            }
            catch (InvalidDataException)
            {
                fullSourceBytes =
                    VbaCompression.DecompressBestEffort(
                        streamData,
                        sourceOffset,
                        out hadWarnings);
                if (fullSourceBytes.Length == 0)
                {
                    fullSourceBytes = null;
                    return false;
                }
            }
            fullCode = encoding.GetString(fullSourceBytes);
            return true;
        }

        private static bool HasModuleMarker(string code)
        {
            return code.IndexOf(
                "Attribute VB_Name",
                StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private static bool IsReservedVbaStreamName(string name)
        {
            if (string.IsNullOrEmpty(name))
            {
                return true;
            }
            if (string.Equals(
                name,
                "dir",
                StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
            if (string.Equals(
                name,
                "_VBA_PROJECT",
                StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            return name.StartsWith(
                "__SRP_",
                StringComparison.OrdinalIgnoreCase);
        }

        private static List<VbaDirModule> ReadDirModules(
            byte[] dirDecompressed,
            out int codePage,
            out int projectModulesOffset)
        {
            if (dirDecompressed == null)
            {
                throw new ArgumentNullException("dirDecompressed");
            }

            List<int> modulesOffsets =
                FindProjectModulesOffsets(dirDecompressed);
            if (modulesOffsets.Count == 0)
            {
                throw new InvalidDataException(
                    "PROJECTMODULES record was not found in dir.");
            }

            int successfulCount = 0;
            int successfulCodePage = 932;
            int successfulModulesOffset = -1;
            List<VbaDirModule> successfulModules = null;
            InvalidDataException lastFailure = null;
            int moduleIndex;
            for (moduleIndex = 0;
                moduleIndex < modulesOffsets.Count;
                moduleIndex++)
            {
                int modulesOffset = modulesOffsets[moduleIndex];
                List<int> codePages =
                    FindCodePageCandidates(
                        dirDecompressed,
                        modulesOffset);
                int codePageIndex;
                for (codePageIndex = 0;
                    codePageIndex < codePages.Count;
                    codePageIndex++)
                {
                    int candidateCodePage = codePages[codePageIndex];
                    try
                    {
                        Encoding encoding =
                            GetStrictEncoding(candidateCodePage);
                        List<VbaDirModule> modules =
                            ParseDirModules(
                                dirDecompressed,
                                modulesOffset,
                                encoding);
                        successfulCount++;
                        if (successfulCount == 1)
                        {
                            successfulCodePage = candidateCodePage;
                            successfulModulesOffset = modulesOffset;
                            successfulModules = modules;
                        }
                    }
                    catch (InvalidDataException ex)
                    {
                        lastFailure = ex;
                    }
                    catch (DecoderFallbackException ex)
                    {
                        lastFailure = new InvalidDataException(
                            "VBA dir text does not match its code page.",
                            ex);
                    }
                }
            }

            if (successfulCount == 1)
            {
                codePage = successfulCodePage;
                projectModulesOffset = successfulModulesOffset;
                return successfulModules;
            }
            if (successfulCount > 1)
            {
                throw new InvalidDataException(
                    "Multiple valid VBA dir record candidates were found.");
            }
            if (modulesOffsets.Count == 1 && lastFailure != null)
            {
                throw lastFailure;
            }

            throw new InvalidDataException(
                "No valid PROJECTMODULES record was found in dir.");
        }

        public static void SplitAttributeHeader(
            string fullCode,
            out string attributeHeader,
            out string code)
        {
            if (fullCode == null)
            {
                throw new ArgumentNullException("fullCode");
            }

            int headerLength = FindAttributeHeaderLength(fullCode);
            attributeHeader = fullCode.Substring(0, headerLength);
            code = fullCode.Substring(headerLength);
        }

        public static List<string> ReadProjectWmNames(
            byte[] data,
            Encoding encoding)
        {
            if (data == null)
            {
                throw new ArgumentNullException("data");
            }
            if (encoding == null)
            {
                throw new ArgumentNullException("encoding");
            }
            if (data.Length < 2 ||
                data[data.Length - 2] != 0 ||
                data[data.Length - 1] != 0)
            {
                throw new InvalidDataException(
                    "PROJECTwm terminator is missing.");
            }

            List<string> result = new List<string>();
            int limit = data.Length - 2;
            int position = 0;
            while (position < limit)
            {
                int ansiStart = position;
                while (position < limit && data[position] != 0)
                {
                    position++;
                }
                if (position == ansiStart || position >= limit)
                {
                    throw new InvalidDataException(
                        "PROJECTwm contains an invalid module name.");
                }

                string ansiName = encoding.GetString(
                    data,
                    ansiStart,
                    position - ansiStart);
                position++;

                int unicodeStart = position;
                while (position + 1 < limit &&
                    (data[position] != 0 ||
                     data[position + 1] != 0))
                {
                    position += 2;
                }
                if (position == unicodeStart ||
                    position + 1 >= limit ||
                    ((position - unicodeStart) & 1) != 0)
                {
                    throw new InvalidDataException(
                        "PROJECTwm contains an invalid Unicode name.");
                }

                string unicodeName = Encoding.Unicode.GetString(
                    data,
                    unicodeStart,
                    position - unicodeStart);
                position += 2;
                if (!string.Equals(
                    ansiName,
                    unicodeName,
                    StringComparison.Ordinal))
                {
                    throw new InvalidDataException(
                        "PROJECTwm module name encodings do not match.");
                }
                result.Add(unicodeName);
            }

            if (position != limit)
            {
                throw new InvalidDataException(
                    "PROJECTwm module names are misaligned.");
            }
            return result;
        }

        private static List<int> FindProjectModulesOffsets(byte[] data)
        {
            List<int> offsets = new List<int>();
            int position;
            for (position = 0; position + 16 <= data.Length; position++)
            {
                if (ReadUInt16(data, position) != 0x000F ||
                    ReadUInt32(data, position + 2) != 2 ||
                    ReadUInt16(data, position + 8) != 0x0013 ||
                    ReadUInt32(data, position + 10) != 2)
                {
                    continue;
                }

                offsets.Add(position);
            }

            return offsets;
        }

        private static List<int> FindCodePageCandidates(
            byte[] data,
            int modulesOffset)
        {
            List<int> codePages = new List<int>();
            int position;
            for (position = 0;
                position + 8 <= modulesOffset;
                position++)
            {
                if (ReadUInt16(data, position) == 0x0003 &&
                    ReadUInt32(data, position + 2) == 2)
                {
                    codePages.Add(
                        ReadUInt16(data, position + 6));
                }
            }

            if (codePages.Count == 0)
            {
                codePages.Add(932);
            }
            else if (codePages.Count == 1)
            {
                try
                {
                    Encoding.GetEncoding(codePages[0]);
                }
                catch (ArgumentException)
                {
                    codePages[0] = 932;
                }
                catch (NotSupportedException)
                {
                    codePages[0] = 932;
                }
            }

            return codePages;
        }

        private static List<VbaDirModule> ParseDirModules(
            byte[] data,
            int modulesOffset,
            Encoding encoding)
        {
            int position = modulesOffset;
            RequireId(data, ref position, 0x000F, "PROJECTMODULES");
            uint size = ReadUInt32AndAdvance(data, ref position);
            if (size != 2)
            {
                throw new InvalidDataException(
                    "Invalid PROJECTMODULES size.");
            }

            ushort count = ReadUInt16AndAdvance(data, ref position);
            RequireId(data, ref position, 0x0013, "PROJECTCOOKIE");
            size = ReadUInt32AndAdvance(data, ref position);
            if (size != 2)
            {
                throw new InvalidDataException("Invalid PROJECTCOOKIE size.");
            }
            ReadUInt16AndAdvance(data, ref position);

            List<VbaDirModule> modules = new List<VbaDirModule>();
            int index;
            for (index = 0; index < count; index++)
            {
                modules.Add(ParseDirModule(data, ref position, encoding));
            }

            RequireId(data, ref position, 0x0010, "PROJECTTERMINATOR");
            ReadUInt32AndAdvance(data, ref position);
            if (position != data.Length)
            {
                throw new InvalidDataException(
                    "Unexpected data after PROJECTTERMINATOR.");
            }

            return modules;
        }

        private static VbaDirModule ParseDirModule(
            byte[] data,
            ref int position,
            Encoding encoding)
        {
            byte[] nameBytes = ReadSizedRecord(
                data,
                ref position,
                0x0019,
                "MODULENAME");
            string name = encoding.GetString(nameBytes);
            string unicodeName = string.Empty;
            if (PeekId(data, position) == 0x0047)
            {
                byte[] unicodeNameBytes = ReadSizedRecord(
                    data,
                    ref position,
                    0x0047,
                    "MODULENAMEUNICODE");
                if ((unicodeNameBytes.Length & 1) != 0)
                {
                    throw new InvalidDataException(
                        "MODULENAMEUNICODE has an odd byte length.");
                }
                unicodeName = Encoding.Unicode.GetString(unicodeNameBytes);
            }

            RequireId(data, ref position, 0x001A, "MODULESTREAMNAME");
            byte[] streamNameBytes = ReadLengthPrefixedBytes(
                data,
                ref position,
                "MODULESTREAMNAME");
            string streamName = encoding.GetString(streamNameBytes);
            RequireId(
                data,
                ref position,
                0x0032,
                "MODULESTREAMNAME reserved");
            byte[] unicodeStreamNameBytes = ReadLengthPrefixedBytes(
                data,
                ref position,
                "MODULESTREAMNAMEUNICODE");
            if ((unicodeStreamNameBytes.Length & 1) != 0)
            {
                throw new InvalidDataException(
                    "MODULESTREAMNAMEUNICODE has an odd byte length.");
            }
            string unicodeStreamName = Encoding.Unicode.GetString(
                unicodeStreamNameBytes);

            RequireId(data, ref position, 0x001C, "MODULEDOCSTRING");
            ReadLengthPrefixedBytes(
                data,
                ref position,
                "MODULEDOCSTRING");
            RequireId(
                data,
                ref position,
                0x0048,
                "MODULEDOCSTRING reserved");
            byte[] unicodeDocString = ReadLengthPrefixedBytes(
                data,
                ref position,
                "MODULEDOCSTRINGUNICODE");
            if ((unicodeDocString.Length & 1) != 0)
            {
                throw new InvalidDataException(
                    "MODULEDOCSTRINGUNICODE has an odd byte length.");
            }

            RequireId(data, ref position, 0x0031, "MODULEOFFSET");
            uint offsetSize = ReadUInt32AndAdvance(data, ref position);
            if (offsetSize != 4)
            {
                throw new InvalidDataException("Invalid MODULEOFFSET size.");
            }
            int sourceOffsetPosition = position;
            uint sourceOffset = ReadUInt32AndAdvance(data, ref position);

            SkipFixedRecord(
                data,
                ref position,
                0x001E,
                4,
                "MODULEHELPCONTEXT");
            SkipFixedRecord(
                data,
                ref position,
                0x002C,
                2,
                "MODULECOOKIE");

            ushort typeId = ReadUInt16AndAdvance(data, ref position);
            if (typeId != 0x0021 && typeId != 0x0022)
            {
                throw new InvalidDataException("Invalid MODULETYPE record.");
            }
            SkipBytes(data, ref position, 4, "MODULETYPE reserved");

            while (PeekId(data, position) == 0x0025 ||
                PeekId(data, position) == 0x0028)
            {
                ReadUInt16AndAdvance(data, ref position);
                SkipBytes(data, ref position, 4, "module flag reserved");
            }

            RequireId(data, ref position, 0x002B, "MODULETERMINATOR");
            SkipBytes(data, ref position, 4, "MODULETERMINATOR reserved");

            if (name.Length == 0 || streamName.Length == 0)
            {
                throw new InvalidDataException(
                    "A dir module has an empty name.");
            }
            if (unicodeName.Length > 0 &&
                !string.Equals(name, unicodeName, StringComparison.Ordinal))
            {
                throw new InvalidDataException(
                    "MODULENAME and MODULENAMEUNICODE do not match.");
            }
            if (unicodeStreamName.Length > 0 &&
                !string.Equals(
                    streamName,
                    unicodeStreamName,
                    StringComparison.Ordinal))
            {
                throw new InvalidDataException(
                    "MODULESTREAMNAME encodings do not match.");
            }

            VbaDirModule module = new VbaDirModule();
            module.Name = unicodeName.Length > 0 ? unicodeName : name;
            module.StreamName =
                unicodeStreamName.Length > 0 ?
                unicodeStreamName :
                streamName;
            module.SourceOffset = sourceOffset;
            module.SourceOffsetPosition = sourceOffsetPosition;
            module.TypeId = typeId;
            return module;
        }

        private static Dictionary<string, VbaModuleKind> ParseProjectTypes(
            string projectText)
        {
            Dictionary<string, VbaModuleKind> result =
                new Dictionary<string, VbaModuleKind>(
                    StringComparer.OrdinalIgnoreCase);
            string[] lines = projectText.Split(
                new string[] { "\r\n", "\n", "\r" },
                StringSplitOptions.None);
            int index;
            for (index = 0; index < lines.Length; index++)
            {
                string line = lines[index];
                if (line.StartsWith("Document=", StringComparison.Ordinal))
                {
                    string value = line.Substring("Document=".Length);
                    int slash = value.IndexOf('/');
                    if (slash >= 0)
                    {
                        value = value.Substring(0, slash);
                    }
                    AddProjectType(result, value, VbaModuleKind.Document);
                }
                else if (line.StartsWith(
                    "BaseClass=",
                    StringComparison.Ordinal))
                {
                    AddProjectType(
                        result,
                        line.Substring("BaseClass=".Length),
                        VbaModuleKind.Form);
                }
                else if (line.StartsWith(
                    "Module=",
                    StringComparison.Ordinal))
                {
                    AddProjectType(
                        result,
                        line.Substring("Module=".Length),
                        VbaModuleKind.Standard);
                }
                else if (line.StartsWith(
                    "Class=",
                    StringComparison.Ordinal))
                {
                    AddProjectType(
                        result,
                        line.Substring("Class=".Length),
                        VbaModuleKind.Class);
                }
            }

            return result;
        }

        private static void AddProjectType(
            Dictionary<string, VbaModuleKind> types,
            string name,
            VbaModuleKind kind)
        {
            if (name.Length == 0)
            {
                throw new InvalidDataException(
                    "PROJECT contains an empty module name.");
            }
            if (types.ContainsKey(name))
            {
                throw new InvalidDataException(
                    "PROJECT contains a duplicate module: " + name);
            }

            types.Add(name, kind);
        }

        private static void ValidateModuleType(
            VbaDirModule record,
            VbaModuleKind kind)
        {
            ushort expectedType =
                kind == VbaModuleKind.Standard ?
                (ushort)0x0021 :
                (ushort)0x0022;
            if (record.TypeId != expectedType)
            {
                throw new InvalidDataException(
                    "PROJECT and dir module types do not match: " +
                    record.Name);
            }
        }

        private static string GetExtension(VbaModuleKind kind)
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

        private static int FindAttributeHeaderLength(string text)
        {
            int position = 0;
            int headerEnd = 0;
            while (position < text.Length)
            {
                int contentEnd = position;
                while (contentEnd < text.Length &&
                    text[contentEnd] != '\r' &&
                    text[contentEnd] != '\n')
                {
                    contentEnd++;
                }

                string line = text.Substring(
                    position,
                    contentEnd - position);
                if (!IsAttributeLine(line))
                {
                    break;
                }

                int next = contentEnd;
                if (next < text.Length && text[next] == '\r')
                {
                    next++;
                    if (next < text.Length && text[next] == '\n')
                    {
                        next++;
                    }
                }
                else if (next < text.Length && text[next] == '\n')
                {
                    next++;
                }

                headerEnd = next;
                position = next;
            }

            return headerEnd;
        }

        private static bool IsAttributeLine(string line)
        {
            int position = 0;
            while (position < line.Length &&
                (line[position] == ' ' || line[position] == '\t'))
            {
                position++;
            }

            return line.IndexOf(
                "Attribute VB_",
                position,
                StringComparison.OrdinalIgnoreCase) == position;
        }

        private static Encoding GetStrictEncoding(int codePage)
        {
            try
            {
                return Encoding.GetEncoding(
                    codePage,
                    EncoderFallback.ExceptionFallback,
                    DecoderFallback.ExceptionFallback);
            }
            catch (ArgumentException)
            {
                throw new InvalidDataException(
                    "Unsupported VBA project code page: " + codePage);
            }
            catch (NotSupportedException)
            {
                throw new InvalidDataException(
                    "Unsupported VBA project code page: " + codePage);
            }
        }

        private static byte[] ReadSizedRecord(
            byte[] data,
            ref int position,
            ushort id,
            string label)
        {
            RequireId(data, ref position, id, label);
            return ReadLengthPrefixedBytes(data, ref position, label);
        }

        private static byte[] ReadLengthPrefixedBytes(
            byte[] data,
            ref int position,
            string label)
        {
            uint length = ReadUInt32AndAdvance(data, ref position);
            if (length > int.MaxValue)
            {
                throw new InvalidDataException(label + " is too large.");
            }

            int byteLength = (int)length;
            RequireRange(data, position, byteLength, label);
            byte[] result = new byte[byteLength];
            if (byteLength > 0)
            {
                Buffer.BlockCopy(
                    data,
                    position,
                    result,
                    0,
                    byteLength);
            }
            position += byteLength;
            return result;
        }

        private static void SkipFixedRecord(
            byte[] data,
            ref int position,
            ushort id,
            uint expectedSize,
            string label)
        {
            RequireId(data, ref position, id, label);
            uint size = ReadUInt32AndAdvance(data, ref position);
            if (size != expectedSize)
            {
                throw new InvalidDataException(
                    "Invalid " + label + " size.");
            }
            SkipBytes(data, ref position, (int)size, label);
        }

        private static void RequireId(
            byte[] data,
            ref int position,
            ushort expected,
            string label)
        {
            ushort actual = ReadUInt16AndAdvance(data, ref position);
            if (actual != expected)
            {
                throw new InvalidDataException(
                    "Expected " +
                    label +
                    " record 0x" +
                    expected.ToString("X4") +
                    ", found 0x" +
                    actual.ToString("X4") +
                    ".");
            }
        }

        private static ushort PeekId(byte[] data, int position)
        {
            RequireRange(data, position, 2, "dir record ID");
            return ReadUInt16(data, position);
        }

        private static ushort ReadUInt16AndAdvance(
            byte[] data,
            ref int position)
        {
            ushort value = ReadUInt16(data, position);
            position += 2;
            return value;
        }

        private static uint ReadUInt32AndAdvance(
            byte[] data,
            ref int position)
        {
            uint value = ReadUInt32(data, position);
            position += 4;
            return value;
        }

        private static ushort ReadUInt16(byte[] data, int position)
        {
            RequireRange(data, position, 2, "dir data");
            return BitConverter.ToUInt16(data, position);
        }

        private static uint ReadUInt32(byte[] data, int position)
        {
            RequireRange(data, position, 4, "dir data");
            return BitConverter.ToUInt32(data, position);
        }

        private static void SkipBytes(
            byte[] data,
            ref int position,
            int count,
            string label)
        {
            RequireRange(data, position, count, label);
            position += count;
        }

        private static void RequireRange(
            byte[] data,
            int position,
            int count,
            string label)
        {
            if (position < 0 ||
                count < 0 ||
                (long)position + (long)count > data.Length)
            {
                throw new InvalidDataException(
                    "Unexpected end of " + label + ".");
            }
        }
    }

    public sealed class VbaModuleAddition
    {
        public string Name;
        public string Code;

        public VbaModuleAddition(string name, string code)
        {
            Name = name;
            Code = code;
        }
    }

    public static class VbaProjectWriter
    {
        public static Dictionary<int, byte[]> CreateStreamChanges(
            VbaProjectData project,
            IDictionary<string, string> moduleChanges)
        {
            if (project == null)
            {
                throw new ArgumentNullException("project");
            }
            if (moduleChanges == null)
            {
                throw new ArgumentNullException("moduleChanges");
            }

            Dictionary<string, VbaModule> modules =
                new Dictionary<string, VbaModule>(
                    StringComparer.OrdinalIgnoreCase);
            int index;
            for (index = 0; index < project.Modules.Count; index++)
            {
                VbaModule module = project.Modules[index];
                if (modules.ContainsKey(module.Name))
                {
                    throw new InvalidDataException(
                        "Duplicate VBA module name: " + module.Name);
                }
                modules.Add(module.Name, module);
            }

            Dictionary<int, byte[]> result =
                new Dictionary<int, byte[]>();
            foreach (KeyValuePair<string, string> change in moduleChanges)
            {
                VbaModule module;
                if (!modules.TryGetValue(change.Key, out module))
                {
                    throw new InvalidDataException(
                        "VBA module was not found: " + change.Key);
                }
                if (change.Value == null)
                {
                    throw new ArgumentException(
                        "A VBA module change is null.",
                        "moduleChanges");
                }
                if (module.StreamEntry == null ||
                    module.StreamEntry.ObjectType != 2)
                {
                    throw new InvalidDataException(
                        "VBA module stream entry is invalid: " +
                        module.Name);
                }
                if (result.ContainsKey(module.StreamEntry.Id))
                {
                    throw new InvalidDataException(
                        "Multiple VBA modules refer to one stream.");
                }

                result.Add(
                    module.StreamEntry.Id,
                    CreateModuleStream(
                        project,
                        module,
                        change.Value));
            }

            return result;
        }

        private static Encoding GetWriteEncoding(VbaProjectData project)
        {
            // Reading tolerates bytes the declared code page cannot map;
            // writing must not. A character that cannot be encoded fails
            // the build instead of reaching the workbook as a
            // substitution.
            try
            {
                return Encoding.GetEncoding(
                    project.CodePage,
                    EncoderFallback.ExceptionFallback,
                    DecoderFallback.ExceptionFallback);
            }
            catch (ArgumentException)
            {
            }
            catch (NotSupportedException)
            {
            }

            return project.Encoding;
        }

        public static byte[] CreateModuleStream(
            VbaProjectData project,
            VbaModule module,
            string fullCode)
        {
            if (project == null)
            {
                throw new ArgumentNullException("project");
            }
            if (module == null)
            {
                throw new ArgumentNullException("module");
            }
            if (fullCode == null)
            {
                throw new ArgumentNullException("fullCode");
            }
            if (project.Encoding == null)
            {
                throw new InvalidDataException(
                    "The VBA project encoding is missing.");
            }
            if (module.StreamData == null)
            {
                throw new InvalidDataException(
                    "The VBA module stream data is missing.");
            }
            if (module.SourceOffset > int.MaxValue ||
                module.SourceOffset >= module.StreamData.Length)
            {
                throw new InvalidDataException(
                    "MODULEOFFSET is outside the module stream: " +
                    module.Name);
            }

            // Everything before MODULEOFFSET is the compiled p-code of the
            // code we are replacing, and it holds that old code's string
            // literals verbatim. Excel runs the p-code whenever it accepts
            // the cache, so carrying the prefix over meant the workbook
            // still showed and ran the old code while our own read-back of
            // the compressed source said the change had landed.
            //
            // We cannot rebuild p-code - there is no VBA compiler here - so
            // the module stream becomes source only, and RebuildProject
            // clears the rest of the compiled state so Excel compiles from
            // that source. Dropping the prefix alone is not enough and not
            // safe on its own; see DropCompiledCaches.
            byte[] sourceBytes =
                GetWriteEncoding(project).GetBytes(fullCode);
            return VbaCompression.Compress(sourceBytes);
        }

        // Excel keeps a VBA project's compiled state in three places, and it
        // believes that state whenever _VBA_PROJECT's version matches its own
        // engine. Measured on Excel 16 against a workbook this product had
        // rebuilt:
        //   * leaving all three alone  -> the old code is shown and run
        //   * version mismatch only    -> Excel terminates (0x800706BE)
        //   * MODULEOFFSET zeroed only -> "the data is in an invalid format"
        //   * __SRP_* emptied only     -> the old code is still shown
        // Only clearing all three together gives Excel one consistent answer,
        // "nothing here is compiled", which makes it compile from source. So
        // this is deliberately not a cache-deleting shotgun: each of the three
        // is required, and any two without the third produce a broken book.
        private const ushort UncompiledProjectVersion = 0xFFFF;

        private static byte[] DropCompiledCaches(
            VbaProjectData project,
            byte[] dirBytes,
            IDictionary<int, byte[]> streamChanges)
        {
            if (project.Ole2 == null ||
                project.VbaStorage == null ||
                dirBytes == null)
            {
                throw new InvalidDataException(
                    "The VBA project compiled state cannot be located.");
            }

            // Which modules dir records a MODULEOFFSET for. A module whose
            // stream we write has to be in here: the stream and dir have to
            // say the same thing about where the source starts, and a
            // module dir does not describe is one we cannot keep in step.
            Dictionary<string, VbaDirModule> dirByName =
                new Dictionary<string, VbaDirModule>(
                    StringComparer.OrdinalIgnoreCase);
            int index;
            for (index = 0; index < project.DirModules.Count; index++)
            {
                VbaDirModule record = project.DirModules[index];
                if (record.SourceOffsetPosition < 0 ||
                    record.SourceOffsetPosition + 4 > dirBytes.Length ||
                    dirByName.ContainsKey(record.Name))
                {
                    continue;
                }
                dirByName.Add(record.Name, record);
            }

            // 1. Modules we did not rewrite still carry their own p-code.
            // Their source is unchanged, but a project where some modules
            // have a cache and others do not is the mixed state Excel
            // rejects outright, so every module loses its prefix.
            for (index = 0; index < project.Modules.Count; index++)
            {
                VbaModule module = project.Modules[index];
                if (module.StreamEntry == null ||
                    module.StreamData == null)
                {
                    continue;
                }

                bool rewritten =
                    streamChanges.ContainsKey(module.StreamEntry.Id);
                bool hasCache =
                    module.SourceOffset != 0 &&
                    module.SourceOffset <= int.MaxValue &&
                    module.SourceOffset < module.StreamData.Length;
                if (!rewritten && !hasCache)
                {
                    continue;
                }
                if (!dirByName.ContainsKey(module.Name))
                {
                    // Rather than ship a workbook whose dir and module
                    // stream disagree - which is the one shape Excel
                    // refuses to open - stop and say which module.
                    throw new InvalidDataException(
                        "The VBA project does not record where this " +
                        "module's source starts, so its compiled code " +
                        "cannot be dropped safely: " + module.Name);
                }
                if (rewritten || !hasCache)
                {
                    continue;
                }

                int prefixLength = (int)module.SourceOffset;
                int sourceLength =
                    module.StreamData.Length - prefixLength;
                byte[] sourceOnly = new byte[sourceLength];
                Buffer.BlockCopy(
                    module.StreamData,
                    prefixLength,
                    sourceOnly,
                    0,
                    sourceLength);
                streamChanges.Add(
                    module.StreamEntry.Id,
                    sourceOnly);
            }

            // 2. dir must agree that the source now starts at byte zero.
            byte[] result = new byte[dirBytes.Length];
            Buffer.BlockCopy(dirBytes, 0, result, 0, dirBytes.Length);
            foreach (KeyValuePair<string, VbaDirModule> pair in dirByName)
            {
                WriteUInt32(
                    result,
                    pair.Value.SourceOffsetPosition,
                    0);
            }

            // 3. The per-project caches: __SRP_* streams, and the version
            // stamp in _VBA_PROJECT that tells Excel the cache is its own.
            for (index = 0;
                index < project.VbaStorage.Children.Count;
                index++)
            {
                Ole2DirectoryEntry child = project.Ole2.Entries[
                    project.VbaStorage.Children[index]];
                if (child.ObjectType != 2 ||
                    streamChanges.ContainsKey(child.Id))
                {
                    continue;
                }

                if (child.Name.StartsWith(
                    "__SRP_",
                    StringComparison.OrdinalIgnoreCase))
                {
                    streamChanges.Add(child.Id, new byte[0]);
                    continue;
                }

                if (string.Equals(
                    child.Name,
                    "_VBA_PROJECT",
                    StringComparison.OrdinalIgnoreCase))
                {
                    byte[] stamped = project.Ole2.ReadStream(child);
                    if (stamped.Length < 4)
                    {
                        throw new InvalidDataException(
                            "The _VBA_PROJECT stream is too short.");
                    }
                    byte[] copy = new byte[stamped.Length];
                    Buffer.BlockCopy(
                        stamped,
                        0,
                        copy,
                        0,
                        stamped.Length);
                    WriteUInt16(
                        copy,
                        2,
                        UncompiledProjectVersion);
                    streamChanges.Add(child.Id, copy);
                }
            }

            return result;
        }

        private static void WriteUInt32(
            byte[] output,
            int offset,
            uint value)
        {
            output[offset] = (byte)(value & 0xFF);
            output[offset + 1] = (byte)((value >> 8) & 0xFF);
            output[offset + 2] = (byte)((value >> 16) & 0xFF);
            output[offset + 3] = (byte)((value >> 24) & 0xFF);
        }

        public static byte[] RebuildProject(
            VbaProjectData project,
            IDictionary<string, string> moduleChanges)
        {
            return RebuildProject(
                project,
                moduleChanges,
                new List<VbaModuleAddition>());
        }

        public static byte[] RebuildProject(
            VbaProjectData project,
            IDictionary<string, string> moduleChanges,
            IList<VbaModuleAddition> newModules)
        {
            if (project == null)
            {
                throw new ArgumentNullException("project");
            }
            if (moduleChanges == null)
            {
                throw new ArgumentNullException("moduleChanges");
            }
            if (newModules == null)
            {
                throw new ArgumentNullException("newModules");
            }

            Dictionary<int, byte[]> streamChanges =
                CreateStreamChanges(project, moduleChanges);
            if (newModules.Count == 0)
            {
                // A build that changes no code must leave the workbook
                // exactly as it was, compiled state included.
                if (moduleChanges.Count == 0)
                {
                    return Ole2Writer.Rebuild(
                        project.Ole2,
                        streamChanges);
                }
                if (project.Ole2 == null ||
                    project.DirEntry == null)
                {
                    throw new InvalidDataException(
                        "The VBA project dir stream is missing.");
                }

                byte[] changedDir = DropCompiledCaches(
                    project,
                    project.DirDecompressed,
                    streamChanges);
                streamChanges.Add(
                    project.DirEntry.Id,
                    VbaCompression.Compress(changedDir));
                return Ole2Writer.Rebuild(
                    project.Ole2,
                    streamChanges);
            }
            if (project.Ole2 == null ||
                project.Encoding == null ||
                project.VbaStorage == null ||
                project.ProjectEntry == null ||
                project.ProjectWmEntry == null ||
                project.DirEntry == null)
            {
                throw new InvalidDataException(
                    "The VBA project addition streams are missing.");
            }

            HashSet<string> names = new HashSet<string>(
                StringComparer.OrdinalIgnoreCase);
            int index;
            for (index = 0; index < project.Modules.Count; index++)
            {
                names.Add(project.Modules[index].Name);
            }

            // Before AddDirModule appends anything, while the recorded
            // MODULEOFFSET positions still match these bytes. A new module is
            // written as source with no p-code, so without this the project
            // would be exactly the mixed state Excel refuses to open.
            byte[] dirBytes = DropCompiledCaches(
                project,
                project.DirDecompressed,
                streamChanges);
            string projectText = project.ProjectText;
            byte[] projectWmBytes = project.ProjectWmBytes;
            List<Ole2StreamAddition> streamAdditions =
                new List<Ole2StreamAddition>();
            for (index = 0; index < newModules.Count; index++)
            {
                VbaModuleAddition addition = newModules[index];
                if (addition == null)
                {
                    throw new ArgumentException(
                        "A VBA module addition is null.",
                        "newModules");
                }
                ValidateNewModuleName(addition.Name);
                if (addition.Code == null)
                {
                    throw new ArgumentException(
                        "A VBA module addition code is null.",
                        "newModules");
                }
                if (!names.Add(addition.Name) ||
                    project.Ole2.FindChild(
                        project.VbaStorage,
                        addition.Name,
                        0) != null)
                {
                    throw new InvalidDataException(
                        "Duplicate VBA module name: " +
                        addition.Name);
                }

                streamAdditions.Add(
                    new Ole2StreamAddition(
                        project.VbaStorage.Id,
                        addition.Name,
                        CreateNewModuleStream(
                            project,
                            addition.Name,
                            addition.Code)));
                dirBytes = AddDirModule(
                    project,
                    dirBytes,
                    addition.Name,
                    project.Modules.Count + index);
                projectText = AddProjectModuleLine(
                    projectText,
                    addition.Name);
                projectWmBytes = AddProjectWmName(
                    project,
                    projectWmBytes,
                    addition.Name);
            }

            streamChanges.Add(
                project.DirEntry.Id,
                VbaCompression.Compress(dirBytes));
            streamChanges.Add(
                project.ProjectEntry.Id,
                GetWriteEncoding(project).GetBytes(projectText));
            streamChanges.Add(
                project.ProjectWmEntry.Id,
                projectWmBytes);
            return Ole2Writer.Rebuild(
                project.Ole2,
                streamChanges,
                streamAdditions);
        }

        public static void ValidateNewModuleName(string name)
        {
            if (string.IsNullOrEmpty(name) ||
                name.Length > 31 ||
                !char.IsLetter(name[0]))
            {
                throw new InvalidDataException(
                    "The VBA module name is not a valid identifier.");
            }

            int index;
            for (index = 1; index < name.Length; index++)
            {
                if (!char.IsLetterOrDigit(name[index]) &&
                    name[index] != '_')
                {
                    throw new InvalidDataException(
                        "The VBA module name is not a valid identifier.");
                }
            }
        }

        public static string CreateNewModuleFullCode(
            string name,
            string code)
        {
            ValidateNewModuleName(name);
            if (code == null)
            {
                throw new ArgumentNullException("code");
            }
            return "Attribute VB_Name = \"" +
                name +
                "\"\r\n" +
                code;
        }

        public static byte[] CreateNewModuleStream(
            VbaProjectData project,
            string name,
            string code)
        {
            if (project == null)
            {
                throw new ArgumentNullException("project");
            }
            if (project.Encoding == null)
            {
                throw new InvalidDataException(
                    "The VBA project encoding is missing.");
            }

            string fullCode = CreateNewModuleFullCode(name, code);
            byte[] sourceBytes =
                GetWriteEncoding(project).GetBytes(fullCode);
            return VbaCompression.Compress(sourceBytes);
        }

        private static byte[] AddDirModule(
            VbaProjectData project,
            byte[] source,
            string name,
            int expectedCount)
        {
            if (source == null ||
                project.ProjectModulesOffset < 0 ||
                project.ProjectModulesOffset + 8 > source.Length)
            {
                throw new InvalidDataException(
                    "The PROJECTMODULES record is missing.");
            }

            int countOffset =
                project.ProjectModulesOffset + 6;
            ushort count = BitConverter.ToUInt16(
                source,
                countOffset);
            if (count != expectedCount)
            {
                throw new InvalidDataException(
                    "The PROJECTMODULES count is inconsistent.");
            }
            if (count == ushort.MaxValue)
            {
                throw new InvalidDataException(
                    "The VBA project has too many modules.");
            }

            int terminatorOffset = source.Length - 6;
            if (terminatorOffset < 0 ||
                BitConverter.ToUInt16(
                    source,
                    terminatorOffset) != 0x0010)
            {
                throw new InvalidDataException(
                    "The PROJECTTERMINATOR record is missing.");
            }

            byte[] record = CreateDirModuleRecord(
                GetWriteEncoding(project),
                name);
            byte[] result = new byte[
                source.Length + record.Length];
            Buffer.BlockCopy(
                source,
                0,
                result,
                0,
                terminatorOffset);
            Buffer.BlockCopy(
                record,
                0,
                result,
                terminatorOffset,
                record.Length);
            Buffer.BlockCopy(
                source,
                terminatorOffset,
                result,
                terminatorOffset + record.Length,
                6);
            WriteUInt16(
                result,
                countOffset,
                (ushort)(count + 1));
            return result;
        }

        private static byte[] CreateDirModuleRecord(
            Encoding encoding,
            string name)
        {
            byte[] ansiName = encoding.GetBytes(name);
            byte[] unicodeName = Encoding.Unicode.GetBytes(name);
            using (MemoryStream output = new MemoryStream())
            {
                WriteSizedRecord(
                    output,
                    0x0019,
                    ansiName);
                WriteSizedRecord(
                    output,
                    0x0047,
                    unicodeName);
                WriteSizedRecord(
                    output,
                    0x001A,
                    ansiName);
                WriteSizedRecord(
                    output,
                    0x0032,
                    unicodeName);
                WriteSizedRecord(
                    output,
                    0x001C,
                    new byte[0]);
                WriteSizedRecord(
                    output,
                    0x0048,
                    new byte[0]);
                WriteUInt16(output, 0x0031);
                WriteUInt32(output, 4);
                WriteUInt32(output, 0);
                WriteUInt16(output, 0x001E);
                WriteUInt32(output, 4);
                WriteUInt32(output, 0);
                WriteUInt16(output, 0x002C);
                WriteUInt32(output, 2);
                WriteUInt16(output, 0xFFFF);
                WriteUInt16(output, 0x0021);
                WriteUInt32(output, 0);
                WriteUInt16(output, 0x002B);
                WriteUInt32(output, 0);
                return output.ToArray();
            }
        }

        private static string AddProjectModuleLine(
            string source,
            string name)
        {
            string newLine = FindProjectNewLine(source);
            int position = 0;
            int lastModuleEnd = -1;
            int lastDeclarationEnd = -1;
            while (position < source.Length)
            {
                int contentEnd = position;
                while (contentEnd < source.Length &&
                    source[contentEnd] != '\r' &&
                    source[contentEnd] != '\n')
                {
                    contentEnd++;
                }

                int next = contentEnd;
                if (next < source.Length && source[next] == '\r')
                {
                    next++;
                    if (next < source.Length &&
                        source[next] == '\n')
                    {
                        next++;
                    }
                }
                else if (next < source.Length &&
                    source[next] == '\n')
                {
                    next++;
                }

                string line = source.Substring(
                    position,
                    contentEnd - position);
                if (line.StartsWith(
                    "Module=",
                    StringComparison.Ordinal))
                {
                    lastModuleEnd = next;
                }
                if (IsProjectModuleDeclaration(line))
                {
                    lastDeclarationEnd = next;
                }
                position = next;
            }

            int insertion = lastModuleEnd >= 0 ?
                lastModuleEnd :
                lastDeclarationEnd;
            if (insertion < 0)
            {
                throw new InvalidDataException(
                    "PROJECT contains no module declarations.");
            }

            string lineToAdd =
                "Module=" + name + newLine;
            if (insertion > 0 &&
                source[insertion - 1] != '\r' &&
                source[insertion - 1] != '\n')
            {
                lineToAdd = newLine + lineToAdd;
            }
            return source.Insert(insertion, lineToAdd);
        }

        private static bool IsProjectModuleDeclaration(
            string line)
        {
            return line.StartsWith(
                "Document=",
                StringComparison.Ordinal) ||
                line.StartsWith(
                    "BaseClass=",
                    StringComparison.Ordinal) ||
                line.StartsWith(
                    "Module=",
                    StringComparison.Ordinal) ||
                line.StartsWith(
                    "Class=",
                    StringComparison.Ordinal);
        }

        private static string FindProjectNewLine(string source)
        {
            int index;
            for (index = 0; index < source.Length; index++)
            {
                if (source[index] == '\r')
                {
                    return index + 1 < source.Length &&
                        source[index + 1] == '\n' ?
                        "\r\n" :
                        "\r";
                }
                if (source[index] == '\n')
                {
                    return "\n";
                }
            }
            return "\r\n";
        }

        private static byte[] AddProjectWmName(
            VbaProjectData project,
            byte[] source,
            string name)
        {
            if (source == null ||
                source.Length < 2 ||
                source[source.Length - 2] != 0 ||
                source[source.Length - 1] != 0)
            {
                throw new InvalidDataException(
                    "PROJECTwm terminator is missing.");
            }

            byte[] ansiName =
                GetWriteEncoding(project).GetBytes(name);
            byte[] unicodeName =
                Encoding.Unicode.GetBytes(name);
            int mapLength = ansiName.Length +
                1 +
                unicodeName.Length +
                2;
            byte[] result = new byte[
                source.Length + mapLength];
            int insertion = source.Length - 2;
            Buffer.BlockCopy(
                source,
                0,
                result,
                0,
                insertion);
            Buffer.BlockCopy(
                ansiName,
                0,
                result,
                insertion,
                ansiName.Length);
            int position = insertion + ansiName.Length + 1;
            Buffer.BlockCopy(
                unicodeName,
                0,
                result,
                position,
                unicodeName.Length);
            Buffer.BlockCopy(
                source,
                insertion,
                result,
                insertion + mapLength,
                2);
            return result;
        }

        private static void WriteSizedRecord(
            Stream output,
            ushort id,
            byte[] data)
        {
            WriteUInt16(output, id);
            WriteUInt32(output, (uint)data.Length);
            output.Write(data, 0, data.Length);
        }

        private static void WriteUInt16(
            Stream output,
            ushort value)
        {
            output.WriteByte((byte)(value & 0xFF));
            output.WriteByte((byte)((value >> 8) & 0xFF));
        }

        private static void WriteUInt32(
            Stream output,
            uint value)
        {
            output.WriteByte((byte)(value & 0xFF));
            output.WriteByte((byte)((value >> 8) & 0xFF));
            output.WriteByte((byte)((value >> 16) & 0xFF));
            output.WriteByte((byte)((value >> 24) & 0xFF));
        }

        private static void WriteUInt16(
            byte[] output,
            int offset,
            ushort value)
        {
            output[offset] = (byte)(value & 0xFF);
            output[offset + 1] =
                (byte)((value >> 8) & 0xFF);
        }
    }
}
