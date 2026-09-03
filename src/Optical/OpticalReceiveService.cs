using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;

namespace Ferry
{
    internal sealed class OpticalReceiveService
    {
        private readonly object _gate = new object();
        private readonly Dictionary<string, OpticalReceiveState> _receivers =
            new Dictionary<string, OpticalReceiveState>(StringComparer.Ordinal);
        private readonly string _outputRoot;

        public OpticalReceiveService(string outputRoot)
        {
            _outputRoot = Path.GetFullPath(outputRoot);
        }

        public void Reset(string receiverId)
        {
            receiverId = RequireReceiverId(receiverId);
            lock (_gate)
            {
                _receivers.Remove(receiverId);
            }
        }

        public void Stop(string receiverId)
        {
            Reset(receiverId);
        }

        public OpticalReceiveResult AddFrame(
            string receiverId,
            byte[] bytes,
            bool openWhenDone)
        {
            receiverId = RequireReceiverId(receiverId);
            OpticalFrame frame;
            if (!OpticalFrame.TryParse(bytes, out frame))
            {
                return OpticalReceiveResult.NotRecognized();
            }

            lock (_gate)
            {
                OpticalReceiveState state;
                if (!_receivers.TryGetValue(receiverId, out state)
                    || !state.Matches(frame))
                {
                    state = new OpticalReceiveState(frame);
                    _receivers[receiverId] = state;
                }

                if (state.CompletedResult != null)
                {
                    return state.CompletedResult;
                }

                state.Decoder.AddFrame(frame.Sequence, frame.Block);
                if (!state.Decoder.IsComplete)
                {
                    return state.DescribeProgress();
                }

                try
                {
                    var payload = state.Decoder.Assemble();
                    if (Fnv1a(payload) != frame.PayloadFnv)
                    {
                        throw new InvalidDataException(
                            "受信した光学転送の検算に失敗しました。もう一度読み取ってください。");
                    }

                    var saved = OpticalPayloadReader.Save(payload, _outputRoot);
                    string openError = null;
                    if (openWhenDone)
                    {
                        try
                        {
                            OutputLauncher.OpenFolder(saved.OutputPath);
                        }
                        catch (Exception exception)
                        {
                            openError = exception.Message;
                        }
                    }

                    state.CompletedResult = state.DescribeComplete(saved, openError);
                    return state.CompletedResult;
                }
                catch
                {
                    _receivers.Remove(receiverId);
                    throw;
                }
            }
        }

        private static string RequireReceiverId(string value)
        {
            if (string.IsNullOrWhiteSpace(value) || value.Length > 100)
            {
                throw new ArgumentException("受信画面を識別できません。", "receiverId");
            }
            return value;
        }

        private static uint Fnv1a(byte[] bytes)
        {
            var hash = 0x811c9dc5u;
            for (var index = 0; index < bytes.Length; index++)
            {
                hash ^= bytes[index];
                hash = unchecked(hash * 0x01000193u);
            }
            return hash;
        }
    }

    internal sealed class OpticalReceiveState
    {
        private readonly ushort _sessionId;
        private readonly int _blockCount;
        private readonly int _blockLength;
        private readonly int _totalLength;
        private readonly uint _payloadFnv;
        private readonly DateTime _startedUtc;

        public OpticalReceiveState(OpticalFrame firstFrame)
        {
            _sessionId = firstFrame.SessionId;
            _blockCount = firstFrame.BlockCount;
            _blockLength = firstFrame.BlockLength;
            _totalLength = firstFrame.TotalLength;
            _payloadFnv = firstFrame.PayloadFnv;
            _startedUtc = DateTime.UtcNow;
            Decoder = new FountainDecoder(
                _blockCount,
                _blockLength,
                _sessionId,
                _totalLength);
        }

        public FountainDecoder Decoder { get; private set; }
        public OpticalReceiveResult CompletedResult { get; set; }

        public bool Matches(OpticalFrame frame)
        {
            return frame.SessionId == _sessionId
                && frame.BlockCount == _blockCount
                && frame.BlockLength == _blockLength
                && frame.TotalLength == _totalLength
                && frame.PayloadFnv == _payloadFnv;
        }

