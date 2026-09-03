using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;

namespace MacroStudio
{
    // What a workbook carries besides its VBA code. This tool only ever
    // reads and rewrites modules, so everything listed here is work for a
    // person: it is found and named, never changed. Names and counts
    // only - a connection's actual target, like a save location or a
    // server, is the owner's to manage and is not copied out.
    public sealed class BookInventory
    {
        public string Sha256;
        public long SizeBytes;
        public string ModifiedUtc;
        public List<string> References;
        public List<string> Connections;
        public List<string> BarcodeFonts;
        public bool HasPowerQuery;
        public int ActiveXCount;
        public int ExternalLinkCount;
        public bool HasVbaSignature;
        public bool Complete;

        public BookInventory()
        {
            References = new List<string>();
            Connections = new List<string>();
            BarcodeFonts = new List<string>();
            Complete = true;
        }
    }

    public static class BookInventoryReader
    {
        // Font names that only ever appear because someone is drawing a
        // barcode. The list is deliberately short and matched loosely:
        // it is a prompt to go and look, not a verdict.
        private static readonly string[] BarcodeFontMarkers =
        {
            "barcode",
            "code39",
            "code 39",
            "code128",
            "code 128",
            "3 of 9",
            "3of9",
            "idautomation",
            "ean13",
            "ean 13",
            "jan13",
            "itf",
            "nw-7",
            "codabar"
        };

        public static BookInventory Read(
            string filePath,
            byte[] bookBytes,
            VbaProjectData project)
        {
            BookInventory inventory = new BookInventory();

            ReadFileFacts(filePath, bookBytes, inventory);
            ReadReferences(project, inventory);
            ReadPackage(bookBytes, inventory);
            return inventory;
        }

        private static void ReadFileFacts(
            string filePath,
            byte[] bookBytes,
            BookInventory inventory)
        {
            if (bookBytes != null)
            {
                inventory.SizeBytes = bookBytes.LongLength;
                try
                {
                    using (SHA256 hash = SHA256.Create())
                    {
                        inventory.Sha256 = ToHex(hash.ComputeHash(bookBytes));
                    }
                }
                catch (Exception)
                {
                    inventory.Complete = false;
                }
            }
            try
            {
                if (!string.IsNullOrEmpty(filePath) && File.Exists(filePath))
                {
                    inventory.ModifiedUtc = File.GetLastWriteTimeUtc(filePath)
                        .ToString(
                            "yyyy-MM-dd HH:mm:ss'Z'",
                            CultureInfo.InvariantCulture);
                }
            }
            catch (Exception)
            {
                inventory.Complete = false;
            }
        }

        private static string ToHex(byte[] value)
        {
            StringBuilder text = new StringBuilder(value.Length * 2);
            int index;

            for (index = 0; index < value.Length; index++)
            {
                text.Append(value[index].ToString(
                    "x2",
                    CultureInfo.InvariantCulture));
            }
            return text.ToString();
        }

        // REFERENCENAME records in the project's dir stream. Reading the
        // names is enough to hand the list to a person; which library a
        // name resolves to on this machine is not this tool's to decide.
        private static void ReadReferences(
            VbaProjectData project,
            BookInventory inventory)
        {
            byte[] data = project == null ? null : project.DirDecompressed;
            Encoding encoding;
            int position = 0;

            if (data == null)
            {
                inventory.Complete = false;
                return;
            }
            encoding = project.Encoding != null
                ? project.Encoding
                : Encoding.GetEncoding(1252);
            while (position + 6 <= data.Length)
            {
                ushort id = BitConverter.ToUInt16(data, position);
                int size = BitConverter.ToInt32(data, position + 2);

                if (size < 0 ||
                    (long)position + 6L + (long)size > data.Length)
                {
                    inventory.Complete = false;
                    return;
                }
                if (id == 0x0016 && size > 0)
                {
                    string name = encoding
                        .GetString(data, position + 6, size)
                        .Trim();
                    if (name.Length > 0 &&
                        !inventory.References.Contains(name))
                    {
                        inventory.References.Add(name);
                    }
                }
                position += 6 + size;
                // MODULES has a two-byte Cookie the record size does not
                // cover, exactly as the code-page walk allows for.
                if (id == 0x0009 && position + 2 <= data.Length)
                {
                    position += 2;
                }
            }
        }

        private static void ReadPackage(
            byte[] bookBytes,
            BookInventory inventory)
        {
            if (bookBytes == null)
            {
                inventory.Complete = false;
                return;
            }
            try
            {
                using (MemoryStream memory = new MemoryStream(bookBytes, false))
                using (ZipArchive archive = new ZipArchive(
                    memory,
                    ZipArchiveMode.Read,
                    false))
                {
                    int index;
                    for (index = 0; index < archive.Entries.Count; index++)
                    {
                        ReadPackageEntry(archive.Entries[index], inventory);
                    }
                }
            }
            catch (InvalidDataException)
            {
                // An OLE2-era workbook is not a package. Its VBA still
                // reads; the rest of this list simply does not apply.
                inventory.Complete = false;
            }
            catch (NotSupportedException)
            {
                inventory.Complete = false;
            }
            catch (IOException)
            {
                inventory.Complete = false;
            }
        }

