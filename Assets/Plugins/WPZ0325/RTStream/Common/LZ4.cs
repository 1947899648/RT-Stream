using System;
using System.Collections.Generic;

namespace WPZ0325.RTStream
{
    /// <summary>
    /// LZ4 快速压缩算法的纯 C# 实现。
    /// 封装格式为 4 字节原始长度 + LZ4 压缩数据体，适用于帧数据的实时压缩传输。
    /// </summary>
    public static class LZ4
    {
        // LZ4 规范要求的最小匹配长度（4 字节）
        private const int MIN_MATCH = 4;
        // 哈希表位宽，决定查重窗口大小
        private const int HASH_LOG = 14;
        private const int HASH_SIZE = 1 << HASH_LOG;
        // LZ4 格式中偏移量以 2 字节存储，上限 65535
        private const int MAX_OFFSET = 65535;
        // 块末尾必须保留的文字段最小长度，防止扫描越界
        private const int LAST_LITERALS = 5;

        /// <summary>
        /// 压缩原始数据并封装为带长度头的 LZ4 格式包。
        /// </summary>
        /// <param name="input">原始字节数据</param>
        /// <param name="originalSize">原始数据大小（字节）</param>
        /// <param name="compressedSize">压缩体大小（不含 4 字节头部）</param>
        /// <returns>4 字节原始长度 + 压缩数据的完整包</returns>
        public static byte[] Wrap(byte[] input, out int originalSize, out int compressedSize)
        {
            originalSize = input.Length;

            // 小于最小匹配长度的数据不做压缩，避免膨胀
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

            // 组装：4 字节原始长度 + 压缩数据体
            byte[] result = new byte[4 + body.Length];
            BitConverter.GetBytes(originalSize).CopyTo(result, 0);
            Buffer.BlockCopy(body, 0, result, 4, body.Length);
            return result;
        }

        /// <summary>
        /// 解压缩由 <see cref="Wrap"/> 生成的 LZ4 格式包。
        /// </summary>
        /// <param name="input">带 4 字节长度头的压缩包</param>
        /// <param name="compressedSize">压缩体大小（不含头部）</param>
        /// <param name="decompressedSize">解压后的原始数据大小</param>
        /// <returns>解压后的原始字节数据</returns>
        /// <exception cref="InvalidOperationException">解压尺寸超出合法范围时抛出</exception>
        public static byte[] Unwrap(byte[] input, out int compressedSize, out int decompressedSize)
        {
            decompressedSize = BitConverter.ToInt32(input, 0);

            // 安全检查：解压尺寸必须在合理范围内
            if (decompressedSize <= 0 || decompressedSize > 1024 * 1024 * 1024)
                throw new InvalidOperationException("LZ4 Unwrap: invalid decompressed size");

            compressedSize = input.Length - 4;

            // 压缩前后等长说明数据实际未被压缩（极小数据块）
            if (decompressedSize == compressedSize)
            {
                byte[] direct = new byte[decompressedSize];
                Buffer.BlockCopy(input, 4, direct, 0, decompressedSize);
                return direct;
            }

            return Decompress(input, 4, decompressedSize);
        }

        // 对输入数据做 LZ4 块压缩，返回纯压缩数据（不含头部）
        private static byte[] Compress(byte[] input)
        {
            int inputLen = input.Length;
            int[] hashTable = new int[HASH_SIZE];
            List<byte> output = new List<byte>(inputLen + inputLen / 255 + 16);

            int anchor = 0; // 当前文字段起点
            int pos = 0;    // 当前扫描位置

            // 主循环：扫描输入查找重复串
            while (pos + LAST_LITERALS <= inputLen)
            {
                uint h = Hash4(input, pos); // 计算当前位置 4 字节哈希
                int refEntry = hashTable[h];
                hashTable[h] = pos + 1;     // 存储位置+1，用 0 表示未初始化

                if (refEntry > 0)
                {
                    int refPos = refEntry - 1;
                    // 仅当偏移量在可编码范围内才尝试匹配
                    if (pos - refPos <= MAX_OFFSET)
                    {
                        int matchLen = CountMatch(input, refPos, pos, inputLen);
                        if (matchLen >= MIN_MATCH)
                        {
                            // 找到足够长的匹配，输出文字段+匹配序列
                            EncodeSequence(output, pos - anchor, matchLen - MIN_MATCH, input, anchor, pos - refPos);
                            pos += matchLen;
                            anchor = pos;
                            continue;
                        }
                    }
                }
                pos++;
            }

            // 输出末尾剩余的文字段
            EncodeLastLiterals(output, inputLen - anchor, input, anchor);

            return output.ToArray();
        }