        public OpticalReceiveResult DescribeProgress()
        {
            var elapsed = Math.Max(0.001, (DateTime.UtcNow - _startedUtc).TotalSeconds);
            var overhead = Math.Min(
                1.6,
                Math.Max(1.15, 1.1 + 2.45 / Math.Sqrt(Math.Max(1, _blockCount))));
            var expected = Math.Max(_blockCount + 1, (int)Math.Ceiling(_blockCount * overhead));
            var arrival = Math.Min(0.98, Decoder.FramesNew / (double)expected);
            var solved = 0.99 * Decoder.SolvedCount / _blockCount;
            var progress = Math.Max(arrival, solved);
            var rate = Decoder.FramesNew * _blockLength / overhead / 1024.0 / elapsed;
            return new OpticalReceiveResult(
                true,
                false,
                Decoder.FramesNew,
                Decoder.FramesDuplicate,
                _blockCount,
                Decoder.SolvedCount,
                _totalLength,
                progress,
                elapsed,
                rate,
                null,
                0,
                null,
                null);
        }

        public OpticalReceiveResult DescribeComplete(
            OpticalSavedPayload saved,
            string openError)
        {
            var elapsed = Math.Max(0.001, (DateTime.UtcNow - _startedUtc).TotalSeconds);
            return new OpticalReceiveResult(
                true,
                true,
                Decoder.FramesNew,
                Decoder.FramesDuplicate,
                _blockCount,
                Decoder.SolvedCount,
                _totalLength,
                1.0,
                elapsed,
                _totalLength / 1024.0 / elapsed,
                saved.Label,
                saved.FileCount,
                saved.OutputPath,
                openError);
        }
    }

    internal sealed class OpticalReceiveResult
    {
        public OpticalReceiveResult(
            bool recognized,
            bool complete,
            int framesCollected,
            int duplicateFrames,
            int sourceBlocks,
            int solvedBlocks,
            int totalBytes,
            double progress,
            double elapsedSeconds,
            double kilobytesPerSecond,
            string label,
            int fileCount,
            string outputPath,
            string openError)
        {
            Recognized = recognized;
            Complete = complete;
            FramesCollected = framesCollected;
            DuplicateFrames = duplicateFrames;
            SourceBlocks = sourceBlocks;
            SolvedBlocks = solvedBlocks;
            TotalBytes = totalBytes;
            Progress = progress;
            ElapsedSeconds = elapsedSeconds;
            KilobytesPerSecond = kilobytesPerSecond;
            Label = label;
            FileCount = fileCount;
            OutputPath = outputPath;
            OpenError = openError;
        }

        public bool Recognized { get; private set; }
        public bool Complete { get; private set; }
        public int FramesCollected { get; private set; }
        public int DuplicateFrames { get; private set; }
        public int SourceBlocks { get; private set; }
        public int SolvedBlocks { get; private set; }
        public int TotalBytes { get; private set; }
        public double Progress { get; private set; }
        public double ElapsedSeconds { get; private set; }
        public double KilobytesPerSecond { get; private set; }
        public string Label { get; private set; }
        public int FileCount { get; private set; }
        public string OutputPath { get; private set; }
        public string OpenError { get; private set; }

        public static OpticalReceiveResult NotRecognized()
        {
            return new OpticalReceiveResult(
                false,
                false,
                0,
                0,
                0,
                0,
                0,
                0,
                0,
                0,
                null,
                0,
                null,
                null);
        }
    }

    internal sealed class OpticalFrame
    {
        private const int HeaderLength = 20;
        private const int MaximumBlockLength = 2933;
        private const int MaximumPayloadLength = 64 * 1024 * 1024;

        private OpticalFrame()
        {
        }

        public ushort SessionId { get; private set; }
        public uint Sequence { get; private set; }
        public int BlockCount { get; private set; }
        public int BlockLength { get; private set; }
        public int TotalLength { get; private set; }
        public uint PayloadFnv { get; private set; }
        public byte[] Block { get; private set; }

