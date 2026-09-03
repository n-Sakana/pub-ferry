using System;
using System.Collections.Generic;
using System.IO;

namespace MacroStudio
{
    public static class VbaCompression
    {
        public static byte[] Decompress(byte[] data)
        {
            return Decompress(data, 0);
        }

        public static byte[] Decompress(byte[] data, int offset)
        {
            if (data == null)
            {
                throw new ArgumentNullException("data");
            }
            if (offset < 0 || offset >= data.Length)
            {
                throw new InvalidDataException(
                    "VBA compressed container offset is outside the stream.");
            }
            if (data[offset] != 0x01)
            {
                throw new InvalidDataException(
                    "Invalid VBA compressed container signature.");
            }

            List<byte> output = new List<byte>();
            int position = offset + 1;
            while (position < data.Length)
            {
                if (position + 2 > data.Length)
                {
                    throw new InvalidDataException(
                        "Truncated VBA compressed chunk header.");
                }

                int chunkStart = position;
                ushort header = BitConverter.ToUInt16(data, position);
                position += 2;

                int chunkSize = (header & 0x0FFF) + 3;
                int chunkSignature = (header >> 12) & 0x0007;
                bool isCompressed = (header & 0x8000) != 0;
                long chunkEndLong = (long)chunkStart + (long)chunkSize;
                if (chunkSignature != 3)
                {
                    throw new InvalidDataException(
                        "Invalid VBA compressed chunk signature.");
                }
                if (chunkEndLong > data.Length)
                {
                    throw new InvalidDataException(
                        "VBA compressed chunk extends past the stream.");
                }

                int chunkEnd = (int)chunkEndLong;
                int decompressedChunkStart = output.Count;
                if (!isCompressed)
                {
                    if (chunkSize != 4098)
                    {
                        throw new InvalidDataException(
                            "Invalid raw VBA chunk size.");
                    }

                    int index;
                    for (index = 0; index < 4096; index++)
                    {
                        output.Add(data[position + index]);
                    }
                    position = chunkEnd;
                    continue;
                }

                DecompressChunk(
                    data,
                    ref position,
                    chunkEnd,
                    decompressedChunkStart,
                    output);
                if (position != chunkEnd)
                {
                    throw new InvalidDataException(
                        "VBA compressed chunk was not consumed exactly.");
                }
            }

            return output.ToArray();
        }

        public static byte[] DecompressBestEffort(
            byte[] data,
            out bool hadWarnings)
        {
            return DecompressBestEffort(data, 0, out hadWarnings);
        }

        public static byte[] DecompressBestEffort(
            byte[] data,
            int offset,
            out bool hadWarnings)
        {
            if (data == null)
            {
                throw new ArgumentNullException("data");
            }

            hadWarnings = false;
            if (offset < 0 ||
                offset >= data.Length ||
                data[offset] != 0x01)
            {
                hadWarnings = true;
                return new byte[0];
            }

            List<byte> output = new List<byte>();
            int position = offset + 1;
            while (position < data.Length)
            {
                if (position + 2 > data.Length)
                {
                    hadWarnings = true;
                    break;
                }

                int chunkStart = position;
                ushort header = BitConverter.ToUInt16(data, position);
                position += 2;

                int chunkSize = (header & 0x0FFF) + 3;
                int chunkSignature = (header >> 12) & 0x0007;
                bool isCompressed = (header & 0x8000) != 0;
                long declaredEnd =
                    (long)chunkStart + (long)chunkSize;
                int chunkEnd;
                if (declaredEnd > data.Length)
                {
                    hadWarnings = true;
                    chunkEnd = data.Length;
                }
                else
                {
                    chunkEnd = (int)declaredEnd;
                }
                if (chunkSignature != 3)
                {
                    hadWarnings = true;
                }

                int decompressedChunkStart = output.Count;
                if (!isCompressed)
                {
                    int rawLength = Math.Min(
                        4096,
                        Math.Max(0, chunkEnd - position));
                    if (chunkSize != 4098 || rawLength != 4096)
                    {
                        hadWarnings = true;
                    }

                    int rawIndex;
                    for (rawIndex = 0;
                        rawIndex < rawLength;
                        rawIndex++)
                    {
                        output.Add(data[position + rawIndex]);
                    }
                }
                else
                {
                    DecompressChunkBestEffort(
                        data,
                        ref position,
                        chunkEnd,
                        decompressedChunkStart,
                        output,
                        ref hadWarnings);
                }

                position = chunkEnd;
            }

            return output.ToArray();
        }

