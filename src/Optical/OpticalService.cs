using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using ZXing;
using ZXing.Common;
using ZXing.QrCode.Internal;

namespace Ferry
{
    internal sealed class OpticalService
    {
        private const int MinimumFrameBytes = 64;
        private const int MaximumFrameBytes = 2953;
        private const int MaximumFiles = 2000;
        private const int MaximumBundleBytes = 16 * 1024 * 1024;

        private readonly object _gate = new object();
        private OpticalSession _active;

        public OpticalStartResult Start(
            FolderSnapshot source,
            IList<string> selectedNames,
            int frameBytes,
            int framesPerSecond)
        {
            if (frameBytes < MinimumFrameBytes || frameBytes > MaximumFrameBytes)
            {
                throw new ArgumentException("1 枚に載せる量が正しくありません。", "frameBytes");
            }
            if (framesPerSecond != 8 && framesPerSecond != 12 && framesPerSecond != 16)
            {
                throw new ArgumentException("1 秒あたりの枚数が正しくありません。", "framesPerSecond");
            }

            var payload = OpticalPayload.Build(
                source,
                selectedNames,
                MaximumFiles,
                MaximumBundleBytes);
            var session = new OpticalSession(payload, frameBytes, framesPerSecond);
            session.ValidateFirstFrame();

            lock (_gate)
            {
                _active = session;
            }

            return session.Describe();
        }

        public bool TryRenderFrame(
            string token,
            uint sequence,
            out byte[] svg)
        {
            OpticalSession session;
            lock (_gate)
            {
                session = _active;
            }

            if (session == null || !session.Matches(token))
            {
                svg = null;
                return false;
            }

            svg = session.RenderFrame(sequence);
            return true;
        }

        public void Stop(string token)
        {
            lock (_gate)
            {
                if (_active != null && _active.Matches(token))
                {
                    _active = null;
                }
            }
        }

        internal static uint FountainSweepDigest()
        {
            var hash = 0x811c9dc5u;
            for (var k = 1; k <= 65535; k++)
            {
                HashDouble(ref hash, FountainMath.DLog(2.0 * k));
            }
            for (var index = 64; index < 64 * 4096; index++)
            {
                HashDouble(ref hash, FountainMath.DLog(index / 64.0));
            }
            return hash;
        }

        private static void HashDouble(ref uint hash, double value)
        {
            var bytes = BitConverter.GetBytes(value);
            for (var index = 0; index < bytes.Length; index++)
            {
                hash ^= bytes[index];
                hash = unchecked(hash * 0x01000193u);
            }
        }
    }

    internal sealed class OpticalSession
    {
        private const int FrameHeaderLength = 20;
        private const int QuietZone = 4;

        private readonly string _token;
        private readonly ushort _sessionId;
        private readonly int _frameBytes;
        private readonly int _framesPerSecond;
        private readonly byte[] _payload;
        private readonly FountainEncoder _encoder;
        private readonly uint _payloadFnv;
        private int _qrVersion;

        public OpticalSession(
            OpticalPayload payload,
            int frameBytes,
            int framesPerSecond)
        {
            _token = Guid.NewGuid().ToString("N");
            _sessionId = NewSessionId();
            _frameBytes = frameBytes;
            _framesPerSecond = framesPerSecond;
            _payload = payload.Bytes;
            _encoder = new FountainEncoder(
                _payload,
                frameBytes - FrameHeaderLength,
                _sessionId);
            if (_encoder.BlockCount > ushort.MaxValue)
            {
                throw new ArgumentException(
                    "選んだ内容はこの設定では大きすぎます。1 枚に載せる量を増やしてください。");
            }
            _payloadFnv = Fnv1a(_payload);
            Label = payload.Label;
            FileCount = payload.FileCount;
            OriginalBytes = payload.OriginalBytes;
        }

        public string Label { get; private set; }
        public int FileCount { get; private set; }
        public long OriginalBytes { get; private set; }

        public bool Matches(string token)
        {
            return !string.IsNullOrEmpty(token)
                && string.Equals(token, _token, StringComparison.Ordinal);
        }