        public static bool TryParse(byte[] bytes, out OpticalFrame frame)
        {
            frame = null;
            if (bytes == null
                || bytes.Length <= HeaderLength
                || bytes[0] != 0xd1
                || bytes[1] != 0x0c)
            {
                return false;
            }

            var blockCount = ReadUInt16(bytes, 8);
            var blockLength = ReadUInt16(bytes, 10);
            var totalLengthRaw = ReadUInt32(bytes, 12);
            if (blockCount == 0
                || blockLength == 0
                || blockLength > MaximumBlockLength
                || totalLengthRaw == 0
                || totalLengthRaw > MaximumPayloadLength
                || bytes.Length != HeaderLength + blockLength)
            {
                return false;
            }

            var totalLength = (int)totalLengthRaw;
            if ((totalLength + blockLength - 1) / blockLength != blockCount)
            {
                return false;
            }

            var block = new byte[blockLength];
            Buffer.BlockCopy(bytes, HeaderLength, block, 0, block.Length);
            frame = new OpticalFrame
            {
                SessionId = ReadUInt16(bytes, 2),
                Sequence = ReadUInt32(bytes, 4),
                BlockCount = blockCount,
                BlockLength = blockLength,
                TotalLength = totalLength,
                PayloadFnv = ReadUInt32(bytes, 16),
                Block = block
            };
            return true;
        }

        private static ushort ReadUInt16(byte[] bytes, int offset)
        {
            return (ushort)(bytes[offset] | (bytes[offset + 1] << 8));
        }

        private static uint ReadUInt32(byte[] bytes, int offset)
        {
            return (uint)(bytes[offset]
                | (bytes[offset + 1] << 8)
                | (bytes[offset + 2] << 16)
                | (bytes[offset + 3] << 24));
        }
    }

    internal sealed class FountainDecoder
    {
        private readonly int _blockCount;
        private readonly int _blockLength;
        private readonly int _totalLength;
        private readonly ushort _sessionId;
        private readonly int _words;
        private readonly double[] _cdf;
        private readonly uint[][] _solved;
        private readonly Dictionary<int, HashSet<PendingFountainFrame>> _byBlock =
            new Dictionary<int, HashSet<PendingFountainFrame>>();
        private readonly HashSet<uint> _seen = new HashSet<uint>();

        public FountainDecoder(
            int blockCount,
            int blockLength,
            ushort sessionId,
            int totalLength)
        {
            _blockCount = blockCount;
            _blockLength = blockLength;
            _totalLength = totalLength;
            _sessionId = sessionId;
            _words = (blockLength + 3) / 4;
            _cdf = FountainMath.SolitonCdf(blockCount);
            _solved = new uint[blockCount][];
        }

        public int FramesNew { get; private set; }
        public int FramesDuplicate { get; private set; }
        public int SolvedCount { get; private set; }
        public bool IsComplete { get { return SolvedCount >= _blockCount; } }

        public void AddFrame(uint sequence, byte[] block)
        {
            if (!_seen.Add(sequence))
            {
                FramesDuplicate++;
                return;
            }
            FramesNew++;
            if (IsComplete)
            {
                return;
            }

            var indices = new HashSet<int>(FountainMath.FrameIndices(
                _blockCount,
                _cdf,
                _sessionId,
                sequence));
            var words = ReadWords(block);
            var snapshot = new List<int>(indices);
            foreach (var index in snapshot)
            {
                if (_solved[index] != null)
                {
                    XorInto(words, _solved[index]);
                    indices.Remove(index);
                }
            }

            if (indices.Count == 0)
            {
                return;
            }
            if (indices.Count == 1)
            {
                Resolve(Only(indices), words);
                return;
            }

            var pending = new PendingFountainFrame(indices, words);
            foreach (var index in indices)
            {
                HashSet<PendingFountainFrame> waiting;
                if (!_byBlock.TryGetValue(index, out waiting))
                {
                    waiting = new HashSet<PendingFountainFrame>();
                    _byBlock.Add(index, waiting);
                }
                waiting.Add(pending);
            }
        }

