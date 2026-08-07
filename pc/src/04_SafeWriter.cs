using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace PubTransfer
{
    // Turning a verified transfer into files on this machine.
    //
    // The page hands over a DCW1 block: a small JSON header naming each file
    // and its length, then the bytes. The page has already checked the bundle's
    // SHA-256 and every file's — this side never writes anything that has not
    // been verified, and never takes the page's word for a path.
    //
    //   0   "DCW1"          4 bytes
    //   4   u32 headerLen   little-endian
    //   8   header          UTF-8 JSON: { "label": "...", "files": [ { "path": "...", "size": n } ] }
    //   ..  file bytes      concatenated, in header order
    public sealed class WriteRequest
    {
        public string Label;
        public List<WriteEntry> Files = new List<WriteEntry>();
        public byte[] Body;
        public int BodyOffset;
    }

    public sealed class WriteEntry
    {
        public string Path;
        public int Size;
    }

    public sealed class WriteOutcome
    {
        public string SavedAs;
        public int FileCount;
        public long TotalSize;
        public string FullPath;
    }

    public sealed class HostActionException : Exception
    {
        public readonly string Code;

        public HostActionException(string code, string message)
            : base(message)
        {
            Code = code;
        }
    }

    public static class SafeWriter
    {
        public const int MaxFiles = 2000;
        public const long MaxTotalBytes = 64L * 1024 * 1024;
        private const int HeaderLength = 8;

        public static WriteRequest Parse(byte[] block)
        {
            if (block == null || block.Length < HeaderLength)
            {
                throw new HostActionException("truncated", "受信データが短すぎます。");
            }
            if (block[0] != 'D' || block[1] != 'C' || block[2] != 'W' || block[3] != '1')
            {
                throw new HostActionException("bad-magic", "保存要求の形式が不正です。");
            }
            // Read as unsigned, then range-check BEFORE it is used as a length.
            // A declared 4 GB header would otherwise become a negative int and
            // then an offset that walks backwards through the buffer.
            long headerLength = (long)BitConverter.ToUInt32(block, 4);
            if (headerLength <= 0 || headerLength > 4L * 1024 * 1024 ||
                HeaderLength + headerLength > block.Length)
            {
                throw new HostActionException("bad-header", "保存要求の見出しが不正です。");
            }

            string json = Encoding.UTF8.GetString(block, HeaderLength, (int)headerLength);
            Json.Node header = Json.Parse(json);
            WriteRequest request = new WriteRequest();
            request.Label = header.String("label");
            if (string.IsNullOrEmpty(request.Label) || request.Label.IndexOf('/') >= 0 ||
                !SafePath.IsSafe(request.Label))
            {
                throw new HostActionException("bad-label", "転送の名前が保存先の名前として使えません。");
            }

            List<Json.Node> files = header.Array("files");
            if (files.Count == 0)
            {
                throw new HostActionException("empty", "保存するファイルがありません。");
            }
            if (files.Count > MaxFiles)
            {
                throw new HostActionException("too-many", "ファイル数が上限を超えています。");
            }

            long total = 0;
            List<string> paths = new List<string>();
            foreach (Json.Node file in files)
            {
                WriteEntry entry = new WriteEntry();
                entry.Path = file.String("path");
                string reason;
                if (!SafePath.Check(entry.Path, out reason))
                {
                    throw new HostActionException("bad-path", "受け取れないパスが含まれています: " + reason);
                }
                double size = file.Number("size");
                if (size < 0 || size > MaxTotalBytes || size != Math.Floor(size))
                {
                    throw new HostActionException("bad-size", "ファイルサイズが不正です。");
                }
                entry.Size = (int)size;
                total += entry.Size;
                if (total > MaxTotalBytes)
                {
                    throw new HostActionException("too-large", "合計サイズが上限を超えています。");
                }
                paths.Add(entry.Path);
                request.Files.Add(entry);
            }
            string conflict = SafePath.FindSetConflict(paths);
            if (conflict != null)
            {
                throw new HostActionException("path-conflict", conflict);
            }

            int bodyOffset = HeaderLength + (int)headerLength;
            // Exact, not "at least". Trailing bytes mean the two sides disagree
            // about what this block is.
            if (block.Length != bodyOffset + total)
            {
                throw new HostActionException("length-mismatch", "受信データの長さが見出しと合いません。");
            }
            request.Body = block;
            request.BodyOffset = bodyOffset;
            return request;
        }

        /// Write into `destinationRoot`, under a folder of its own.
        ///
        /// Staged inside the destination rather than in %TEMP%: a rename across
        /// volumes is a recursive copy, and a recursive copy is not atomic. A
        /// rename inside one folder is. Any failure removes the staging folder
        /// whole, so a half-written tree is never left for somebody to find.
        public static WriteOutcome Write(string destinationRoot, WriteRequest request)
        {
            if (string.IsNullOrEmpty(destinationRoot) || !Directory.Exists(destinationRoot))
            {
                throw new HostActionException("destination-missing", "保存先のフォルダーが見つかりません。");
            }
            string root = Path.GetFullPath(destinationRoot);
            string staging = Path.Combine(
                root,
                ".pub-transfer-incoming-" + Guid.NewGuid().ToString("N").Substring(0, 12));

            long written = 0;
            int count = 0;
            try
            {
                Directory.CreateDirectory(staging);
                int offset = request.BodyOffset;
                HashSet<string> created = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (WriteEntry entry in request.Files)
                {
                    string target = Path.GetFullPath(Path.Combine(staging, entry.Path.Replace('/', Path.DirectorySeparatorChar)));
                    if (!IsInside(staging, target))
                    {
                        throw new HostActionException("escapes", "保存先の外を指すパスがありました。");
                    }
                    string parent = Path.GetDirectoryName(target);
                    if (!created.Contains(parent))
                    {
                        EnsureDirectoryChain(staging, parent, created);
                    }
                    // CreateNew: if anything is already there this throws
                    // rather than replacing it. Inside a folder created a
                    // moment ago nothing should be — and if something is, that
                    // is exactly what we want to hear about.
                    using (FileStream stream = new FileStream(
                        target,
                        FileMode.CreateNew,
                        FileAccess.Write,
                        FileShare.None))
                    {
                        stream.Write(request.Body, offset, entry.Size);
                    }
                    offset += entry.Size;
                    MarkOfTheWeb(target);
                    written += entry.Size;
                    count++;
                }

                string name = FreeName(root, request.Label);
                string finalPath = Path.Combine(root, name);
                if (!IsInside(root, Path.GetFullPath(finalPath)))
                {
                    throw new HostActionException("escapes", "保存先の外へ書き出そうとしました。");
                }
                Directory.Move(staging, finalPath);

                WriteOutcome outcome = new WriteOutcome();
                outcome.SavedAs = name;
                outcome.FileCount = count;
                outcome.TotalSize = written;
                outcome.FullPath = finalPath;
                return outcome;
            }
            catch (Exception error)
            {
                try
                {
                    if (Directory.Exists(staging))
                    {
                        Directory.Delete(staging, true);
                    }
                }
                catch
                {
                }
                if (error is HostActionException)
                {
                    throw;
                }
                if (error is UnauthorizedAccessException)
                {
                    throw new HostActionException("no-permission", "保存先に書き込む権限がありません。");
                }
                if (error is IOException && IsDiskFull(error))
                {
                    throw new HostActionException("no-space", "保存先の空き容量が足りません。");
                }
                if (error is PathTooLongException)
                {
                    throw new HostActionException("path-too-long", "保存先のパスが長すぎます。");
                }
                throw new HostActionException("write-failed", "保存できませんでした: " + error.Message);
            }
        }

        private static void EnsureDirectoryChain(string staging, string leaf, HashSet<string> created)
        {
            List<string> chain = new List<string>();
            string current = leaf;
            while (!string.Equals(
                       Path.GetFullPath(current),
                       Path.GetFullPath(staging),
                       StringComparison.OrdinalIgnoreCase))
            {
                chain.Add(current);
                string parent = Path.GetDirectoryName(current);
                if (string.IsNullOrEmpty(parent) || parent == current)
                {
                    throw new HostActionException("escapes", "保存先の外を指すパスがありました。");
                }
                current = parent;
            }
            chain.Reverse();
            foreach (string level in chain)
            {
                if (created.Contains(level))
                {
                    continue;
                }
                // A directory that is a reparse point — a junction, or a
                // symbolic link — is a door out of the folder we were given,
                // whoever created it. String checks cannot see one; only asking
                // the filesystem can.
                if (Directory.Exists(level))
                {
                    FileAttributes attributes = File.GetAttributes(level);
                    if ((attributes & FileAttributes.ReparsePoint) != 0)
                    {
                        throw new HostActionException(
                            "link-in-path",
                            "保存先の途中にリンクがあります。中止しました。");
                    }
                }
                else
                {
                    Directory.CreateDirectory(level);
                }
                if (!IsInside(staging, Path.GetFullPath(level)))
                {
                    throw new HostActionException("escapes", "保存先の外を指すパスがありました。");
                }
                created.Add(level);
            }
        }

        public static bool IsInside(string root, string candidate)
        {
            string a = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar);
            string b = Path.GetFullPath(candidate);
            if (string.Equals(a, b, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
            // The separator matters: without it, C:\dataEVIL passes a prefix
            // test against C:\data.
            return b.StartsWith(a + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
        }

        private static string FreeName(string parent, string name)
        {
            if (!Directory.Exists(Path.Combine(parent, name)) &&
                !File.Exists(Path.Combine(parent, name)))
            {
                return name;
            }
            for (int n = 2; n < 1000; n++)
            {
                string candidate = name + " (" + n.ToString() + ")";
                if (!Directory.Exists(Path.Combine(parent, candidate)) &&
                    !File.Exists(Path.Combine(parent, candidate)))
                {
                    return candidate;
                }
            }
            throw new HostActionException("no-free-name", "保存先に空いている名前が見つかりませんでした。");
        }

        /// Mark of the Web.
        ///
        /// These bytes came from another machine over a channel with no
        /// authentication at the content level. Without this stream, Office
        /// opens a received .docm with macros live and SmartScreen never runs.
        /// Best effort: a destination on a filesystem without alternate data
        /// streams simply cannot carry it, and that is not a reason to fail a
        /// transfer that is otherwise complete.
        private static void MarkOfTheWeb(string filePath)
        {
            try
            {
                File.WriteAllText(
                    filePath + ":Zone.Identifier",
                    "[ZoneTransfer]\r\nZoneId=3\r\n",
                    new UTF8Encoding(false));
            }
            catch
            {
            }
        }

        private static bool IsDiskFull(Exception error)
        {
            int code = System.Runtime.InteropServices.Marshal.GetHRForException(error) & 0xffff;
            return code == 39 || code == 112; // ERROR_HANDLE_DISK_FULL / ERROR_DISK_FULL
        }

        /// Can this folder probably be written into?
        ///
        /// A HINT, shown next to a destination so nobody starts a ten-minute
        /// transfer into an unplugged drive. Deliberately not a proof, and
        /// deliberately creates nothing — see the note below.
        public static bool IsWritable(string path)
        {
            if (!Directory.Exists(path))
            {
                return false;
            }
            // Asked, not poked. Creating and deleting a probe leaves litter:
            // on Windows a delete only takes effect when the last handle
            // closes, and something on a typical machine holds new entries long
            // enough that the probe's name is still listed afterwards. A hint
            // that leaves debris in somebody's receive folder is a worse
            // bargain than a hint that is occasionally optimistic — the
            // authoritative answer is the write itself, which reports its own
            // failure and leaves nothing behind.
            try
            {
                DirectoryInfo info = new DirectoryInfo(path);
                if ((info.Attributes & FileAttributes.ReadOnly) != 0)
                {
                    return false;
                }
                // Enumerating requires the same traversal rights writing does,
                // and it costs one call.
                info.EnumerateFileSystemInfos().GetEnumerator().MoveNext();
                return true;
            }
            catch
            {
                return false;
            }
        }
    }
}