        public void ValidateFirstFrame()
        {
            byte[] frame;
            QRCode qr;
            Encode(0, out frame, out qr);
            ValidateQrRoundTrip(frame, qr);
            _qrVersion = qr.Version.VersionNumber;
        }

        public OpticalStartResult Describe()
        {
            return new OpticalStartResult(
                _token,
                Label,
                FileCount,
                OriginalBytes,
                _payload.LongLength,
                _frameBytes,
                _framesPerSecond,
                _encoder.BlockCount,
                _qrVersion,
                (double)_encoder.BlockCount / _framesPerSecond);
        }

        public byte[] RenderFrame(uint sequence)
        {
            byte[] frame;
            QRCode qr;
            Encode(sequence, out frame, out qr);
            return RenderSvg(qr.Matrix, QuietZone);
        }

        private void Encode(uint sequence, out byte[] frame, out QRCode qr)
        {
            var block = _encoder.Encode(sequence);
            frame = new byte[FrameHeaderLength + block.Length];
            frame[0] = 0xd1;
            frame[1] = 0x0c;
            WriteUInt16(frame, 2, _sessionId);
            WriteUInt32(frame, 4, sequence);
            WriteUInt16(frame, 8, checked((ushort)_encoder.BlockCount));
            WriteUInt16(frame, 10, checked((ushort)_encoder.BlockLength));
            WriteUInt32(frame, 12, checked((uint)_payload.Length));
            WriteUInt32(frame, 16, _payloadFnv);
            Buffer.BlockCopy(block, 0, frame, FrameHeaderLength, block.Length);
            qr = CreateQr(frame);
        }

        private static QRCode CreateQr(byte[] frame)
        {
            var hints = new Dictionary<EncodeHintType, object>();
            hints[EncodeHintType.CHARACTER_SET] = "ISO-8859-1";
            hints[EncodeHintType.DISABLE_ECI] = true;
            hints[EncodeHintType.QR_MASK_PATTERN] = 4;
            var content = Encoding.GetEncoding(28591).GetString(frame);
            try
            {
                return ZXing.QrCode.Internal.Encoder.encode(
                    content,
                    ErrorCorrectionLevel.L,
                    hints);
            }
            catch (WriterException exception)
            {
                throw new ArgumentException(
                    "この設定では QR コードに収まりません。1 枚に載せる量を減らしてください。",
                    exception);
            }
        }

        private static void ValidateQrRoundTrip(byte[] expected, QRCode qr)
        {
            var source = qr.Matrix;
            var bits = new BitMatrix(source.Width, source.Height);
            for (var y = 0; y < source.Height; y++)
            {
                for (var x = 0; x < source.Width; x++)
                {
                    if (source[x, y] != 0)
                    {
                        bits[x, y] = true;
                    }
                }
            }

            var decoded = new ZXing.QrCode.Internal.Decoder().decode(bits, null);
            var segments = decoded == null ? null : decoded.ByteSegments;
            if (segments == null || segments.Count == 0)
            {
                throw new InvalidOperationException("作った QR コードを検算できませんでした。");
            }

            var actualLength = 0;
            foreach (var segment in segments)
            {
                actualLength = checked(actualLength + segment.Length);
            }
            if (actualLength != expected.Length)
            {
                throw new InvalidOperationException("QR コードの検算で長さが一致しませんでした。");
            }

            var offset = 0;
            foreach (var segment in segments)
            {
                for (var index = 0; index < segment.Length; index++)
                {
                    if (segment[index] != expected[offset++])
                    {
                        throw new InvalidOperationException("QR コードの検算で内容が一致しませんでした。");
                    }
                }
            }
        }