        private static void ReadPackageEntry(
            ZipArchiveEntry entry,
            BookInventory inventory)
        {
            string name = entry.FullName.Replace('\\', '/');
            string lower = name.ToLowerInvariant();

            if (lower.IndexOf("/activex", StringComparison.Ordinal) >= 0 &&
                lower.EndsWith(".bin", StringComparison.Ordinal))
            {
                inventory.ActiveXCount++;
                return;
            }
            if (lower.IndexOf(
                "/externallinks/externallink",
                StringComparison.Ordinal) >= 0 &&
                lower.EndsWith(".xml", StringComparison.Ordinal))
            {
                inventory.ExternalLinkCount++;
                return;
            }
            if (lower.IndexOf("vbaprojectsignature", StringComparison.Ordinal)
                >= 0)
            {
                inventory.HasVbaSignature = true;
                return;
            }
            if (lower.EndsWith("/connections.xml", StringComparison.Ordinal))
            {
                ReadConnections(entry, inventory);
                return;
            }
            if (lower.EndsWith("/styles.xml", StringComparison.Ordinal))
            {
                ReadFonts(entry, inventory);
                return;
            }
            if (lower.StartsWith("customxml/", StringComparison.Ordinal) &&
                lower.EndsWith(".xml", StringComparison.Ordinal))
            {
                ReadMashup(entry, inventory);
            }
        }

        private static string ReadEntryText(ZipArchiveEntry entry)
        {
            // A part this tool only scans for names never needs to be
            // held whole; anything past this is a workbook far outside
            // what the rest of the flow handles.
            const int Limit = 4 * 1024 * 1024;

            if (entry.Length > Limit)
            {
                return null;
            }
            try
            {
                using (Stream stream = entry.Open())
                using (StreamReader reader = new StreamReader(
                    stream,
                    Encoding.UTF8,
                    true))
                {
                    return reader.ReadToEnd();
                }
            }
            catch (InvalidDataException)
            {
                return null;
            }
            catch (IOException)
            {
                return null;
            }
        }

        // The connection's name, and nothing else. A connection string
        // carries servers, paths and sometimes credentials, and none of
        // that belongs in a list this tool writes to disk.
        private static void ReadConnections(
            ZipArchiveEntry entry,
            BookInventory inventory)
        {
            string text = ReadEntryText(entry);
            int position = 0;

            if (text == null)
            {
                inventory.Complete = false;
                return;
            }
            while (true)
            {
                int start = text.IndexOf(
                    "<connection ",
                    position,
                    StringComparison.Ordinal);
                int end;
                string element;
                string name;

                if (start < 0)
                {
                    return;
                }
                end = text.IndexOf('>', start);
                if (end < 0)
                {
                    return;
                }
                element = text.Substring(start, end - start);
                name = ReadAttribute(element, "name");
                if (!string.IsNullOrEmpty(name) &&
                    !inventory.Connections.Contains(name))
                {
                    inventory.Connections.Add(name);
                }
                position = end + 1;
            }
        }

        private static void ReadFonts(
            ZipArchiveEntry entry,
            BookInventory inventory)
        {
            string text = ReadEntryText(entry);
            int position = 0;

            if (text == null)
            {
                inventory.Complete = false;
                return;
            }
            while (true)
            {
                int start = text.IndexOf(
                    "<name ",
                    position,
                    StringComparison.Ordinal);
                int end;
                string name;

                if (start < 0)
                {
                    return;
                }
                end = text.IndexOf('>', start);
                if (end < 0)
                {
                    return;
                }
                name = ReadAttribute(
                    text.Substring(start, end - start),
                    "val");
                if (!string.IsNullOrEmpty(name) &&
                    LooksLikeBarcodeFont(name) &&
                    !inventory.BarcodeFonts.Contains(name))
                {
                    inventory.BarcodeFonts.Add(name);
                }
                position = end + 1;
            }
        }

        private static void ReadMashup(
            ZipArchiveEntry entry,
            BookInventory inventory)
        {
            string text = ReadEntryText(entry);

            if (text == null)
            {
                return;
            }
            if (text.IndexOf("DataMashup", StringComparison.Ordinal) >= 0)
            {
                inventory.HasPowerQuery = true;
            }
        }

        private static bool LooksLikeBarcodeFont(string name)
        {
            string lower = name.ToLowerInvariant();
            int index;

            for (index = 0; index < BarcodeFontMarkers.Length; index++)
            {
                if (lower.IndexOf(
                    BarcodeFontMarkers[index],
                    StringComparison.Ordinal) >= 0)
                {
                    return true;
                }
            }
            return false;
        }

        private static string ReadAttribute(string element, string attribute)
        {
            string marker = " " + attribute + "=\"";
            int start = element.IndexOf(marker, StringComparison.Ordinal);
            int end;

            if (start < 0)
            {
                return null;
            }
            start += marker.Length;
            end = element.IndexOf('"', start);
            if (end < 0)
            {
                return null;
            }
            return Decode(element.Substring(start, end - start));
        }

        private static string Decode(string value)
        {
            return value
                .Replace("&amp;", "&")
                .Replace("&lt;", "<")
                .Replace("&gt;", ">")
                .Replace("&quot;", "\"")
                .Replace("&apos;", "'");
        }
    }
}