        public byte[] Assemble()
        {
            if (!IsComplete)
            {
                throw new InvalidOperationException("光学転送はまだ揃っていません。");
            }

            var result = new byte[_totalLength];
            for (var block = 0; block < _blockCount; block++)
            {
                var start = block * _blockLength;
                var length = Math.Min(_blockLength, _totalLength - start);
                for (var index = 0; index < length; index++)
                {
                    result[start + index] = (byte)(
                        _solved[block][index / 4] >> ((index % 4) * 8));
                }
            }
            return result;
        }

        private void Resolve(int firstBlock, uint[] firstWords)
        {
            var stack = new Stack<SolvedFountainBlock>();
            stack.Push(new SolvedFountainBlock(firstBlock, firstWords));
            while (stack.Count > 0)
            {
                var solved = stack.Pop();
                if (_solved[solved.Index] != null)
                {
                    continue;
                }

                _solved[solved.Index] = solved.Words;
                SolvedCount++;
                HashSet<PendingFountainFrame> waiting;
                if (!_byBlock.TryGetValue(solved.Index, out waiting))
                {
                    continue;
                }
                _byBlock.Remove(solved.Index);

                foreach (var pending in waiting)
                {
                    XorInto(pending.Words, solved.Words);
                    pending.Indices.Remove(solved.Index);
                    if (pending.Indices.Count == 1)
                    {
                        var remaining = Only(pending.Indices);
                        HashSet<PendingFountainFrame> remainingFrames;
                        if (_byBlock.TryGetValue(remaining, out remainingFrames))
                        {
                            remainingFrames.Remove(pending);
                        }
                        if (_solved[remaining] == null)
                        {
                            stack.Push(new SolvedFountainBlock(remaining, pending.Words));
                        }
                    }
                }
            }
        }

        private uint[] ReadWords(byte[] block)
        {
            if (block == null || block.Length != _blockLength)
            {
                throw new ArgumentException("光学転送のブロック長が一致しません。", "block");
            }
            var words = new uint[_words];
            for (var index = 0; index < block.Length; index++)
            {
                words[index / 4] |= (uint)block[index] << ((index % 4) * 8);
            }
            return words;
        }

        private static void XorInto(uint[] target, uint[] source)
        {
            for (var index = 0; index < target.Length; index++)
            {
                target[index] ^= source[index];
            }
        }

        private static int Only(HashSet<int> values)
        {
            foreach (var value in values)
            {
                return value;
            }
            throw new InvalidOperationException("復元するブロックがありません。");
        }

        private sealed class PendingFountainFrame
        {
            public PendingFountainFrame(HashSet<int> indices, uint[] words)
            {
                Indices = indices;
                Words = words;
            }

            public HashSet<int> Indices { get; private set; }
            public uint[] Words { get; private set; }
        }

        private sealed class SolvedFountainBlock
        {
            public SolvedFountainBlock(int index, uint[] words)
            {
                Index = index;
                Words = words;
            }

            public int Index { get; private set; }
            public uint[] Words { get; private set; }
        }
    }

    internal static class OpticalPayloadReader
    {
        private const int FileHeaderLength = 49;
        private const int BundleHeaderLength = 8;
        private const int MaximumPayloadBytes = 64 * 1024 * 1024;
        private const int MaximumManifestBytes = 1024 * 1024;
        private const int MaximumFiles = 2000;
        private const string BundleMediaType = "application/vnd.decimen.bundle+dcb1";
        private static readonly UTF8Encoding Utf8 = new UTF8Encoding(false, true);