        private static byte[] RenderSvg(ByteMatrix matrix, int quietZone)
        {
            var side = matrix.Width + quietZone * 2;
            var builder = new StringBuilder(side * side * 3);
            builder.Append("<svg xmlns=\"http://www.w3.org/2000/svg\" viewBox=\"0 0 ");
            builder.Append(side.ToString(CultureInfo.InvariantCulture));
            builder.Append(' ');
            builder.Append(side.ToString(CultureInfo.InvariantCulture));
            builder.Append("\" shape-rendering=\"crispEdges\" role=\"img\" aria-label=\"光学転送 QR\">");
            builder.Append("<rect width=\"100%\" height=\"100%\" fill=\"#fff\"/><path fill=\"#000\" d=\"");

            for (var y = 0; y < matrix.Height; y++)
            {
                var x = 0;
                while (x < matrix.Width)
                {
                    while (x < matrix.Width && matrix[x, y] == 0)
                    {
                        x++;
                    }
                    if (x >= matrix.Width)
                    {
                        break;
                    }

                    var start = x;
                    while (x < matrix.Width && matrix[x, y] != 0)
                    {
                        x++;
                    }
                    var length = x - start;
                    builder.Append('M');
                    builder.Append((start + quietZone).ToString(CultureInfo.InvariantCulture));
                    builder.Append(' ');
                    builder.Append((y + quietZone).ToString(CultureInfo.InvariantCulture));
                    builder.Append('h');
                    builder.Append(length.ToString(CultureInfo.InvariantCulture));
                    builder.Append("v1h-");
                    builder.Append(length.ToString(CultureInfo.InvariantCulture));
                    builder.Append('z');
                }
            }

            builder.Append("\"/></svg>");
            return new UTF8Encoding(false).GetBytes(builder.ToString());
        }

        private static ushort NewSessionId()
        {
            var bytes = new byte[2];
            using (var random = RandomNumberGenerator.Create())
            {
                do
                {
                    random.GetBytes(bytes);
                }
                while (bytes[0] == 0 && bytes[1] == 0);
            }
            return (ushort)(bytes[0] | (bytes[1] << 8));
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

        private static void WriteUInt16(byte[] target, int offset, ushort value)
        {
            target[offset] = (byte)value;
            target[offset + 1] = (byte)(value >> 8);
        }

        private static void WriteUInt32(byte[] target, int offset, uint value)
        {
            target[offset] = (byte)value;
            target[offset + 1] = (byte)(value >> 8);
            target[offset + 2] = (byte)(value >> 16);
            target[offset + 3] = (byte)(value >> 24);
        }
    }

    internal sealed class FountainEncoder
    {
        private readonly int _words;
        private readonly uint[] _blocks;
        private readonly double[] _cdf;
        private readonly ushort _sessionId;

        public FountainEncoder(byte[] payload, int blockLength, ushort sessionId)
        {
            if (payload == null || payload.Length == 0)
            {
                throw new ArgumentException("送る内容が空です。", "payload");
            }
            if (blockLength <= 0)
            {
                throw new ArgumentOutOfRangeException("blockLength");
            }

            BlockLength = blockLength;
            BlockCount = Math.Max(1, (payload.Length + blockLength - 1) / blockLength);
            _words = (blockLength + 3) / 4;
            _blocks = new uint[checked(BlockCount * _words)];
            _cdf = FountainMath.SolitonCdf(BlockCount);
            _sessionId = sessionId;

            for (var index = 0; index < payload.Length; index++)
            {
                var block = index / blockLength;
                var within = index % blockLength;
                var word = within / 4;
                var shift = (within % 4) * 8;
                _blocks[block * _words + word] |= (uint)payload[index] << shift;
            }
        }

        public int BlockLength { get; private set; }
        public int BlockCount { get; private set; }

        public byte[] Encode(uint sequence)
        {
            var indices = FountainMath.FrameIndices(
                BlockCount,
                _cdf,
                _sessionId,
                sequence);
            var words = new uint[_words];
            foreach (var block in indices)
            {
                var offset = block * _words;
                for (var word = 0; word < _words; word++)
                {
                    words[word] ^= _blocks[offset + word];
                }
            }

            var result = new byte[BlockLength];
            for (var index = 0; index < result.Length; index++)
            {
                var word = words[index / 4];
                result[index] = (byte)(word >> ((index % 4) * 8));
            }
            return result;
        }
    }

    internal static class FountainMath
    {
        private const double Ln2 = 0.6931471805599453;
        private const double SolitonC = 0.1;
        private const double SolitonDelta = 0.5;