        // LZ4 解压缩核心：逐令牌解析文字段与匹配序列
        private static byte[] Decompress(byte[] input, int start, int outputLen)
        {
            byte[] output = new byte[outputLen];
            int ip = start;  // 输入读取位置
            int op = 0;      // 输出写入位置
            int inputEnd = input.Length;

            // 令牌字节格式：高 4 位 = 文字长度，低 4 位 = 匹配长度
            while (ip < inputEnd - 1 && op < outputLen)
            {
                byte token = input[ip++];

                // 解析文字段长度（15 表示需要额外字节扩展）
                int litLen = token >> 4;
                if (litLen == 15)
                    litLen += ReadVarLen(input, ref ip);

                if (ip + litLen > inputEnd || op + litLen > outputLen) break;
                Buffer.BlockCopy(input, ip, output, op, litLen);
                ip += litLen;
                op += litLen;

                if (op >= outputLen) break;

                // 解析偏移量（2 字节，小端序）
                int offset = BitConverter.ToUInt16(input, ip);
                ip += 2;

                // 解析匹配长度（基础 4，15 表示需额外字节扩展）
                int matchLen = (token & 0x0F) + MIN_MATCH;
                if ((token & 0x0F) == 15)
                    matchLen += ReadVarLen(input, ref ip);

                // 从已解码区域复制匹配数据（允许重叠复制，逐字节保证正确性）
                int matchSrc = op - offset;
                int copyLen = matchLen;
                if (op + copyLen > outputLen) copyLen = outputLen - op;
                for (int i = 0; i < copyLen; i++)
                    output[op + i] = output[matchSrc + i];
                op += copyLen;
            }

            return output;
        }

        // 编码一个序列块：文字段 + 匹配（偏移量 + 长度）
        private static void EncodeSequence(List<byte> dst, int litLen, int matchLen, byte[] src, int litStart, int offset)
        {
            // 构造令牌字节：高 4 位 = 文字长度，低 4 位 = 匹配长度
            byte token = 0;

            if (litLen >= 15) { token |= (15 << 4); }
            else { token |= (byte)(litLen << 4); }

            if (matchLen >= 15) { token |= 15; }
            else { token |= (byte)matchLen; }

            dst.Add(token);

            // 扩展文字长度（超过 14 的部分用可变长度编码）
            if (litLen >= 15) WriteVarLen(dst, litLen - 15);

            // 写入文字段原始数据
            for (int i = 0; i < litLen; i++)
                dst.Add(src[litStart + i]);

            // 写入 2 字节偏移量（小端序）
            dst.Add((byte)offset);
            dst.Add((byte)(offset >> 8));

            // 扩展匹配长度
            if (matchLen >= 15) WriteVarLen(dst, matchLen - 15);
        }

        // 编码末尾文字段（无后续匹配，仅包含文字段）
        private static void EncodeLastLiterals(List<byte> dst, int litLen, byte[] src, int litStart)
        {
            byte token = (byte)(litLen >= 15 ? (15 << 4) : (litLen << 4));
            dst.Add(token);

            if (litLen >= 15) WriteVarLen(dst, litLen - 15);

            for (int i = 0; i < litLen; i++)
                dst.Add(src[litStart + i]);
        }

        // 写入 LZ4 可变长度编码值（每字节最多承载 255）
        private static void WriteVarLen(List<byte> dst, int value)
        {
            while (value >= 255)
            {
                dst.Add(255);
                value -= 255;
            }
            dst.Add((byte)value);
        }

        // 读取 LZ4 可变长度编码值
        private static int ReadVarLen(byte[] src, ref int pos)
        {
            int value = 0;
            byte b;
            while ((b = src[pos++]) == 255)
                value += 255;
            return value + b;
        }

        // 计算 4 字节窗口的乘法哈希值，用于哈希表索引
        private static uint Hash4(byte[] buf, int pos)
        {
            uint v = (uint)buf[pos] | ((uint)buf[pos + 1] << 8) | ((uint)buf[pos + 2] << 16) | ((uint)buf[pos + 3] << 24);
            return (v * 2654435761u) >> (32 - HASH_LOG);
        }

        // 计算从 p1 和 p2 开始向前匹配的相同字节数
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
}