        public static byte[] DecompressUntilInvalid(
            byte[] data,
            int offset,
            int maximumLength,
            out int endOffset)
        {
            if (data == null)
            {
                throw new ArgumentNullException("data");
            }

            endOffset = offset;
            if (offset < 0 ||
                offset >= data.Length ||
                data[offset] != 0x01)
            {
                return new byte[0];
            }

            // Raw container scanning has no stream boundary to stop at,
            // so every chunk must parse exactly. The first chunk that
            // does not ends the container instead of appending the bytes
            // that follow it in the file.
            List<byte> output = new List<byte>();
            int position = offset + 1;
            endOffset = position;
            while (position + 2 <= data.Length)
            {
                int chunkStart = position;
                ushort header = BitConverter.ToUInt16(data, position);
                int chunkSize = (header & 0x0FFF) + 3;
                int chunkSignature = (header >> 12) & 0x0007;
                bool isCompressed = (header & 0x8000) != 0;
                long chunkEndLong = (long)chunkStart + (long)chunkSize;
                if (chunkSignature != 3 || chunkEndLong > data.Length)
                {
                    break;
                }

                int chunkEnd = (int)chunkEndLong;
                int chunkPosition = chunkStart + 2;
                int decompressedChunkStart = output.Count;
                if (!isCompressed)
                {
                    if (chunkSize != 4098)
                    {
                        break;
                    }

                    int index;
                    for (index = 0; index < 4096; index++)
                    {
                        output.Add(data[chunkPosition + index]);
                    }
                    chunkPosition = chunkEnd;
                }
                else
                {
                    try
                    {
                        DecompressChunk(
                            data,
                            ref chunkPosition,
                            chunkEnd,
                            decompressedChunkStart,
                            output);
                    }
                    catch (InvalidDataException)
                    {
                        output.RemoveRange(
                            decompressedChunkStart,
                            output.Count - decompressedChunkStart);
                        break;
                    }
                    if (chunkPosition != chunkEnd)
                    {
                        output.RemoveRange(
                            decompressedChunkStart,
                            output.Count - decompressedChunkStart);
                        break;
                    }
                }

                position = chunkEnd;
                endOffset = chunkEnd;
                if (maximumLength > 0 && output.Count >= maximumLength)
                {
                    break;
                }
            }

            return output.ToArray();
        }

        public static byte[] Compress(byte[] data)
        {
            if (data == null)
            {
                throw new ArgumentNullException("data");
            }

            using (MemoryStream output = new MemoryStream())
            {
                output.WriteByte(0x01);
                int chunkStart = 0;
                while (chunkStart < data.Length)
                {
                    int chunkLength = Math.Min(
                        4096,
                        data.Length - chunkStart);
                    byte[] compressed = CompressChunk(
                        data,
                        chunkStart,
                        chunkLength);

                    if (compressed.Length > 4096 &&
                        chunkLength < 4096)
                    {
                        // 3640 literals plus 455 flag bytes fit in 4096.
                        chunkLength = Math.Min(chunkLength, 3640);
                        compressed = CompressChunk(
                            data,
                            chunkStart,
                            chunkLength);
                    }

                    if (compressed.Length > 4096)
                    {
                        WriteUInt16(output, 0x3FFF);
                        output.Write(data, chunkStart, chunkLength);
                    }
                    else
                    {
                        int chunkSize = compressed.Length + 2;
                        ushort header = (ushort)(
                            0xB000 | (chunkSize - 3));
                        WriteUInt16(output, header);
                        output.Write(
                            compressed,
                            0,
                            compressed.Length);
                    }

                    chunkStart += chunkLength;
                }

                return output.ToArray();
            }
        }