        public static double DLog(double value)
        {
            var exponent = 0;
            var mantissa = value;
            while (mantissa >= 1.5)
            {
                mantissa /= 2.0;
                exponent++;
            }
            while (mantissa < 0.75)
            {
                mantissa *= 2.0;
                exponent--;
            }

            var z = (mantissa - 1.0) / (mantissa + 1.0);
            var z2 = z * z;
            var term = z;
            var sum = 0.0;
            for (var number = 1; number <= 21; number += 2)
            {
                sum += term / number;
                term *= z2;
            }
            return exponent * Ln2 + 2.0 * sum;
        }

        public static double[] SolitonCdf(int blockCount)
        {
            var cdf = new double[blockCount];
            if (blockCount == 1)
            {
                cdf[0] = 1.0;
                return cdf;
            }

            var r = Math.Max(
                1.0,
                SolitonC * DLog(blockCount / SolitonDelta) * Math.Sqrt(blockCount));
            var spike = Math.Min(blockCount, (int)Math.Ceiling(blockCount / r));
            var total = 0.0;
            for (var degree = 1; degree <= blockCount; degree++)
            {
                var rho = degree == 1
                    ? 1.0 / blockCount
                    : 1.0 / (degree * (double)(degree - 1));
                var tau = 0.0;
                if (degree < spike)
                {
                    tau = r / (degree * blockCount);
                }
                else if (degree == spike)
                {
                    tau = r * Math.Max(0.0, DLog(r / SolitonDelta)) / blockCount;
                }
                total += rho + tau;
                cdf[degree - 1] = total;
            }
            for (var index = 0; index < blockCount; index++)
            {
                cdf[index] /= total;
            }
            cdf[blockCount - 1] = 1.0;
            return cdf;
        }

        public static List<int> FrameIndices(
            int blockCount,
            double[] cdf,
            ushort sessionId,
            uint sequence)
        {
            var random = new SplitMix32(FrameSeed(sessionId, sequence));
            var sample = random.Next() * (1.0 / 4294967296.0);
            var low = 0;
            var high = blockCount - 1;
            while (low < high)
            {
                var middle = (low + high) >> 1;
                if (cdf[middle] >= sample)
                {
                    high = middle;
                }
                else
                {
                    low = middle + 1;
                }
            }
            var degree = Math.Min(blockCount, low + 1);

            if (degree > (blockCount >> 3))
            {
                var scratch = new int[blockCount];
                for (var index = 0; index < blockCount; index++)
                {
                    scratch[index] = index;
                }
                var result = new List<int>(degree);
                for (var index = 0; index < degree; index++)
                {
                    var pick = index + (int)(random.Next() % (uint)(blockCount - index));
                    var value = scratch[index];
                    scratch[index] = scratch[pick];
                    scratch[pick] = value;
                    result.Add(scratch[index]);
                }
                return result;
            }

            var seen = new HashSet<int>();
            var ordered = new List<int>(degree);
            while (ordered.Count < degree)
            {
                var value = (int)(random.Next() % (uint)blockCount);
                if (seen.Add(value))
                {
                    ordered.Add(value);
                }
            }
            return ordered;
        }

        private static uint FrameSeed(ushort sessionId, uint sequence)
        {
            var hash = unchecked(((uint)sessionId + 1u) * 0x9e3779b1u)
                ^ unchecked(sequence + 0x85ebca6bu);
            hash = unchecked((hash ^ (hash >> 13)) * 0xc2b2ae35u);
            return hash ^ (hash >> 16);
        }

        private sealed class SplitMix32
        {
            private uint _state;

            public SplitMix32(uint seed)
            {
                _state = seed;
            }

            public uint Next()
            {
                _state = unchecked(_state + 0x9e3779b9u);
                var value = _state ^ (_state >> 16);
                value = unchecked(value * 0x21f0aaadu);
                value ^= value >> 15;
                value = unchecked(value * 0x735a2d97u);
                value ^= value >> 15;
                return value;
            }
        }
    }

