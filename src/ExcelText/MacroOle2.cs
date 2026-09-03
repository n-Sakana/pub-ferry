using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace MacroStudio
{
    public sealed class Ole2DirectoryEntry
    {
        public int Id;
        public string Name;
        public byte ObjectType;
        public byte ColorFlag;
        public uint LeftSiblingId;
        public uint RightSiblingId;
        public uint ChildId;
        public Guid ClassId;
        public uint StateBits;
        public long CreationTimeRaw;
        public long ModifiedTimeRaw;
        public DateTime? CreationTimeUtc;
        public DateTime? ModifiedTimeUtc;
        public int StartSector;
        public ulong Size;
        public int ParentId;
        public long DirectoryOffset;
        public List<int> Children;

        public Ole2DirectoryEntry()
        {
            Name = string.Empty;
            ParentId = -1;
            Children = new List<int>();
        }
    }

    public sealed class Ole2StreamAddition
    {
        public int ParentEntryId;
        public string Name;
        public byte[] Data;

        public Ole2StreamAddition(
            int parentEntryId,
            string name,
            byte[] data)
        {
            ParentEntryId = parentEntryId;
            Name = name;
            Data = data;
        }
    }

    public sealed class Ole2File
    {
        public const int FreeSector = -1;
        public const int EndOfChain = -2;
        public const int FatSector = -3;
        public const int DifatSector = -4;
        public const uint NoStream = 0xFFFFFFFFU;

        private readonly byte[] bytes;
        private int[] fat;
        private int[] miniFat;
        private byte[] miniStreamData;
        private int sectorCount;

        public ushort MajorVersion;
        public int SectorSize;
        public int MiniSectorSize;
        public uint NumberOfDirectorySectors;
        public uint NumberOfFatSectors;
        public int FirstDirectorySector;
        public uint MiniStreamCutoff;
        public int FirstMiniFatSector;
        public uint NumberOfMiniFatSectors;
        public int FirstDifatSector;
        public uint NumberOfDifatSectors;
        public List<int> FatSectorIds;
        public List<int> DifatSectorIds;
        public List<Ole2DirectoryEntry> Entries;
        public Ole2DirectoryEntry RootEntry;
        public bool HasReadWarnings;
        // HasReadWarnings covers everything that did not hold, from a
        // header field that merely breaks the spec to a chain that ends
        // early. Most of it never changes the bytes handed out, so the
        // one case that does is recorded separately: a stream that came
        // back shorter than its directory entry says it is. That is
        // content loss, and the only OLE2 finding a user has to act on.
        public bool HasShortStreamRead;

        private Ole2File(byte[] source)
        {
            bytes = source;
            fat = new int[0];
            miniFat = new int[0];
            miniStreamData = new byte[0];
            FatSectorIds = new List<int>();
            DifatSectorIds = new List<int>();
            Entries = new List<Ole2DirectoryEntry>();
            HasReadWarnings = false;
            HasShortStreamRead = false;
        }

        public byte[] Bytes
        {
            get { return bytes; }
        }

        public int[] Fat
        {
            get { return fat; }
        }

        public int[] MiniFat
        {
            get { return miniFat; }
        }

        public byte[] MiniStreamData
        {
            get { return miniStreamData; }
        }

        public static Ole2File Parse(byte[] source)
        {
            if (source == null)
            {
                throw new ArgumentNullException("source");
            }

            Ole2File file = new Ole2File(source);
            file.ParseHeader();
            file.ParseDifat();
            file.ParseFat();
            file.ParseDirectory();
            file.ParseMiniFat();
            file.BuildMiniStream();
            return file;
        }

        public Ole2DirectoryEntry FindChild(
            Ole2DirectoryEntry parent,
            string name,
            byte objectType)
        {
            if (parent == null)
            {
                throw new ArgumentNullException("parent");
            }

            Ole2DirectoryEntry found = null;
            int index;
            for (index = 0; index < parent.Children.Count; index++)
            {
                Ole2DirectoryEntry entry = Entries[parent.Children[index]];
                if ((objectType == 0 || entry.ObjectType == objectType) &&
                    string.Equals(entry.Name, name, StringComparison.OrdinalIgnoreCase))
                {
                    if (found != null)
                    {
                        HasReadWarnings = true;
                        continue;
                    }

                    found = entry;
                }
            }

            return found;
        }

        public byte[] ReadStream(Ole2DirectoryEntry entry)
        {
            if (entry == null)
            {
                throw new ArgumentNullException("entry");
            }
            if (entry.ObjectType != 2)
            {
                throw new InvalidDataException(
                    "OLE2 entry is not a stream: " + entry.Name);
            }
            if (entry.Size == 0)
            {
                return new byte[0];
            }
            if (entry.Size < MiniStreamCutoff)
            {
                // Some producers store small streams in the regular FAT
                // chain while the root mini stream is missing entirely.
                // Only in that case fall back to the regular chain; a
                // partially readable mini stream keeps its honest
                // partial result instead of unrelated sector data.
                if (miniStreamData.Length == 0)
                {
                    byte[] regular = ReadRegularStream(
                        entry.StartSector,
                        entry.Size,
                        "stream " + entry.Name);
                    if (regular.Length > 0)
                    {
                        HasReadWarnings = true;
                        return regular;
                    }
                }
                return ReadMiniStream(entry);
            }

            return ReadRegularStream(
                entry.StartSector,
                entry.Size,
                "stream " + entry.Name);
        }

        private void ParseHeader()
        {
            byte[] signature = new byte[]
            {
                0xD0, 0xCF, 0x11, 0xE0, 0xA1, 0xB1, 0x1A, 0xE1
            };

            RequireRange(0, 512, "OLE2 header");
            int index;
            for (index = 0; index < signature.Length; index++)
            {
                if (bytes[index] != signature[index])
                {
                    HasReadWarnings = true;
                    break;
                }
            }

            MajorVersion = ReadUInt16(26);
            ushort byteOrder = ReadUInt16(28);
            ushort sectorShift = ReadUInt16(30);
            ushort miniSectorShift = ReadUInt16(32);

            if (byteOrder != 0xFFFE)
            {
                HasReadWarnings = true;
            }

            if (sectorShift == 12)
            {
                if (MajorVersion != 4)
                {
                    HasReadWarnings = true;
                }
                MajorVersion = 4;
            }
            else if (sectorShift == 9)
            {
                if (MajorVersion != 3)
                {
                    HasReadWarnings = true;
                }
                MajorVersion = 3;
            }
            else
            {
                HasReadWarnings = true;
                sectorShift = MajorVersion == 4 ? (ushort)12 : (ushort)9;
                MajorVersion = sectorShift == 12 ? (ushort)4 : (ushort)3;
            }

            SectorSize = 1 << sectorShift;
            if (bytes.Length < SectorSize)
            {
                HasReadWarnings = true;
                SectorSize = 512;
                MajorVersion = 3;
            }
            if (miniSectorShift != 6)
            {
                HasReadWarnings = true;
                miniSectorShift = 6;
            }
            MiniSectorSize = 1 << miniSectorShift;
            if (bytes.Length % SectorSize != 0)
            {
                HasReadWarnings = true;
            }

            sectorCount = bytes.Length / SectorSize - 1;
            NumberOfDirectorySectors = ReadUInt32(40);
            NumberOfFatSectors = ReadUInt32(44);
            FirstDirectorySector = ReadInt32(48);
            MiniStreamCutoff = ReadUInt32(56);
            FirstMiniFatSector = ReadInt32(60);
            NumberOfMiniFatSectors = ReadUInt32(64);
            FirstDifatSector = ReadInt32(68);
            NumberOfDifatSectors = ReadUInt32(72);

            if (MajorVersion == 3 && NumberOfDirectorySectors != 0)
            {
                HasReadWarnings = true;
            }
            if (MiniStreamCutoff != 0x1000)
            {
                HasReadWarnings = true;
                if (MiniStreamCutoff == 0)
                {
                    MiniStreamCutoff = 0x1000;
                }
            }
        }

        private void ParseDifat()
        {
            int fatCount = ToIntCount(NumberOfFatSectors, "FAT sector count");
            int difatCount = ToIntCount(NumberOfDifatSectors, "DIFAT sector count");
            HashSet<int> fatIds = new HashSet<int>();
            HashSet<int> difatIds = new HashSet<int>();
            int index;

            for (index = 0; index < 109; index++)
            {
                int sectorId = ReadInt32(76 + index * 4);
                if (sectorId >= 0)
                {
                    AddFatSector(sectorId, fatCount, fatIds);
                }
                else if (sectorId != FreeSector)
                {
                    HasReadWarnings = true;
                }
            }

            int current = FirstDifatSector;
            int entriesPerDifatSector = SectorSize / 4 - 1;
            for (index = 0; index < difatCount; index++)
            {
                if (!IsValidSectorId(current))
                {
                    HasReadWarnings = true;
                    break;
                }
                if (!difatIds.Add(current))
                {
                    HasReadWarnings = true;
                    break;
                }

                DifatSectorIds.Add(current);
                int offset = GetSectorOffset(current);
                int item;
                for (item = 0; item < entriesPerDifatSector; item++)
                {
                    int fatSectorId = ReadInt32(offset + item * 4);
                    if (fatSectorId >= 0)
                    {
                        AddFatSector(fatSectorId, fatCount, fatIds);
                    }
                    else if (fatSectorId != FreeSector)
                    {
                        HasReadWarnings = true;
                    }
                }

                current = ReadInt32(
                    offset + entriesPerDifatSector * 4);
            }

            if (difatCount == 0)
            {
                if (FirstDifatSector != EndOfChain &&
                    FirstDifatSector != FreeSector)
                {
                    HasReadWarnings = true;
                }
            }
            else if (current != EndOfChain)
            {
                HasReadWarnings = true;
            }

            if (FatSectorIds.Count != fatCount)
            {
                HasReadWarnings = true;
            }
        }

        private void AddFatSector(
            int sectorId,
            int expectedCount,
            HashSet<int> seen)
        {
            if (!IsValidSectorId(sectorId))
            {
                HasReadWarnings = true;
                return;
            }
            if (!seen.Add(sectorId))
            {
                HasReadWarnings = true;
                return;
            }

            FatSectorIds.Add(sectorId);
            if (FatSectorIds.Count > expectedCount)
            {
                HasReadWarnings = true;
            }
        }

        private void ParseFat()
        {
            int entriesPerSector = SectorSize / 4;
            int totalEntries;
            try
            {
                totalEntries = checked(FatSectorIds.Count * entriesPerSector);
            }
            catch (OverflowException)
            {
                throw new InvalidDataException("OLE2 FAT is too large.");
            }

            fat = new int[totalEntries];
            int outputIndex = 0;
            int fatIndex;
            for (fatIndex = 0; fatIndex < FatSectorIds.Count; fatIndex++)
            {
                int offset = GetSectorOffset(FatSectorIds[fatIndex]);
                int item;
                for (item = 0; item < entriesPerSector; item++)
                {
                    fat[outputIndex] = ReadInt32(offset + item * 4);
                    outputIndex++;
                }
            }

            for (fatIndex = 0; fatIndex < FatSectorIds.Count; fatIndex++)
            {
                int sectorId = FatSectorIds[fatIndex];
                if (sectorId >= fat.Length || fat[sectorId] != FatSector)
                {
                    HasReadWarnings = true;
                }
            }

            int difatIndex;
            for (difatIndex = 0;
                difatIndex < DifatSectorIds.Count;
                difatIndex++)
            {
                int sectorId = DifatSectorIds[difatIndex];
                if (sectorId >= fat.Length || fat[sectorId] != DifatSector)
                {
                    HasReadWarnings = true;
                }
            }
        }

        private void ParseDirectory()
        {
            int expectedSectors = -1;
            if (MajorVersion == 4)
            {
                expectedSectors = ToIntCount(
                    NumberOfDirectorySectors,
                    "directory sector count");
            }

            byte[] directoryBytes = ReadSectorChainBytes(
                FirstDirectorySector,
                expectedSectors,
                "directory stream");

            if (directoryBytes.Length % 128 != 0)
            {
                HasReadWarnings = true;
            }

            int entryCount = directoryBytes.Length / 128;
            int entryIndex;
            for (entryIndex = 0; entryIndex < entryCount; entryIndex++)
            {
                int offset = entryIndex * 128;
                ushort nameLength = ReadUInt16(directoryBytes, offset + 64);
                byte objectType = directoryBytes[offset + 66];

                if (objectType != 0 &&
                    objectType != 1 &&
                    objectType != 2 &&
                    objectType != 5)
                {
                    HasReadWarnings = true;
                    objectType = 0;
                }
                if (nameLength > 64 || (nameLength & 1) != 0)
                {
                    HasReadWarnings = true;
                    nameLength = (ushort)Math.Min((int)nameLength, 64);
                    if ((nameLength & 1) != 0)
                    {
                        nameLength--;
                    }
                }

                string name = string.Empty;
                if (nameLength >= 2)
                {
                    name = Encoding.Unicode.GetString(
                        directoryBytes,
                        offset,
                        nameLength - 2);
                }
                else if (objectType != 0)
                {
                    HasReadWarnings = true;
                }

                byte[] classIdBytes = new byte[16];
                Buffer.BlockCopy(
                    directoryBytes,
                    offset + 80,
                    classIdBytes,
                    0,
                    classIdBytes.Length);

                ulong size = ReadUInt64(directoryBytes, offset + 120);
                if (MajorVersion == 3)
                {
                    size &= 0xFFFFFFFFUL;
                }

                Ole2DirectoryEntry entry = new Ole2DirectoryEntry();
                entry.Id = entryIndex;
                entry.Name = name;
                entry.ObjectType = objectType;
                entry.ColorFlag = directoryBytes[offset + 67];
                entry.LeftSiblingId = ReadUInt32(directoryBytes, offset + 68);
                entry.RightSiblingId = ReadUInt32(directoryBytes, offset + 72);
                entry.ChildId = ReadUInt32(directoryBytes, offset + 76);
                entry.ClassId = new Guid(classIdBytes);
                entry.StateBits = ReadUInt32(directoryBytes, offset + 96);
                entry.CreationTimeRaw = ReadInt64(
                    directoryBytes,
                    offset + 100);
                entry.ModifiedTimeRaw = ReadInt64(
                    directoryBytes,
                    offset + 108);
                entry.CreationTimeUtc = ConvertFileTime(
                    entry.CreationTimeRaw);
                entry.ModifiedTimeUtc = ConvertFileTime(
                    entry.ModifiedTimeRaw);
                entry.StartSector = ReadInt32(directoryBytes, offset + 116);
                entry.Size = size;
                entry.DirectoryOffset = offset;
                Entries.Add(entry);
            }

            RootEntry = null;
            for (entryIndex = 0; entryIndex < Entries.Count; entryIndex++)
            {
                if (Entries[entryIndex].ObjectType == 5)
                {
                    RootEntry = Entries[entryIndex];
                    break;
                }
            }
            if (RootEntry == null)
            {
                HasReadWarnings = true;
                RootEntry = new Ole2DirectoryEntry();
                RootEntry.Id = Entries.Count;
                RootEntry.Name = "Root Entry";
                RootEntry.ObjectType = 5;
                Entries.Add(RootEntry);
            }
            else if (RootEntry.Id != 0)
            {
                HasReadWarnings = true;
            }
            int storageIndex;
            for (storageIndex = 0;
                storageIndex < Entries.Count;
                storageIndex++)
            {
                Ole2DirectoryEntry storage = Entries[storageIndex];
                if (storage.ObjectType == 1 || storage.ObjectType == 5)
                {
                    AssignChildTree(storage);
                }
            }
        }

        private void AssignChildTree(Ole2DirectoryEntry storage)
        {
            if (storage.ChildId == NoStream)
            {
                return;
            }

            Stack<uint> pending = new Stack<uint>();
            HashSet<uint> visited = new HashSet<uint>();
            pending.Push(storage.ChildId);

            while (pending.Count > 0)
            {
                uint id = pending.Pop();
                if (id == NoStream)
                {
                    continue;
                }
                if (id >= Entries.Count)
                {
                    HasReadWarnings = true;
                    continue;
                }
                if (!visited.Add(id))
                {
                    HasReadWarnings = true;
                    continue;
                }

                Ole2DirectoryEntry entry = Entries[(int)id];
                if (entry.ObjectType == 0 || entry.ObjectType == 5)
                {
                    HasReadWarnings = true;
                    continue;
                }
                if (entry.ParentId != -1 && entry.ParentId != storage.Id)
                {
                    HasReadWarnings = true;
                    continue;
                }

                entry.ParentId = storage.Id;
                storage.Children.Add(entry.Id);
                if (entry.LeftSiblingId != NoStream)
                {
                    pending.Push(entry.LeftSiblingId);
                }
                if (entry.RightSiblingId != NoStream)
                {
                    pending.Push(entry.RightSiblingId);
                }
            }
        }

        private void ParseMiniFat()
        {
            int expectedSectors = ToIntCount(
                NumberOfMiniFatSectors,
                "mini FAT sector count");

            if (expectedSectors == 0)
            {
                if (FirstMiniFatSector != EndOfChain &&
                    FirstMiniFatSector != FreeSector)
                {
                    HasReadWarnings = true;
                    expectedSectors = -1;
                }
                else
                {
                    miniFat = new int[0];
                    return;
                }
            }

            byte[] miniFatBytes = ReadSectorChainBytes(
                FirstMiniFatSector,
                expectedSectors,
                "mini FAT");
            miniFat = new int[miniFatBytes.Length / 4];
            int index;
            for (index = 0; index < miniFat.Length; index++)
            {
                miniFat[index] = ReadInt32(miniFatBytes, index * 4);
            }
        }

        private void BuildMiniStream()
        {
            if (RootEntry == null || RootEntry.Size == 0)
            {
                miniStreamData = new byte[0];
                return;
            }

            miniStreamData = ReadRegularStream(
                RootEntry.StartSector,
                RootEntry.Size,
                "root mini stream");
        }

        private byte[] ReadMiniStream(Ole2DirectoryEntry entry)
        {
            int length;
            if (entry.Size > (ulong)miniStreamData.Length)
            {
                HasReadWarnings = true;
                HasShortStreamRead = true;
                length = miniStreamData.Length;
            }
            else
            {
                length = (int)entry.Size;
            }
            HashSet<int> visited = new HashSet<int>();
            int current = entry.StartSector;
            using (MemoryStream output = new MemoryStream(length))
            {
                while (output.Length < length &&
                    current != EndOfChain)
                {
                    if (current < 0 || current >= miniFat.Length)
                    {
                        HasReadWarnings = true;
                        break;
                    }
                    if (!visited.Add(current))
                    {
                        HasReadWarnings = true;
                        break;
                    }

                    long sourceOffset =
                        (long)current * (long)MiniSectorSize;
                    int copyLength = Math.Min(
                        MiniSectorSize,
                        length - (int)output.Length);
                    if (sourceOffset < 0 ||
                        sourceOffset >= miniStreamData.Length)
                    {
                        HasReadWarnings = true;
                        break;
                    }

                    int available = Math.Min(
                        copyLength,
                        miniStreamData.Length - (int)sourceOffset);
                    output.Write(
                        miniStreamData,
                        (int)sourceOffset,
                        available);
                    if (available < copyLength)
                    {
                        HasReadWarnings = true;
                        break;
                    }

                    current = miniFat[current];
                }

                if (output.Length < length)
                {
                    HasReadWarnings = true;
                    HasShortStreamRead = true;
                }
                return output.ToArray();
            }
        }

        private byte[] ReadRegularStream(
            int startSector,
            ulong size,
            string label)
        {
            if (size == 0)
            {
                return new byte[0];
            }

            ulong maximumLength = (ulong)bytes.Length;
            int length;
            if (size > maximumLength)
            {
                HasReadWarnings = true;
                HasShortStreamRead = true;
                length = bytes.Length;
            }
            else
            {
                length = (int)size;
            }

            int expectedSectors = (int)(
                ((long)length + (long)SectorSize - 1L) /
                (long)SectorSize);
            byte[] chainBytes = ReadSectorChainBytes(
                startSector,
                expectedSectors,
                label);
            int copyLength = Math.Min(length, chainBytes.Length);
            byte[] result = new byte[copyLength];
            if (copyLength > 0)
            {
                Buffer.BlockCopy(chainBytes, 0, result, 0, copyLength);
            }
            if (copyLength < length)
            {
                HasReadWarnings = true;
                HasShortStreamRead = true;
            }
            return result;
        }

        private byte[] ReadSectorChainBytes(
            int startSector,
            int expectedSectors,
            string label)
        {
            List<int> chain = ReadSectorChain(
                startSector,
                expectedSectors,
                label);
            int length;
            try
            {
                length = checked(chain.Count * SectorSize);
            }
            catch (OverflowException)
            {
                throw new InvalidDataException(
                    "OLE2 sector chain is too large: " + label);
            }

            byte[] result = new byte[length];
            int index;
            for (index = 0; index < chain.Count; index++)
            {
                Buffer.BlockCopy(
                    bytes,
                    GetSectorOffset(chain[index]),
                    result,
                    index * SectorSize,
                    SectorSize);
            }

            return result;
        }

        private List<int> ReadSectorChain(
            int startSector,
            int expectedSectors,
            string label)
        {
            List<int> chain = new List<int>();
            if (expectedSectors == 0)
            {
                if (startSector != EndOfChain &&
                    startSector != FreeSector)
                {
                    HasReadWarnings = true;
                }

                return chain;
            }
            if (startSector < 0)
            {
                HasReadWarnings = true;
                return chain;
            }

            HashSet<int> visited = new HashSet<int>();
            int current = startSector;
            while (current != EndOfChain &&
                (expectedSectors < 0 ||
                    chain.Count < expectedSectors))
            {
                if (!IsValidSectorId(current) ||
                    current >= fat.Length)
                {
                    HasReadWarnings = true;
                    break;
                }
                if (!visited.Add(current))
                {
                    HasReadWarnings = true;
                    break;
                }

                chain.Add(current);
                current = fat[current];
            }

            if (expectedSectors >= 0 &&
                chain.Count < expectedSectors)
            {
                HasReadWarnings = true;
            }

            return chain;
        }

        private int GetSectorOffset(int sectorId)
        {
            ValidateSectorId(sectorId, "sector");
            long offset = ((long)sectorId + 1L) * (long)SectorSize;
            if (offset > int.MaxValue)
            {
                throw new InvalidDataException(
                    "OLE2 sector offset is too large.");
            }

            return (int)offset;
        }

        private void ValidateSectorId(int sectorId, string label)
        {
            if (!IsValidSectorId(sectorId))
            {
                throw new InvalidDataException(
                    "Invalid OLE2 sector for " + label + ": " + sectorId);
            }
        }

        private bool IsValidSectorId(int sectorId)
        {
            return sectorId >= 0 && sectorId < sectorCount;
        }

        private void RequireRange(int offset, int count, string label)
        {
            if (offset < 0 ||
                count < 0 ||
                (long)offset + (long)count > bytes.Length)
            {
                throw new InvalidDataException(
                    "Unexpected end of data while reading " + label + ".");
            }
        }

        private ushort ReadUInt16(int offset)
        {
            RequireRange(offset, 2, "OLE2 data");
            return BitConverter.ToUInt16(bytes, offset);
        }

        private uint ReadUInt32(int offset)
        {
            RequireRange(offset, 4, "OLE2 data");
            return BitConverter.ToUInt32(bytes, offset);
        }

        private int ReadInt32(int offset)
        {
            RequireRange(offset, 4, "OLE2 data");
            return BitConverter.ToInt32(bytes, offset);
        }

        private static ushort ReadUInt16(byte[] data, int offset)
        {
            RequireRange(data, offset, 2, "directory data");
            return BitConverter.ToUInt16(data, offset);
        }

        private static uint ReadUInt32(byte[] data, int offset)
        {
            RequireRange(data, offset, 4, "directory data");
            return BitConverter.ToUInt32(data, offset);
        }

        private static int ReadInt32(byte[] data, int offset)
        {
            RequireRange(data, offset, 4, "directory data");
            return BitConverter.ToInt32(data, offset);
        }

        private static ulong ReadUInt64(byte[] data, int offset)
        {
            RequireRange(data, offset, 8, "directory data");
            return BitConverter.ToUInt64(data, offset);
        }

        private static long ReadInt64(byte[] data, int offset)
        {
            RequireRange(data, offset, 8, "directory data");
            return BitConverter.ToInt64(data, offset);
        }

        private static void RequireRange(
            byte[] data,
            int offset,
            int count,
            string label)
        {
            if (offset < 0 ||
                count < 0 ||
                (long)offset + (long)count > data.Length)
            {
                throw new InvalidDataException(
                    "Unexpected end of " + label + ".");
            }
        }

        private int ToIntCount(uint value, string label)
        {
            if (value > (uint)sectorCount)
            {
                HasReadWarnings = true;
                return sectorCount;
            }

            return (int)value;
        }

        private static DateTime? ConvertFileTime(long value)
        {
            if (value == 0)
            {
                return null;
            }

            try
            {
                return DateTime.FromFileTimeUtc(value);
            }
            catch (ArgumentOutOfRangeException)
            {
                return null;
            }
        }
    }

    public static class Ole2Writer
    {
        private const int SectorSize = 512;
        private const int MiniSectorSize = 64;
        private const int MiniStreamCutoff = 4096;
        private const int FatEntriesPerSector = 128;
        private const int DifatEntriesPerSector = 127;

        private sealed class StreamPlan
        {
            public Ole2DirectoryEntry Entry;
            public byte[] Data;
            public bool IsMini;
            public int SectorCount;
            public int StartSector;
            public int MiniSectorCount;
            public int MiniStartSector;
        }

        public static byte[] Rebuild(Ole2File source)
        {
            return Rebuild(source, null);
        }

        public static byte[] Rebuild(
            Ole2File source,
            IDictionary<int, byte[]> streamChanges)
        {
            return Rebuild(source, streamChanges, null);
        }

        public static byte[] Rebuild(
            Ole2File source,
            IDictionary<int, byte[]> streamChanges,
            IList<Ole2StreamAddition> streamAdditions)
        {
            if (source == null)
            {
                throw new ArgumentNullException("source");
            }
            if (source.MajorVersion == 4)
            {
                throw new InvalidDataException(
                    "OLE2 version 4 reconstruction is not supported.");
            }
            if (source.MajorVersion != 3)
            {
                throw new InvalidDataException(
                    "Only OLE2 version 3 can be reconstructed.");
            }
            if (source.Entries.Count == 0 ||
                source.RootEntry == null ||
                source.RootEntry.Id != 0 ||
                source.RootEntry.ObjectType != 5)
            {
                throw new InvalidDataException(
                    "The OLE2 root entry is invalid.");
            }

            List<Ole2DirectoryEntry> entries = CloneEntries(source);
            IDictionary<int, byte[]> effectiveChanges =
                AddStreamEntries(
                    source,
                    entries,
                    streamChanges,
                    streamAdditions);
            ValidateParents(entries);
            BuildDirectoryTrees(entries);

            HashSet<int> usedChanges = new HashSet<int>();
            List<StreamPlan> streams = BuildStreamPlans(
                source,
                entries,
                effectiveChanges,
                usedChanges);
            ValidateChanges(streamChanges, usedChanges);

            int miniSectorCount = AssignMiniSectors(streams);
            byte[] miniStream = BuildMiniStream(
                streams,
                miniSectorCount);
            int[] miniFat = BuildMiniFat(streams, miniSectorCount);
            int miniFatSectorCount = miniFat.Length /
                FatEntriesPerSector;

            int directoryByteCount = CheckedMultiply(
                entries.Count,
                128,
                "directory size");
            int directorySectorCount = DivideRoundUp(
                directoryByteCount,
                SectorSize);
            int miniStreamSectorCount = DivideRoundUp(
                miniStream.Length,
                SectorSize);

            int baseSectorCount = directorySectorCount;
            baseSectorCount = CheckedAdd(
                baseSectorCount,
                miniStreamSectorCount,
                "base sector count");
            baseSectorCount = CheckedAdd(
                baseSectorCount,
                miniFatSectorCount,
                "base sector count");

            int index;
            for (index = 0; index < streams.Count; index++)
            {
                StreamPlan stream = streams[index];
                if (!stream.IsMini)
                {
                    baseSectorCount = CheckedAdd(
                        baseSectorCount,
                        stream.SectorCount,
                        "base sector count");
                }
            }

            int fatSectorCount;
            int difatSectorCount;
            CalculateAllocationTableCounts(
                baseSectorCount,
                out fatSectorCount,
                out difatSectorCount);

            int nextSector = 0;
            for (index = 0; index < streams.Count; index++)
            {
                StreamPlan stream = streams[index];
                if (stream.Data.Length == 0)
                {
                    stream.StartSector = Ole2File.EndOfChain;
                    stream.Entry.StartSector = Ole2File.EndOfChain;
                }
                else if (stream.IsMini)
                {
                    stream.Entry.StartSector = stream.MiniStartSector;
                }
                else
                {
                    stream.StartSector = nextSector;
                    stream.Entry.StartSector = nextSector;
                    nextSector = CheckedAdd(
                        nextSector,
                        stream.SectorCount,
                        "sector allocation");
                }

                stream.Entry.Size = (ulong)stream.Data.Length;
            }

            int miniStreamStart = Ole2File.EndOfChain;
            if (miniStreamSectorCount > 0)
            {
                miniStreamStart = nextSector;
                nextSector = CheckedAdd(
                    nextSector,
                    miniStreamSectorCount,
                    "mini stream allocation");
            }

            int miniFatStart = Ole2File.EndOfChain;
            if (miniFatSectorCount > 0)
            {
                miniFatStart = nextSector;
                nextSector = CheckedAdd(
                    nextSector,
                    miniFatSectorCount,
                    "mini FAT allocation");
            }

            int directoryStart = nextSector;
            nextSector = CheckedAdd(
                nextSector,
                directorySectorCount,
                "directory allocation");

            List<int> fatSectorIds = new List<int>();
            for (index = 0; index < fatSectorCount; index++)
            {
                fatSectorIds.Add(nextSector);
                nextSector++;
            }

            List<int> difatSectorIds = new List<int>();
            for (index = 0; index < difatSectorCount; index++)
            {
                difatSectorIds.Add(nextSector);
                nextSector++;
            }

            if (nextSector !=
                baseSectorCount + fatSectorCount + difatSectorCount)
            {
                throw new InvalidDataException(
                    "OLE2 sector allocation count mismatch.");
            }

            int fatEntryCount = CheckedMultiply(
                fatSectorCount,
                FatEntriesPerSector,
                "FAT size");
            int[] fat = CreateFilledIntArray(
                fatEntryCount,
                Ole2File.FreeSector);

            for (index = 0; index < streams.Count; index++)
            {
                StreamPlan stream = streams[index];
                if (!stream.IsMini && stream.SectorCount > 0)
                {
                    MarkChain(
                        fat,
                        stream.StartSector,
                        stream.SectorCount);
                }
            }
            MarkChain(
                fat,
                miniStreamStart,
                miniStreamSectorCount);
            MarkChain(
                fat,
                miniFatStart,
                miniFatSectorCount);
            MarkChain(
                fat,
                directoryStart,
                directorySectorCount);

            for (index = 0; index < fatSectorIds.Count; index++)
            {
                fat[fatSectorIds[index]] = Ole2File.FatSector;
            }
            for (index = 0; index < difatSectorIds.Count; index++)
            {
                fat[difatSectorIds[index]] = Ole2File.DifatSector;
            }

            Ole2DirectoryEntry root = entries[0];
            root.StartSector = miniStreamStart;
            root.Size = (ulong)miniStream.Length;

            long outputLength =
                ((long)nextSector + 1L) * (long)SectorSize;
            if (outputLength > int.MaxValue)
            {
                throw new InvalidDataException(
                    "Reconstructed OLE2 file is too large.");
            }

            byte[] output = new byte[(int)outputLength];
            WriteHeader(
                output,
                fatSectorIds,
                directoryStart,
                miniFatStart,
                miniFatSectorCount,
                difatSectorIds);

            for (index = 0; index < streams.Count; index++)
            {
                StreamPlan stream = streams[index];
                if (!stream.IsMini && stream.SectorCount > 0)
                {
                    WriteSectorData(
                        output,
                        stream.StartSector,
                        stream.Data);
                }
            }

            if (miniStreamSectorCount > 0)
            {
                WriteSectorData(
                    output,
                    miniStreamStart,
                    miniStream);
            }
            if (miniFatSectorCount > 0)
            {
                WriteIntArraySectors(
                    output,
                    miniFatStart,
                    miniFat);
            }

            byte[] directoryBytes = BuildDirectoryBytes(
                entries,
                directorySectorCount * SectorSize);
            WriteSectorData(
                output,
                directoryStart,
                directoryBytes);
            WriteFatSectors(output, fatSectorIds, fat);
            WriteDifatSectors(
                output,
                fatSectorIds,
                difatSectorIds);

            return output;
        }

        private static List<Ole2DirectoryEntry> CloneEntries(
            Ole2File source)
        {
            List<Ole2DirectoryEntry> result =
                new List<Ole2DirectoryEntry>();
            int index;
            for (index = 0; index < source.Entries.Count; index++)
            {
                Ole2DirectoryEntry original = source.Entries[index];
                Ole2DirectoryEntry entry = new Ole2DirectoryEntry();
                entry.Id = original.Id;
                entry.Name = original.Name;
                entry.ObjectType = original.ObjectType;
                entry.ColorFlag = original.ColorFlag;
                entry.LeftSiblingId = original.LeftSiblingId;
                entry.RightSiblingId = original.RightSiblingId;
                entry.ChildId = original.ChildId;
                entry.ClassId = original.ClassId;
                entry.StateBits = original.StateBits;
                entry.CreationTimeRaw = original.CreationTimeRaw;
                entry.ModifiedTimeRaw = original.ModifiedTimeRaw;
                entry.CreationTimeUtc = original.CreationTimeUtc;
                entry.ModifiedTimeUtc = original.ModifiedTimeUtc;
                entry.StartSector = original.StartSector;
                entry.Size = original.Size;
                entry.ParentId = original.ParentId;
                entry.DirectoryOffset = original.DirectoryOffset;
                result.Add(entry);
            }

            return result;
        }

        private static IDictionary<int, byte[]> AddStreamEntries(
            Ole2File source,
            List<Ole2DirectoryEntry> entries,
            IDictionary<int, byte[]> streamChanges,
            IList<Ole2StreamAddition> streamAdditions)
        {
            if (streamAdditions == null ||
                streamAdditions.Count == 0)
            {
                return streamChanges;
            }

            Dictionary<int, byte[]> changes =
                new Dictionary<int, byte[]>();
            if (streamChanges != null)
            {
                foreach (KeyValuePair<int, byte[]> pair
                    in streamChanges)
                {
                    changes.Add(pair.Key, pair.Value);
                }
            }

            int index;
            for (index = 0;
                index < streamAdditions.Count;
                index++)
            {
                Ole2StreamAddition addition =
                    streamAdditions[index];
                if (addition == null)
                {
                    throw new ArgumentException(
                        "An OLE2 stream addition is null.",
                        "streamAdditions");
                }
                if (addition.ParentEntryId < 0 ||
                    addition.ParentEntryId >=
                        source.Entries.Count)
                {
                    throw new ArgumentException(
                        "An OLE2 stream addition parent is invalid.",
                        "streamAdditions");
                }

                Ole2DirectoryEntry parent =
                    entries[addition.ParentEntryId];
                if (parent.ObjectType != 1 &&
                    parent.ObjectType != 5)
                {
                    throw new ArgumentException(
                        "An OLE2 stream addition parent is not a storage.",
                        "streamAdditions");
                }
                if (string.IsNullOrEmpty(addition.Name))
                {
                    throw new ArgumentException(
                        "An OLE2 stream addition name is empty.",
                        "streamAdditions");
                }
                if (addition.Data == null)
                {
                    throw new ArgumentException(
                        "An OLE2 stream addition is null.",
                        "streamAdditions");
                }

                Ole2DirectoryEntry entry =
                    new Ole2DirectoryEntry();
                entry.Id = entries.Count;
                entry.Name = addition.Name;
                entry.ObjectType = 2;
                entry.ColorFlag = 1;
                entry.LeftSiblingId = Ole2File.NoStream;
                entry.RightSiblingId = Ole2File.NoStream;
                entry.ChildId = Ole2File.NoStream;
                entry.ClassId = Guid.Empty;
                entry.StateBits = 0;
                entry.CreationTimeRaw = 0;
                entry.ModifiedTimeRaw = 0;
                entry.CreationTimeUtc = null;
                entry.ModifiedTimeUtc = null;
                entry.StartSector = Ole2File.EndOfChain;
                entry.Size = (ulong)addition.Data.Length;
                entry.ParentId = addition.ParentEntryId;
                entry.DirectoryOffset = -1;
                entries.Add(entry);
                changes.Add(entry.Id, addition.Data);
            }

            return changes;
        }

        private static void ValidateParents(
            List<Ole2DirectoryEntry> entries)
        {
            int index;
            for (index = 0; index < entries.Count; index++)
            {
                Ole2DirectoryEntry entry = entries[index];
                if (entry.Id != index)
                {
                    throw new InvalidDataException(
                        "OLE2 directory entry IDs are not contiguous.");
                }
                if (entry.ObjectType == 0)
                {
                    continue;
                }
                if (index == 0)
                {
                    if (entry.ObjectType != 5)
                    {
                        throw new InvalidDataException(
                            "The first OLE2 entry is not the root.");
                    }
                    continue;
                }
                if (entry.ObjectType == 5)
                {
                    throw new InvalidDataException(
                        "OLE2 contains multiple root entries.");
                }
                if (entry.ParentId < 0 ||
                    entry.ParentId >= entries.Count)
                {
                    throw new InvalidDataException(
                        "OLE2 entry is not attached to a storage: " +
                        entry.Name);
                }

                byte parentType = entries[entry.ParentId].ObjectType;
                if (parentType != 1 && parentType != 5)
                {
                    throw new InvalidDataException(
                        "OLE2 entry parent is not a storage: " +
                        entry.Name);
                }
            }
        }

        private static void BuildDirectoryTrees(
            List<Ole2DirectoryEntry> entries)
        {
            Dictionary<int, List<int>> children =
                new Dictionary<int, List<int>>();
            int index;
            for (index = 0; index < entries.Count; index++)
            {
                Ole2DirectoryEntry entry = entries[index];
                entry.LeftSiblingId = Ole2File.NoStream;
                entry.RightSiblingId = Ole2File.NoStream;
                entry.ChildId = Ole2File.NoStream;
                if (entry.ObjectType != 0)
                {
                    entry.ColorFlag = 1;
                }

                if (index > 0 && entry.ObjectType != 0)
                {
                    List<int> list;
                    if (!children.TryGetValue(entry.ParentId, out list))
                    {
                        list = new List<int>();
                        children.Add(entry.ParentId, list);
                    }
                    list.Add(index);
                }
            }

            foreach (KeyValuePair<int, List<int>> pair in children)
            {
                List<int> list = pair.Value;
                list.Sort(delegate(int left, int right)
                {
                    return CompareDirectoryNames(
                        entries[left].Name,
                        entries[right].Name);
                });

                for (index = 1; index < list.Count; index++)
                {
                    if (CompareDirectoryNames(
                        entries[list[index - 1]].Name,
                        entries[list[index]].Name) == 0)
                    {
                        throw new InvalidDataException(
                            "Duplicate OLE2 child name: " +
                            entries[list[index]].Name);
                    }
                }

                entries[pair.Key].ChildId = (uint)BuildBalancedTree(
                    entries,
                    list,
                    0,
                    list.Count);
            }
        }

        private static int BuildBalancedTree(
            List<Ole2DirectoryEntry> entries,
            List<int> sortedIds,
            int start,
            int count)
        {
            if (count == 0)
            {
                return -1;
            }

            int leftCount = count / 2;
            int rootIndex = start + leftCount;
            int id = sortedIds[rootIndex];
            int left = BuildBalancedTree(
                entries,
                sortedIds,
                start,
                leftCount);
            int right = BuildBalancedTree(
                entries,
                sortedIds,
                rootIndex + 1,
                count - leftCount - 1);

            entries[id].LeftSiblingId =
                left < 0 ? Ole2File.NoStream : (uint)left;
            entries[id].RightSiblingId =
                right < 0 ? Ole2File.NoStream : (uint)right;
            entries[id].ColorFlag = 1;
            return id;
        }

        private static int CompareDirectoryNames(
            string left,
            string right)
        {
            int leftLength = Encoding.Unicode.GetByteCount(left) + 2;
            int rightLength = Encoding.Unicode.GetByteCount(right) + 2;
            int lengthResult = leftLength.CompareTo(rightLength);
            if (lengthResult != 0)
            {
                return lengthResult;
            }

            int index;
            for (index = 0; index < left.Length; index++)
            {
                char leftCharacter = char.ToUpperInvariant(left[index]);
                char rightCharacter = char.ToUpperInvariant(right[index]);
                int characterResult =
                    ((int)leftCharacter).CompareTo((int)rightCharacter);
                if (characterResult != 0)
                {
                    return characterResult;
                }
            }

            return 0;
        }

        private static List<StreamPlan> BuildStreamPlans(
            Ole2File source,
            List<Ole2DirectoryEntry> entries,
            IDictionary<int, byte[]> streamChanges,
            HashSet<int> usedChanges)
        {
            List<StreamPlan> result = new List<StreamPlan>();
            int index;
            for (index = 0; index < entries.Count; index++)
            {
                Ole2DirectoryEntry entry = entries[index];
                if (entry.ObjectType != 2)
                {
                    continue;
                }

                byte[] data;
                if (streamChanges != null &&
                    streamChanges.TryGetValue(index, out data))
                {
                    if (data == null)
                    {
                        throw new ArgumentException(
                            "An OLE2 stream change is null.",
                            "streamChanges");
                    }
                    usedChanges.Add(index);
                }
                else
                {
                    data = source.ReadStream(source.Entries[index]);
                }

                StreamPlan plan = new StreamPlan();
                plan.Entry = entry;
                plan.Data = data;
                plan.IsMini =
                    data.Length > 0 &&
                    data.Length < MiniStreamCutoff;
                plan.SectorCount =
                    plan.IsMini ?
                    0 :
                    DivideRoundUp(data.Length, SectorSize);
                plan.MiniSectorCount =
                    plan.IsMini ?
                    DivideRoundUp(data.Length, MiniSectorSize) :
                    0;
                plan.StartSector = Ole2File.EndOfChain;
                plan.MiniStartSector = Ole2File.EndOfChain;
                result.Add(plan);
            }

            return result;
        }

        private static void ValidateChanges(
            IDictionary<int, byte[]> streamChanges,
            HashSet<int> usedChanges)
        {
            if (streamChanges == null)
            {
                return;
            }

            foreach (KeyValuePair<int, byte[]> pair in streamChanges)
            {
                if (!usedChanges.Contains(pair.Key))
                {
                    throw new ArgumentException(
                        "An OLE2 stream change does not identify a stream.",
                        "streamChanges");
                }
            }
        }

        private static int AssignMiniSectors(
            List<StreamPlan> streams)
        {
            int next = 0;
            int index;
            for (index = 0; index < streams.Count; index++)
            {
                StreamPlan stream = streams[index];
                if (!stream.IsMini)
                {
                    continue;
                }

                stream.MiniStartSector = next;
                next = CheckedAdd(
                    next,
                    stream.MiniSectorCount,
                    "mini sector allocation");
            }

            return next;
        }

        private static byte[] BuildMiniStream(
            List<StreamPlan> streams,
            int miniSectorCount)
        {
            int length = CheckedMultiply(
                miniSectorCount,
                MiniSectorSize,
                "mini stream size");
            byte[] result = new byte[length];
            int index;
            for (index = 0; index < streams.Count; index++)
            {
                StreamPlan stream = streams[index];
                if (!stream.IsMini)
                {
                    continue;
                }

                Buffer.BlockCopy(
                    stream.Data,
                    0,
                    result,
                    stream.MiniStartSector * MiniSectorSize,
                    stream.Data.Length);
            }

            return result;
        }

        private static int[] BuildMiniFat(
            List<StreamPlan> streams,
            int miniSectorCount)
        {
            if (miniSectorCount == 0)
            {
                return new int[0];
            }

            int entryCount = CheckedMultiply(
                DivideRoundUp(
                    miniSectorCount,
                    FatEntriesPerSector),
                FatEntriesPerSector,
                "mini FAT size");
            int[] result = CreateFilledIntArray(
                entryCount,
                Ole2File.FreeSector);
            int index;
            for (index = 0; index < streams.Count; index++)
            {
                StreamPlan stream = streams[index];
                if (stream.IsMini)
                {
                    MarkChain(
                        result,
                        stream.MiniStartSector,
                        stream.MiniSectorCount);
                }
            }

            return result;
        }

        private static void CalculateAllocationTableCounts(
            int baseSectorCount,
            out int fatSectorCount,
            out int difatSectorCount)
        {
            fatSectorCount = DivideRoundUp(
                baseSectorCount,
                FatEntriesPerSector);
            difatSectorCount = CalculateDifatCount(fatSectorCount);

            while (true)
            {
                int total = CheckedAdd(
                    baseSectorCount,
                    fatSectorCount,
                    "total sector count");
                total = CheckedAdd(
                    total,
                    difatSectorCount,
                    "total sector count");
                int nextFat = DivideRoundUp(
                    total,
                    FatEntriesPerSector);
                int nextDifat = CalculateDifatCount(nextFat);
                if (nextFat == fatSectorCount &&
                    nextDifat == difatSectorCount)
                {
                    return;
                }

                fatSectorCount = nextFat;
                difatSectorCount = nextDifat;
            }
        }

        private static int CalculateDifatCount(int fatSectorCount)
        {
            if (fatSectorCount <= 109)
            {
                return 0;
            }

            return DivideRoundUp(
                fatSectorCount - 109,
                DifatEntriesPerSector);
        }

        private static int[] CreateFilledIntArray(
            int length,
            int value)
        {
            int[] result = new int[length];
            int index;
            for (index = 0; index < result.Length; index++)
            {
                result[index] = value;
            }
            return result;
        }

        private static void MarkChain(
            int[] allocationTable,
            int start,
            int count)
        {
            if (count == 0)
            {
                return;
            }
            if (start < 0 ||
                (long)start + (long)count > allocationTable.Length)
            {
                throw new InvalidDataException(
                    "OLE2 sector chain is outside its allocation table.");
            }

            int index;
            for (index = 0; index < count; index++)
            {
                allocationTable[start + index] =
                    index + 1 < count ?
                    start + index + 1 :
                    Ole2File.EndOfChain;
            }
        }

        private static byte[] BuildDirectoryBytes(
            List<Ole2DirectoryEntry> entries,
            int byteCount)
        {
            byte[] result = new byte[byteCount];
            int index;
            for (index = 0; index < entries.Count; index++)
            {
                Ole2DirectoryEntry entry = entries[index];
                if (entry.ObjectType != 0)
                {
                    WriteDirectoryEntry(
                        result,
                        index * 128,
                        entry);
                }
            }

            return result;
        }

        private static void WriteDirectoryEntry(
            byte[] output,
            int offset,
            Ole2DirectoryEntry entry)
        {
            byte[] nameBytes = Encoding.Unicode.GetBytes(
                entry.Name + "\0");
            if (nameBytes.Length < 2 || nameBytes.Length > 64)
            {
                throw new InvalidDataException(
                    "OLE2 directory name is too long: " + entry.Name);
            }

            Buffer.BlockCopy(
                nameBytes,
                0,
                output,
                offset,
                nameBytes.Length);
            WriteUInt16(output, offset + 64, (ushort)nameBytes.Length);
            output[offset + 66] = entry.ObjectType;
            output[offset + 67] = entry.ColorFlag;
            WriteUInt32(output, offset + 68, entry.LeftSiblingId);
            WriteUInt32(output, offset + 72, entry.RightSiblingId);
            WriteUInt32(output, offset + 76, entry.ChildId);

            byte[] classId = entry.ClassId.ToByteArray();
            Buffer.BlockCopy(
                classId,
                0,
                output,
                offset + 80,
                classId.Length);
            WriteUInt32(output, offset + 96, entry.StateBits);
            WriteUInt64(
                output,
                offset + 100,
                unchecked((ulong)entry.CreationTimeRaw));
            WriteUInt64(
                output,
                offset + 108,
                unchecked((ulong)entry.ModifiedTimeRaw));
            WriteInt32(output, offset + 116, entry.StartSector);
            WriteUInt64(output, offset + 120, entry.Size);
        }

        private static void WriteHeader(
            byte[] output,
            List<int> fatSectorIds,
            int directoryStart,
            int miniFatStart,
            int miniFatSectorCount,
            List<int> difatSectorIds)
        {
            byte[] signature = new byte[]
            {
                0xD0, 0xCF, 0x11, 0xE0, 0xA1, 0xB1, 0x1A, 0xE1
            };
            Buffer.BlockCopy(
                signature,
                0,
                output,
                0,
                signature.Length);
            WriteUInt16(output, 24, 0x003E);
            WriteUInt16(output, 26, 0x0003);
            WriteUInt16(output, 28, 0xFFFE);
            WriteUInt16(output, 30, 0x0009);
            WriteUInt16(output, 32, 0x0006);
            WriteUInt32(output, 40, 0);
            WriteUInt32(output, 44, (uint)fatSectorIds.Count);
            WriteInt32(output, 48, directoryStart);
            WriteUInt32(output, 52, 0);
            WriteUInt32(output, 56, MiniStreamCutoff);
            WriteInt32(output, 60, miniFatStart);
            WriteUInt32(output, 64, (uint)miniFatSectorCount);
            WriteInt32(
                output,
                68,
                difatSectorIds.Count == 0 ?
                    Ole2File.EndOfChain :
                    difatSectorIds[0]);
            WriteUInt32(output, 72, (uint)difatSectorIds.Count);

            int index;
            for (index = 0; index < 109; index++)
            {
                int value =
                    index < fatSectorIds.Count ?
                    fatSectorIds[index] :
                    Ole2File.FreeSector;
                WriteInt32(output, 76 + index * 4, value);
            }
        }

        private static void WriteSectorData(
            byte[] output,
            int startSector,
            byte[] data)
        {
            if (data.Length == 0)
            {
                return;
            }

            int offset = CheckedMultiply(
                startSector + 1,
                SectorSize,
                "sector offset");
            if ((long)offset + (long)data.Length > output.Length)
            {
                throw new InvalidDataException(
                    "OLE2 sector data is outside the output.");
            }

            Buffer.BlockCopy(data, 0, output, offset, data.Length);
        }

        private static void WriteIntArraySectors(
            byte[] output,
            int startSector,
            int[] values)
        {
            int index;
            for (index = 0; index < values.Length; index++)
            {
                int sectorOffset = CheckedMultiply(
                    startSector + 1,
                    SectorSize,
                    "integer sector offset");
                WriteInt32(
                    output,
                    sectorOffset + index * 4,
                    values[index]);
            }
        }

        private static void WriteFatSectors(
            byte[] output,
            List<int> fatSectorIds,
            int[] fat)
        {
            int sectorIndex;
            for (sectorIndex = 0;
                sectorIndex < fatSectorIds.Count;
                sectorIndex++)
            {
                int outputOffset = CheckedMultiply(
                    fatSectorIds[sectorIndex] + 1,
                    SectorSize,
                    "FAT sector offset");
                int item;
                for (item = 0;
                    item < FatEntriesPerSector;
                    item++)
                {
                    WriteInt32(
                        output,
                        outputOffset + item * 4,
                        fat[
                            sectorIndex *
                                FatEntriesPerSector +
                            item]);
                }
            }
        }

        private static void WriteDifatSectors(
            byte[] output,
            List<int> fatSectorIds,
            List<int> difatSectorIds)
        {
            int sectorIndex;
            for (sectorIndex = 0;
                sectorIndex < difatSectorIds.Count;
                sectorIndex++)
            {
                int outputOffset = CheckedMultiply(
                    difatSectorIds[sectorIndex] + 1,
                    SectorSize,
                    "DIFAT sector offset");
                int item;
                for (item = 0;
                    item < DifatEntriesPerSector;
                    item++)
                {
                    int fatIndex =
                        109 +
                        sectorIndex * DifatEntriesPerSector +
                        item;
                    int value =
                        fatIndex < fatSectorIds.Count ?
                        fatSectorIds[fatIndex] :
                        Ole2File.FreeSector;
                    WriteInt32(
                        output,
                        outputOffset + item * 4,
                        value);
                }

                int next =
                    sectorIndex + 1 < difatSectorIds.Count ?
                    difatSectorIds[sectorIndex + 1] :
                    Ole2File.EndOfChain;
                WriteInt32(
                    output,
                    outputOffset + SectorSize - 4,
                    next);
            }
        }

        private static int DivideRoundUp(int value, int divisor)
        {
            if (value < 0)
            {
                throw new InvalidDataException(
                    "A negative OLE2 allocation size was requested.");
            }
            if (value == 0)
            {
                return 0;
            }

            return (int)(
                ((long)value + (long)divisor - 1L) /
                (long)divisor);
        }

        private static int CheckedAdd(
            int left,
            int right,
            string label)
        {
            try
            {
                return checked(left + right);
            }
            catch (OverflowException)
            {
                throw new InvalidDataException(
                    "OLE2 " + label + " is too large.");
            }
        }

        private static int CheckedMultiply(
            int left,
            int right,
            string label)
        {
            try
            {
                return checked(left * right);
            }
            catch (OverflowException)
            {
                throw new InvalidDataException(
                    "OLE2 " + label + " is too large.");
            }
        }

        private static void WriteUInt16(
            byte[] output,
            int offset,
            ushort value)
        {
            output[offset] = (byte)(value & 0xFF);
            output[offset + 1] = (byte)(value >> 8);
        }

        private static void WriteInt32(
            byte[] output,
            int offset,
            int value)
        {
            WriteUInt32(output, offset, unchecked((uint)value));
        }

        private static void WriteUInt32(
            byte[] output,
            int offset,
            uint value)
        {
            output[offset] = (byte)(value & 0xFFU);
            output[offset + 1] = (byte)((value >> 8) & 0xFFU);
            output[offset + 2] = (byte)((value >> 16) & 0xFFU);
            output[offset + 3] = (byte)(value >> 24);
        }

        private static void WriteUInt64(
            byte[] output,
            int offset,
            ulong value)
        {
            int index;
            for (index = 0; index < 8; index++)
            {
                output[offset + index] = (byte)(
                    (value >> (index * 8)) & 0xFFUL);
            }
        }
    }
}