        private static byte[] CompressChunk(
            byte[] data,
            int chunkStart,
            int chunkLength)
        {
            int chunkEnd = chunkStart + chunkLength;
            using (MemoryStream output = new MemoryStream())
            {
                int position = chunkStart;
                while (position < chunkEnd)
                {
                    long flagPosition = output.Position;
                    output.WriteByte(0);
                    byte flags = 0;

                    int bit;
                    for (bit = 0;
                        bit < 8 && position < chunkEnd;
                        bit++)
                    {
                        int matchLength;
                        int matchOffset;
                        FindMatch(
                            data,
                            chunkStart,
                            chunkEnd,
                            position,
                            out matchLength,
                            out matchOffset);

                        if (matchLength >= 3)
                        {
                            int bitCount = GetOffsetBitCount(
                                position - chunkStart);
                            int token =
                                ((matchOffset - 1) <<
                                    (16 - bitCount)) |
                                (matchLength - 3);
                            flags |= (byte)(1 << bit);
                            WriteUInt16(output, (ushort)token);
                            position += matchLength;
                        }
                        else
                        {
                            output.WriteByte(data[position]);
                            position++;
                        }
                    }

                    long endPosition = output.Position;
                    output.Position = flagPosition;
                    output.WriteByte(flags);
                    output.Position = endPosition;
                }

                return output.ToArray();
            }
        }

        private static void FindMatch(
            byte[] data,
            int chunkStart,
            int chunkEnd,
            int position,
            out int bestLength,
            out int bestOffset)
        {
            bestLength = 0;
            bestOffset = 0;
            int decompressedPosition = position - chunkStart;
            if (decompressedPosition == 0)
            {
                return;
            }

            int bitCount = GetOffsetBitCount(decompressedPosition);
            int maximumOffset = Math.Min(
                1 << bitCount,
                decompressedPosition);
            int maximumLength = Math.Min(
                (0xFFFF >> bitCount) + 3,
                chunkEnd - position);

            int offset;
            for (offset = 1; offset <= maximumOffset; offset++)
            {
                if (data[position - offset] != data[position])
                {
                    continue;
                }

                int length = 1;
                while (length < maximumLength &&
                    data[position - offset + (length % offset)] ==
                        data[position + length])
                {
                    length++;
                }

                if (length >= 3 && length > bestLength)
                {
                    bestLength = length;
                    bestOffset = offset;
                    if (bestLength == maximumLength)
                    {
                        break;
                    }
                }
            }
        }

        private static int GetOffsetBitCount(int decompressedPosition)
        {
            int bitCount = 4;
            while ((1 << bitCount) < decompressedPosition &&
                bitCount < 12)
            {
                bitCount++;
            }

            return bitCount;
        }

        private static void WriteUInt16(
            Stream output,
            ushort value)
        {
            output.WriteByte((byte)(value & 0xFF));
            output.WriteByte((byte)(value >> 8));
        }

        private static void DecompressChunk(
            byte[] data,
            ref int position,
            int chunkEnd,
            int decompressedChunkStart,
            List<byte> output)
        {
            while (position < chunkEnd)
            {
                byte flagByte = data[position];
                position++;

                int bit;
                for (bit = 0; bit < 8 && position < chunkEnd; bit++)
                {
                    bool isCopyToken = (flagByte & (1 << bit)) != 0;
                    if (!isCopyToken)
                    {
                        EnsureChunkCapacity(
                            output.Count - decompressedChunkStart,
                            1);
                        output.Add(data[position]);
                        position++;
                    }
                    else
                    {
                        if (position + 2 > chunkEnd)
                        {
                            throw new InvalidDataException(
                                "Truncated VBA copy token.");
                        }

                        ushort token = BitConverter.ToUInt16(data, position);
                        position += 2;
                        CopyToken(
                            token,
                            decompressedChunkStart,
                            output);
                    }
                }
            }
        }