    internal sealed class OpticalPayload
    {
        private const int BundleHeaderLength = 8;
        private const int FileHeaderLength = 49;
        private static readonly byte[] BundleMagic = { 0x44, 0x43, 0x42, 0x31 };
        private static readonly byte[] FileMagic = { 0x44, 0x43, 0x46, 0x32 };
        private const string BundleMediaType = "application/vnd.decimen.bundle+dcb1";

        private OpticalPayload(
            string label,
            int fileCount,
            long originalBytes,
            byte[] bytes)
        {
            Label = label;
            FileCount = fileCount;
            OriginalBytes = originalBytes;
            Bytes = bytes;
        }

        public string Label { get; private set; }
        public int FileCount { get; private set; }
        public long OriginalBytes { get; private set; }
        public byte[] Bytes { get; private set; }

        public static OpticalPayload Build(
            FolderSnapshot source,
            IList<string> selectedNames,
            int maximumFiles,
            int maximumBytes)
        {
            if (source == null)
            {
                throw new ArgumentNullException("source");
            }
            if (selectedNames == null || selectedNames.Count == 0)
            {
                throw new ArgumentException("送るファイルを1つ以上選んでください。", "selectedNames");
            }

            var selected = Resolve(source, selectedNames);
            if (selected.Count > maximumFiles)
            {
                throw new ArgumentException(string.Format(
                    "一度に送れるのは {0:N0} ファイルまでです。",
                    maximumFiles));
            }

            long total = 0;
            var manifestFiles = new List<object>();
            var contents = new List<byte[]>();
            foreach (var file in selected)
            {
                var bytes = File.ReadAllBytes(file.FullPath);
                total = checked(total + bytes.LongLength);
                if (total > maximumBytes)
                {
                    throw new ArgumentException(string.Format(
                        "選んだ内容は合計 {0:N1} MB です。現在は 16 MB まで送れます。",
                        total / 1024.0 / 1024.0));
                }
                contents.Add(bytes);
                manifestFiles.Add(new Dictionary<string, object>
                {
                    { "path", ValidateEntryName(file.Name) },
                    { "size", bytes.LongLength },
                    { "sha256", Sha256Hex(bytes) }
                });
            }

            var label = SafeLabel(Path.GetFileName(
                source.DirectoryPath.TrimEnd(
                    Path.DirectorySeparatorChar,
                    Path.AltDirectorySeparatorChar)));
            var manifest = new Dictionary<string, object>
            {
                { "v", 1 },
                { "label", label },
                { "count", selected.Count },
                { "totalSize", total },
                { "files", manifestFiles }
            };
            var manifestBytes = JsonCodec.Serialize(manifest);
            if (manifestBytes.Length > 1024 * 1024)
            {
                throw new ArgumentException("ファイル名の一覧が大きすぎます。");
            }

            var bundleLength = checked(BundleHeaderLength + manifestBytes.Length + (int)total);
            var bundle = new byte[bundleLength];
            Buffer.BlockCopy(BundleMagic, 0, bundle, 0, BundleMagic.Length);
            WriteUInt32(bundle, 4, checked((uint)manifestBytes.Length));
            Buffer.BlockCopy(manifestBytes, 0, bundle, BundleHeaderLength, manifestBytes.Length);
            var offset = BundleHeaderLength + manifestBytes.Length;
            foreach (var bytes in contents)
            {
                Buffer.BlockCopy(bytes, 0, bundle, offset, bytes.Length);
                offset += bytes.Length;
            }

            var fileContainer = PackFile(label + ".dcb1", BundleMediaType, bundle);
            return new OpticalPayload(label, selected.Count, total, fileContainer);
        }

        private static List<FolderFile> Resolve(
            FolderSnapshot source,
            IList<string> names)
        {
            var byName = new Dictionary<string, FolderFile>(StringComparer.CurrentCultureIgnoreCase);
            foreach (var file in source.Files)
            {
                byName[file.Name] = file;
            }

            var result = new List<FolderFile>();
            var seen = new HashSet<string>(StringComparer.CurrentCultureIgnoreCase);
            foreach (var name in names)
            {
                FolderFile file;
                if (string.IsNullOrWhiteSpace(name) || !byName.TryGetValue(name, out file))
                {
                    throw new FileNotFoundException(
                        string.Format("選んだファイルが見つかりません: {0}", name));
                }
                if (seen.Add(file.Name))
                {
                    result.Add(file);
                }
            }
            return result;
        }

