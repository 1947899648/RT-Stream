using System;
using System.Collections.Generic;

public static class LZ4
{
    private const int MIN_MATCH = 4;
    private const int HASH_LOG = 14;
    private const int HASH_SIZE = 1 << HASH_LOG;
    private const int MAX_OFFSET = 65535;
    private const int LAST_LITERALS = 5;

    public static byte[] Wrap(byte[] input, out int originalSize, out int compressedSize)
    {
        originalSize = input.Length;

        if (input.Length < MIN_MATCH)
        {
            compressedSize = input.Length;
            byte[] tiny = new byte[4 + input.Length];
            BitConverter.GetBytes(originalSize).CopyTo(tiny, 0);
            Buffer.BlockCopy(input, 0, tiny, 4, input.Length);
            return tiny;
        }

        byte[] body = Compress(input);
        compressedSize = body.Length;

        byte[] result = new byte[4 + body.Length];
        BitConverter.GetBytes(originalSize).CopyTo(result, 0);
        Buffer.BlockCopy(body, 0, result, 4, body.Length);
        return result;
    }

    public static byte[] Unwrap(byte[] input, out int compressedSize, out int decompressedSize)
    {
        decompressedSize = BitConverter.ToInt32(input, 0);

        if (decompressedSize <= 0 || decompressedSize > 64 * 1024 * 1024)
            throw new InvalidOperationException("LZ4 Unwrap: invalid decompressed size");

        compressedSize = input.Length - 4;

        if (decompressedSize == compressedSize)
        {
            byte[] direct = new byte[decompressedSize];
            Buffer.BlockCopy(input, 4, direct, 0, decompressedSize);
            return direct;
        }

        return Decompress(input, 4, decompressedSize);
    }

    private static byte[] Compress(byte[] input)
    {
        int inputLen = input.Length;
        int[] hashTable = new int[HASH_SIZE];
        List<byte> output = new List<byte>(inputLen + inputLen / 255 + 16);

        int anchor = 0;
        int pos = 0;

        while (pos + LAST_LITERALS <= inputLen)
        {
            uint h = Hash4(input, pos);
            int refEntry = hashTable[h];
            hashTable[h] = pos + 1;

            if (refEntry > 0)
            {
                int refPos = refEntry - 1;
                if (pos - refPos <= MAX_OFFSET)
                {
                    int matchLen = CountMatch(input, refPos, pos, inputLen);
                    if (matchLen >= MIN_MATCH)
                    {
                        EncodeSequence(output, pos - anchor, matchLen - MIN_MATCH, input, anchor, pos - refPos);
                        pos += matchLen;
                        anchor = pos;
                        continue;
                    }
                }
            }
            pos++;
        }

        EncodeLastLiterals(output, inputLen - anchor, input, anchor);

        return output.ToArray();
    }

    private static byte[] Decompress(byte[] input, int start, int outputLen)
    {
        byte[] output = new byte[outputLen];
        int ip = start;
        int op = 0;
        int inputEnd = input.Length;

        while (ip < inputEnd - 1 && op < outputLen)
        {
            byte token = input[ip++];

            int litLen = token >> 4;
            if (litLen == 15)
                litLen += ReadVarLen(input, ref ip);

            if (ip + litLen > inputEnd || op + litLen > outputLen) break;
            Buffer.BlockCopy(input, ip, output, op, litLen);
            ip += litLen;
            op += litLen;

            if (op >= outputLen) break;

            int offset = BitConverter.ToUInt16(input, ip);
            ip += 2;

            int matchLen = (token & 0x0F) + MIN_MATCH;
            if ((token & 0x0F) == 15)
                matchLen += ReadVarLen(input, ref ip);

            int matchSrc = op - offset;
            int copyLen = matchLen;
            if (op + copyLen > outputLen) copyLen = outputLen - op;
            for (int i = 0; i < copyLen; i++)
                output[op + i] = output[matchSrc + i];
            op += copyLen;
        }

        return output;
    }

    private static void EncodeSequence(List<byte> dst, int litLen, int matchLen, byte[] src, int litStart, int offset)
    {
        byte token = 0;

        if (litLen >= 15) { token |= (15 << 4); }
        else { token |= (byte)(litLen << 4); }

        if (matchLen >= 15) { token |= 15; }
        else { token |= (byte)matchLen; }

        dst.Add(token);

        if (litLen >= 15) WriteVarLen(dst, litLen - 15);

        for (int i = 0; i < litLen; i++)
            dst.Add(src[litStart + i]);

        dst.Add((byte)offset);
        dst.Add((byte)(offset >> 8));

        if (matchLen >= 15) WriteVarLen(dst, matchLen - 15);
    }

    private static void EncodeLastLiterals(List<byte> dst, int litLen, byte[] src, int litStart)
    {
        byte token = (byte)(litLen >= 15 ? (15 << 4) : (litLen << 4));
        dst.Add(token);

        if (litLen >= 15) WriteVarLen(dst, litLen - 15);

        for (int i = 0; i < litLen; i++)
            dst.Add(src[litStart + i]);
    }

    private static void WriteVarLen(List<byte> dst, int value)
    {
        while (value >= 255)
        {
            dst.Add(255);
            value -= 255;
        }
        dst.Add((byte)value);
    }

    private static int ReadVarLen(byte[] src, ref int pos)
    {
        int value = 0;
        byte b;
        while ((b = src[pos++]) == 255)
            value += 255;
        return value + b;
    }

    private static uint Hash4(byte[] buf, int pos)
    {
        uint v = (uint)buf[pos] | ((uint)buf[pos + 1] << 8) | ((uint)buf[pos + 2] << 16) | ((uint)buf[pos + 3] << 24);
        return (v * 2654435761u) >> (32 - HASH_LOG);
    }

    private static int CountMatch(byte[] buf, int p1, int p2, int limit)
    {
        int start = p2;
        while (p2 < limit && buf[p1] == buf[p2])
        {
            p1++;
            p2++;
        }
        return p2 - start;
    }
}