        public static OpticalSavedPayload Save(byte[] container, string outputRoot)
        {
            string mediaType;
            var bundle = UnpackFile(container, out mediaType);
            if (!string.Equals(mediaType, BundleMediaType, StringComparison.Ordinal))
            {
                throw new InvalidDataException("受信した内容は Ferry のフォルダ転送ではありません。");
            }

            var parsed = ReadBundle(bundle);
            var outputDirectory = OutputLayout.CreateRunDirectory(outputRoot, parsed.Label);
            foreach (var entry in parsed.Files)
            {
                var relative = entry.Path.Replace('/', Path.DirectorySeparatorChar);
                var target = Path.GetFullPath(Path.Combine(outputDirectory, relative));
                var prefix = outputDirectory.TrimEnd(
                    Path.DirectorySeparatorChar,
                    Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
                var comparison = PlatformInfo.IsWindows
                    ? StringComparison.OrdinalIgnoreCase
                    : StringComparison.Ordinal;
                if (!target.StartsWith(prefix, comparison))
                {
                    throw new InvalidDataException("受信したファイルの保存先が不正です。");
                }

                var parent = Path.GetDirectoryName(target);
                if (!string.IsNullOrWhiteSpace(parent))
                {
                    Directory.CreateDirectory(parent);
                }
                File.WriteAllBytes(target, entry.Bytes);
            }

            return new OpticalSavedPayload(
                parsed.Label,
                parsed.Files.Count,
                outputDirectory);
        }

        private static byte[] UnpackFile(byte[] container, out string mediaType)
        {
            if (container == null
                || container.Length < FileHeaderLength
                || container[0] != 0x44
                || container[1] != 0x43
                || container[2] != 0x46
                || container[3] != 0x32)
            {
                throw new InvalidDataException("受信したファイルヘッダーが不正です。");
            }

            var compression = container[4];
            var nameLength = ReadUInt16(container, 5);
            var typeLength = ReadUInt16(container, 7);
            var originalLengthRaw = ReadUInt32(container, 9);
            var transmittedLengthRaw = ReadUInt32(container, 13);
            if (compression > 1
                || originalLengthRaw == 0
                || originalLengthRaw > MaximumPayloadBytes
                || transmittedLengthRaw == 0
                || transmittedLengthRaw > MaximumPayloadBytes)
            {
                throw new InvalidDataException("受信したファイル長が不正です。");
            }

            var originalLength = (int)originalLengthRaw;
            var transmittedLength = (int)transmittedLengthRaw;
            var bodyOffset = checked(FileHeaderLength + nameLength + typeLength);
            if (bodyOffset > container.Length
                || container.Length - bodyOffset != transmittedLength)
            {
                throw new InvalidDataException("受信したファイルが途中で切れています。");
            }

            try
            {
                Utf8.GetString(container, FileHeaderLength, nameLength);
                mediaType = Utf8.GetString(
                    container,
                    FileHeaderLength + nameLength,
                    typeLength);
            }
            catch (DecoderFallbackException exception)
            {
                throw new InvalidDataException("受信したファイル情報を読めません。", exception);
            }

            var transmitted = new byte[transmittedLength];
            Buffer.BlockCopy(container, bodyOffset, transmitted, 0, transmitted.Length);
            var bytes = compression == 1
                ? Decompress(transmitted, originalLength)
                : transmitted;
            if (bytes.Length != originalLength)
            {
                throw new InvalidDataException("受信したファイル長がヘッダーと一致しません。");
            }

            var expectedHash = new byte[32];
            Buffer.BlockCopy(container, 17, expectedHash, 0, expectedHash.Length);
            var actualHash = Sha256(bytes);
            if (!EqualBytes(expectedHash, actualHash))
            {
                throw new InvalidDataException("受信したファイルの SHA-256 検算に失敗しました。");
            }
            return bytes;
        }

        private static ParsedOpticalBundle ReadBundle(byte[] bytes)
        {
            if (bytes.Length < BundleHeaderLength
                || bytes[0] != 0x44
                || bytes[1] != 0x43
                || bytes[2] != 0x42
                || bytes[3] != 0x31)
            {
                throw new InvalidDataException("受信したフォルダ情報が不正です。");
            }

            var manifestLengthRaw = ReadUInt32(bytes, 4);
            if (manifestLengthRaw == 0 || manifestLengthRaw > MaximumManifestBytes)
            {
                throw new InvalidDataException("受信したファイル一覧の長さが不正です。");
            }
            var manifestLength = (int)manifestLengthRaw;
            if (BundleHeaderLength + manifestLength > bytes.Length)
            {
                throw new InvalidDataException("受信したファイル一覧が途中で切れています。");
            }

            var manifestBytes = new byte[manifestLength];
            Buffer.BlockCopy(bytes, BundleHeaderLength, manifestBytes, 0, manifestLength);
            Dictionary<string, object> manifest;
            try
            {
                manifest = JsonCodec.ParseObject(manifestBytes);
            }
            catch (ArgumentException exception)
            {
                throw new InvalidDataException("受信したファイル一覧を読めません。", exception);
            }

            if (ReadInteger(manifest, "v", 1) != 1)
            {
                throw new InvalidDataException("この Ferry では扱えない転送形式です。");
            }
            var label = ReadString(manifest, "label");
            if (label.IndexOf('/') >= 0)
            {
                throw new InvalidDataException("受信した転送名が不正です。");
            }
            OpticalPayload.ValidateEntryName(label);

            object filesValue;
            var rawFiles = manifest.TryGetValue("files", out filesValue)
                ? filesValue as List<object>
                : null;
            if (rawFiles == null || rawFiles.Count == 0 || rawFiles.Count > MaximumFiles)
            {
                throw new InvalidDataException("受信したファイル数が不正です。");
            }
            if (ReadInteger(manifest, "count", MaximumFiles) != rawFiles.Count)
            {
                throw new InvalidDataException("受信したファイル数が一覧と一致しません。");
            }
            var declaredTotal = ReadInteger(manifest, "totalSize", MaximumPayloadBytes);

            var records = new List<OpticalBundleRecord>();
            var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            long measuredTotal = 0;
            foreach (var raw in rawFiles)
            {
                var item = raw as Dictionary<string, object>;
                if (item == null)
                {
                    throw new InvalidDataException("受信したファイル一覧が壊れています。");
                }
                var path = ReadString(item, "path");
                OpticalPayload.ValidateEntryName(path);
                if (!paths.Add(path))
                {
                    throw new InvalidDataException("受信した一覧に同じファイル名が重複しています。");
                }
                var size = ReadInteger(item, "size", MaximumPayloadBytes);
                var hash = ReadString(item, "sha256");
                if (!IsSha256(hash))
                {
                    throw new InvalidDataException("受信したファイルのハッシュ値が不正です。");
                }
                measuredTotal = checked(measuredTotal + size);
                if (measuredTotal > MaximumPayloadBytes)
                {
                    throw new InvalidDataException("受信したフォルダが大きすぎます。");
                }
                records.Add(new OpticalBundleRecord(path, size, hash));
            }

            if (measuredTotal != declaredTotal)
            {
                throw new InvalidDataException("受信した合計サイズが一覧と一致しません。");
            }
            ValidatePathSet(paths);

            var bodyOffset = BundleHeaderLength + manifestLength;
            if (bytes.Length - bodyOffset != declaredTotal)
            {
                throw new InvalidDataException("受信したフォルダの長さが一覧と一致しません。");
            }

            var entries = new List<ReceivedOpticalFile>();
            var offset = bodyOffset;
            foreach (var record in records)
            {
                var content = new byte[record.Size];
                Buffer.BlockCopy(bytes, offset, content, 0, content.Length);
                offset += content.Length;
                if (!string.Equals(
                    Hex(Sha256(content)),
                    record.Sha256,
                    StringComparison.Ordinal))
                {
                    throw new InvalidDataException(
                        string.Format("受信したファイルが壊れています: {0}", record.Path));
                }
                entries.Add(new ReceivedOpticalFile(record.Path, content));
            }
            return new ParsedOpticalBundle(label, entries);
        }

        private static byte[] Decompress(byte[] bytes, int expectedLength)
        {
            using (var input = new MemoryStream(bytes, false))
            using (var gzip = new GZipStream(input, CompressionMode.Decompress, false))
            using (var output = new MemoryStream(Math.Min(expectedLength, 1024 * 1024)))
            {
                var buffer = new byte[8192];
                while (true)
                {
                    var read = gzip.Read(buffer, 0, buffer.Length);
                    if (read == 0)
                    {
                        break;
                    }
                    if (output.Length + read > expectedLength)
                    {
                        throw new InvalidDataException("受信した圧縮データが宣言サイズを超えました。");
                    }
                    output.Write(buffer, 0, read);
                }
                return output.ToArray();
            }
        }

        private static int ReadInteger(
            Dictionary<string, object> values,
            string name,
            int maximum)
        {
            object raw;
            if (!values.TryGetValue(name, out raw) || raw == null)
            {
                throw new InvalidDataException("受信した一覧に必要な数値がありません: " + name);
            }
            decimal value;
            try
            {
                value = Convert.ToDecimal(raw, CultureInfo.InvariantCulture);
            }
            catch (Exception exception)
            {
                throw new InvalidDataException("受信した一覧の数値が不正です: " + name, exception);
            }
            if (value < 0 || value > maximum || decimal.Truncate(value) != value)
            {
                throw new InvalidDataException("受信した一覧の数値が不正です: " + name);
            }
            return (int)value;
        }

        private static string ReadString(Dictionary<string, object> values, string name)
        {
            object raw;
            var value = values.TryGetValue(name, out raw) ? raw as string : null;
            if (string.IsNullOrWhiteSpace(value))
            {
                throw new InvalidDataException("受信した一覧に必要な文字列がありません: " + name);
            }
            return value;
        }

        private static void ValidatePathSet(HashSet<string> paths)
        {
            foreach (var path in paths)
            {
                var slash = path.IndexOf('/');
                while (slash >= 0)
                {
                    if (paths.Contains(path.Substring(0, slash)))
                    {
                        throw new InvalidDataException(
                            "受信した一覧で同じ名前がファイルとフォルダに使われています。");
                    }
                    slash = path.IndexOf('/', slash + 1);
                }
            }
        }

        private static bool IsSha256(string value)
        {
            if (value == null || value.Length != 64)
            {
                return false;
            }
            for (var index = 0; index < value.Length; index++)
            {
                var character = value[index];
                if (!((character >= '0' && character <= '9')
                    || (character >= 'a' && character <= 'f')))
                {
                    return false;
                }
            }
            return true;
        }

        private static byte[] Sha256(byte[] bytes)
        {
            using (var hash = SHA256.Create())
            {
                return hash.ComputeHash(bytes);
            }
        }

        private static string Hex(byte[] bytes)
        {
            var builder = new StringBuilder(bytes.Length * 2);
            foreach (var value in bytes)
            {
                builder.Append(value.ToString("x2", CultureInfo.InvariantCulture));
            }
            return builder.ToString();
        }

        private static bool EqualBytes(byte[] left, byte[] right)
        {
            if (left.Length != right.Length)
            {
                return false;
            }
            var difference = 0;
            for (var index = 0; index < left.Length; index++)
            {
                difference |= left[index] ^ right[index];
            }
            return difference == 0;
        }

        private static ushort ReadUInt16(byte[] bytes, int offset)
        {
            return (ushort)(bytes[offset] | (bytes[offset + 1] << 8));
        }

        private static uint ReadUInt32(byte[] bytes, int offset)
        {
            return (uint)(bytes[offset]
                | (bytes[offset + 1] << 8)
                | (bytes[offset + 2] << 16)
                | (bytes[offset + 3] << 24));
        }

        private sealed class OpticalBundleRecord
        {
            public OpticalBundleRecord(string path, int size, string sha256)
            {
                Path = path;
                Size = size;
                Sha256 = sha256;
            }

            public string Path { get; private set; }
            public int Size { get; private set; }
            public string Sha256 { get; private set; }
        }

        private sealed class ParsedOpticalBundle
        {
            public ParsedOpticalBundle(string label, List<ReceivedOpticalFile> files)
            {
                Label = label;
                Files = files;
            }

            public string Label { get; private set; }
            public List<ReceivedOpticalFile> Files { get; private set; }
        }
    }

    internal sealed class ReceivedOpticalFile
    {
        public ReceivedOpticalFile(string path, byte[] bytes)
        {
            Path = path;
            Bytes = bytes;
        }

        public string Path { get; private set; }
        public byte[] Bytes { get; private set; }
    }

    internal sealed class OpticalSavedPayload
    {
        public OpticalSavedPayload(string label, int fileCount, string outputPath)
        {
            Label = label;
            FileCount = fileCount;
            OutputPath = outputPath;
        }

        public string Label { get; private set; }
        public int FileCount { get; private set; }
        public string OutputPath { get; private set; }
    }
}