        private static byte[] PackFile(string name, string mediaType, byte[] bytes)
        {
            var transmitted = CompressIfUseful(bytes);
            var compressed = !object.ReferenceEquals(transmitted, bytes);
            var nameBytes = Encoding.UTF8.GetBytes(name);
            var typeBytes = Encoding.UTF8.GetBytes(mediaType);
            var result = new byte[checked(
                FileHeaderLength + nameBytes.Length + typeBytes.Length + transmitted.Length)];
            Buffer.BlockCopy(FileMagic, 0, result, 0, FileMagic.Length);
            result[4] = compressed ? (byte)1 : (byte)0;
            WriteUInt16(result, 5, checked((ushort)nameBytes.Length));
            WriteUInt16(result, 7, checked((ushort)typeBytes.Length));
            WriteUInt32(result, 9, checked((uint)bytes.Length));
            WriteUInt32(result, 13, checked((uint)transmitted.Length));
            var digest = Sha256(bytes);
            Buffer.BlockCopy(digest, 0, result, 17, digest.Length);
            Buffer.BlockCopy(nameBytes, 0, result, FileHeaderLength, nameBytes.Length);
            Buffer.BlockCopy(
                typeBytes,
                0,
                result,
                FileHeaderLength + nameBytes.Length,
                typeBytes.Length);
            Buffer.BlockCopy(
                transmitted,
                0,
                result,
                FileHeaderLength + nameBytes.Length + typeBytes.Length,
                transmitted.Length);
            return result;
        }

        private static byte[] CompressIfUseful(byte[] bytes)
        {
            if (bytes.Length < 768)
            {
                return bytes;
            }
            using (var buffer = new MemoryStream())
            {
                using (var gzip = new GZipStream(buffer, CompressionLevel.Optimal, true))
                {
                    gzip.Write(bytes, 0, bytes.Length);
                }
                var compressed = buffer.ToArray();
                return compressed.Length + 64 < bytes.Length ? compressed : bytes;
            }
        }

        private static byte[] Sha256(byte[] bytes)
        {
            using (var hash = SHA256.Create())
            {
                return hash.ComputeHash(bytes);
            }
        }

        private static string Sha256Hex(byte[] bytes)
        {
            var digest = Sha256(bytes);
            var text = new StringBuilder(digest.Length * 2);
            foreach (var value in digest)
            {
                text.Append(value.ToString("x2", CultureInfo.InvariantCulture));
            }
            return text.ToString();
        }

        private static string ValidateEntryName(string name)
        {
            if (string.IsNullOrWhiteSpace(name)
                || name.Length > 200
                || name.StartsWith("/", StringComparison.Ordinal)
                || name.IndexOf('\\') >= 0
                || (name.Length >= 2 && char.IsLetter(name[0]) && name[1] == ':'))
            {
                throw new ArgumentException(
                    string.Format("光学転送で扱えないファイル名です: {0}", name));
            }

            string normalized;
            try
            {
                normalized = name.Normalize(NormalizationForm.FormC);
            }
            catch (ArgumentException)
            {
                normalized = null;
            }
            if (!string.Equals(name, normalized, StringComparison.Ordinal))
            {
                throw new ArgumentException(
                    string.Format("光学転送で扱えないファイル名です: {0}", name));
            }

            var segments = name.Split('/');
            if (segments.Length > 32)
            {
                throw new ArgumentException(
                    string.Format("光学転送で扱えないファイル名です: {0}", name));
            }
            foreach (var segment in segments)
            {
                if (!IsSafePathSegment(segment))
                {
                    throw new ArgumentException(
                        string.Format("光学転送で扱えないファイル名です: {0}", name));
                }
            }
            return name;
        }