        private static void DecompressChunkBestEffort(
            byte[] data,
            ref int position,
            int chunkEnd,
            int decompressedChunkStart,
            List<byte> output,
            ref bool hadWarnings)
        {
            while (position < chunkEnd &&
                output.Count - decompressedChunkStart < 4096)
            {
                byte flagByte = data[position];
                position++;

                int bit;
                for (bit = 0; bit < 8 && position < chunkEnd; bit++)
                {
                    int decompressedPosition =
                        output.Count - decompressedChunkStart;
                    if (decompressedPosition >= 4096)
                    {
                        hadWarnings = true;
                        return;
                    }

                    bool isCopyToken =
                        (flagByte & (1 << bit)) != 0;
                    if (!isCopyToken)
                    {
                        output.Add(data[position]);
                        position++;
                        continue;
                    }
                    if (position + 2 > chunkEnd)
                    {
                        hadWarnings = true;
                        position = chunkEnd;
                        break;
                    }

                    ushort token = BitConverter.ToUInt16(
                        data,
                        position);
                    position += 2;
                    decompressedPosition =
                        output.Count - decompressedChunkStart;
                    if (decompressedPosition <= 0)
                    {
                        hadWarnings = true;
                        continue;
                    }

                    int bitCount = GetOffsetBitCount(
                        decompressedPosition);
                    int lengthMask = 0xFFFF >> bitCount;
                    int copyLength = (token & lengthMask) + 3;
                    int copyOffset =
                        (token >> (16 - bitCount)) + 1;
                    if (copyOffset > decompressedPosition)
                    {
                        hadWarnings = true;
                        continue;
                    }
                    if (decompressedPosition + copyLength > 4096)
                    {
                        hadWarnings = true;
                        copyLength = 4096 - decompressedPosition;
                    }

                    int copyIndex;
                    for (copyIndex = 0;
                        copyIndex < copyLength;
                        copyIndex++)
                    {
                        int sourceIndex =
                            output.Count - copyOffset;
                        if (sourceIndex < decompressedChunkStart ||
                            sourceIndex >= output.Count)
                        {
                            hadWarnings = true;
                            break;
                        }
                        output.Add(output[sourceIndex]);
                    }
                }
            }

            if (position < chunkEnd)
            {
                hadWarnings = true;
            }
        }

        private static void CopyToken(
            ushort token,
            int decompressedChunkStart,
            List<byte> output)
        {
            int decompressedPosition =
                output.Count - decompressedChunkStart;
            if (decompressedPosition <= 0)
            {
                throw new InvalidDataException(
                    "VBA copy token has no preceding data.");
            }

            int bitCount = 4;
            while ((1 << bitCount) < decompressedPosition &&
                bitCount < 12)
            {
                bitCount++;
            }

            int lengthMask = 0xFFFF >> bitCount;
            int copyLength = (token & lengthMask) + 3;
            int copyOffset = (token >> (16 - bitCount)) + 1;
            if (copyOffset > decompressedPosition)
            {
                throw new InvalidDataException(
                    "VBA copy token points before the chunk.");
            }

            EnsureChunkCapacity(decompressedPosition, copyLength);
            int index;
            for (index = 0; index < copyLength; index++)
            {
                int sourceIndex = output.Count - copyOffset;
                output.Add(output[sourceIndex]);
            }
        }

        private static void EnsureChunkCapacity(
            int decompressedPosition,
            int additionalLength)
        {
            if (decompressedPosition < 0 ||
                additionalLength < 0 ||
                decompressedPosition + additionalLength > 4096)
            {
                throw new InvalidDataException(
                    "VBA decompressed chunk exceeds 4096 bytes.");
            }
        }
    }
}