        private static bool IsSafePathSegment(string segment)
        {
            if (string.IsNullOrEmpty(segment)
                || segment.Length > 180
                || segment == "."
                || segment == ".."
                || segment[0] == ' '
                || segment[segment.Length - 1] == '.'
                || segment[segment.Length - 1] == ' ')
            {
                return false;
            }

            for (var index = 0; index < segment.Length; index++)
            {
                var character = segment[index];
                if (character < 0x20
                    || character == 0x7f
                    || character == '<'
                    || character == '>'
                    || character == ':'
                    || character == '"'
                    || character == '|'
                    || character == '?'
                    || character == '*'
                    || character == '\\'
                    || character == '\u00ad'
                    || (character >= '\u200b' && character <= '\u200f')
                    || (character >= '\u202a' && character <= '\u202e')
                    || character == '\u2028'
                    || character == '\u2029'
                    || (character >= '\u2066' && character <= '\u2069')
                    || character == '\ufeff')
                {
                    return false;
                }
                if (char.IsHighSurrogate(character))
                {
                    if (index + 1 >= segment.Length || !char.IsLowSurrogate(segment[index + 1]))
                    {
                        return false;
                    }
                    index++;
                }
                else if (char.IsLowSurrogate(character))
                {
                    return false;
                }
            }

            var dot = segment.IndexOf('.');
            var baseName = (dot < 0 ? segment : segment.Substring(0, dot)).ToUpperInvariant();
            if (baseName == "CON" || baseName == "PRN" || baseName == "AUX" || baseName == "NUL")
            {
                return false;
            }
            if ((baseName.StartsWith("COM", StringComparison.Ordinal)
                    || baseName.StartsWith("LPT", StringComparison.Ordinal))
                && baseName.Length == 4
                && ((baseName[3] >= '0' && baseName[3] <= '9')
                    || baseName[3] == '\u00b9'
                    || baseName[3] == '\u00b2'
                    || baseName[3] == '\u00b3'))
            {
                return false;
            }
            return true;
        }

        private static string SafeLabel(string value)
        {
            var invalid = new HashSet<char>(Path.GetInvalidFileNameChars());
            invalid.Add('/');
            invalid.Add('\\');
            invalid.Add(':');
            invalid.Add('*');
            invalid.Add('?');
            invalid.Add('"');
            invalid.Add('<');
            invalid.Add('>');
            invalid.Add('|');
            var builder = new StringBuilder();
            foreach (var character in value ?? string.Empty)
            {
                if (!invalid.Contains(character) && !char.IsControl(character))
                {
                    builder.Append(character);
                }
            }
            var label = builder.ToString().Trim().TrimEnd('.');
            return string.IsNullOrWhiteSpace(label) ? "Ferry" : label;
        }

        private static void WriteUInt16(byte[] target, int offset, ushort value)
        {
            target[offset] = (byte)value;
            target[offset + 1] = (byte)(value >> 8);
        }

        private static void WriteUInt32(byte[] target, int offset, uint value)
        {
            target[offset] = (byte)value;
            target[offset + 1] = (byte)(value >> 8);
            target[offset + 2] = (byte)(value >> 16);
            target[offset + 3] = (byte)(value >> 24);
        }
    }

    internal sealed class OpticalStartResult
    {
        public OpticalStartResult(
            string token,
            string label,
            int fileCount,
            long originalBytes,
            long transmittedBytes,
            int frameBytes,
            int framesPerSecond,
            int sourceBlocks,
            int qrVersion,
            double minimumSeconds)
        {
            Token = token;
            Label = label;
            FileCount = fileCount;
            OriginalBytes = originalBytes;
            TransmittedBytes = transmittedBytes;
            FrameBytes = frameBytes;
            FramesPerSecond = framesPerSecond;
            SourceBlocks = sourceBlocks;
            QrVersion = qrVersion;
            MinimumSeconds = minimumSeconds;
        }

        public string Token { get; private set; }
        public string Label { get; private set; }
        public int FileCount { get; private set; }
        public long OriginalBytes { get; private set; }
        public long TransmittedBytes { get; private set; }
        public int FrameBytes { get; private set; }
        public int FramesPerSecond { get; private set; }
        public int SourceBlocks { get; private set; }
        public int QrVersion { get; private set; }
        public double MinimumSeconds { get; private set; }
    }
}
