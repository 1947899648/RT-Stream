using System;
using System.Collections.Generic;

namespace WPZ0325.RTStream
{
    /// <summary>
    /// 脏矩形瓦片数据。包含瓦片索引和对应的像素数据，用于增量帧传输。
    /// </summary>
    public struct DirtyTile
    {
        /// <summary>瓦片在纹理中的线性索引</summary>
        public int index;
        /// <summary>瓦片的原始像素数据（RGBA32，TileSize×TileSize×4 字节）</summary>
        public byte[] data;
    }

    /// <summary>
    /// 诊断用纹理信息。用于在 UI 中展示当前已注册纹理的基本属性。
    /// </summary>
    public struct DiagTextureInfo
    {
        /// <summary>纹理标识 ID</summary>
        public byte TexId;
        /// <summary>纹理宽度（像素）</summary>
        public int Width;
        /// <summary>纹理高度（像素）</summary>
        public int Height;
    }

    /// <summary>
    /// 帧类型标识。用于区分协议帧的用途。
    /// </summary>
    public enum FrameType : byte
    {
        /// <summary>增量帧：携带脏瓦片数据</summary>
        DeltaFrame = 0x00,
        /// <summary>纹理公告帧：通知接收端纹理尺寸信息</summary>
        TextureAnnounce = 0x01,
        /// <summary>订阅请求帧：客户端请求订阅特定纹理</summary>
        SubscribeReq = 0x02
    }

    /// <summary>
    /// RT Stream 协议帧编解码器。
    /// 负责帧打包、解包、纹理公告编码以及订阅请求的编解码。
    /// </summary>
    public static class FrameCodec
    {
        /// <summary>瓦片尺寸（64×64 像素）</summary>
        public const int TileSize = 64;
        /// <summary>协议帧头部字节数</summary>
        public const int HeaderSize = 15;
        /// <summary>帧数据中瓦片负载的起始偏移</summary>
        public const int TilePayloadOffset = 16;

        // 帧标志位：最高位（0x8000）为 1 表示负载已 LZ4 压缩
        private const ushort CompressFlag = 0x8000;

        /// <summary>最近一次编码的原始字节数（诊断用）</summary>
        public static int LastEncodeOrigBytes { get; set; }
        /// <summary>最近一次编码的压缩后字节数（诊断用）</summary>
        public static int LastEncodeComprBytes { get; set; }
        /// <summary>最近一次解码的压缩字节数（诊断用）</summary>
        public static int LastDecodeComprBytes { get; set; }
        /// <summary>最近一次解码的解压后字节数（诊断用）</summary>
        public static int LastDecodeDecompBytes { get; set; }

        /// <summary>
        /// 计算单个瓦片的字节数（TileSize×TileSize×4）。
        /// </summary>
        /// <returns>单个瓦片的 RGBA 字节数</returns>
        public static int GetBytesPerTile()
        {
            int tileSize = TileSize;
            return tileSize * tileSize * 4;
        }

        /// <summary>
        /// 从数据包中读取纹理 ID。
        /// </summary>
        /// <param name="packet">完整的帧数据包（含 HeaderSize 头部）</param>
        /// <returns>纹理 ID</returns>
        public static byte GetTexId(byte[] packet)
        {
            return packet[HeaderSize];
        }

        /// <summary>
        /// 从数据包中读取瓦片数量（不含压缩标志位）。
        /// </summary>
        /// <param name="packet">完整的帧数据包</param>
        /// <returns>瓦片数量</returns>
        public static ushort GetTileCount(byte[] packet)
        {
            return (ushort)(BitConverter.ToUInt16(packet, 1) & 0x7FFF);
        }

        /// <summary>
        /// 从数据包中读取时间戳（发送端 UTC Ticks）。
        /// </summary>
        /// <param name="packet">完整的帧数据包</param>
        /// <returns>时间戳（Ticks）</returns>
        public static long GetTimestamp(byte[] packet)
        {
            return BitConverter.ToInt64(packet, 7);
        }

        /// <summary>
        /// 判断数据包的负载是否经过 LZ4 压缩。
        /// </summary>
        /// <param name="packet">完整的帧数据包</param>
        /// <returns>true 表示负载已压缩</returns>
        public static bool IsCompressed(byte[] packet)
        {
            return (BitConverter.ToUInt16(packet, 1) & CompressFlag) != 0;
        }

        /// <summary>
        /// 编码增量帧。将脏瓦片列表序列化为帧数据，并可选压缩。
        /// </summary>
        /// <param name="texId">纹理 ID</param>
        /// <param name="tiles">脏瓦片列表</param>
        /// <returns>完整的增量帧数据包</returns>
        public static byte[] EncodeDeltaFrame(byte texId, List<DirtyTile> tiles)
        {
            int tileBytes = GetBytesPerTile();
            int totalTicketPayload = tiles.Count * (4 + tileBytes);
            int payloadLen = 1 + totalTicketPayload;
            byte[] payload = new byte[payloadLen];

            // 负载首字节为纹理 ID，随后按 [索引(4B) + 数据(tileBytes)] 重复
            payload[0] = texId;
            int pos = 1;
            foreach (DirtyTile tile in tiles)
            {
                BitConverter.GetBytes(tile.index).CopyTo(payload, pos);
                Buffer.BlockCopy(tile.data, 0, payload, pos + 4, tileBytes);
                pos += 4 + tileBytes;
            }

            byte[] compressed = LZ4.Wrap(payload, out int origSize, out int comprSize);
            // 仅当压缩后体积更小时才使用压缩版本
            bool useCompressed = compressed.Length < payloadLen;

            if (useCompressed)
            {
                LastEncodeOrigBytes = origSize;
                LastEncodeComprBytes = comprSize;
            }

            int finalPayloadLen = useCompressed ? compressed.Length : payloadLen;
            byte[] packet = new byte[HeaderSize + finalPayloadLen];

            // 填充协议头
            packet[0] = (byte)FrameType.DeltaFrame;
            ushort flags = (ushort)tiles.Count;
            if (useCompressed) flags |= CompressFlag;
            BitConverter.GetBytes(flags).CopyTo(packet, 1);
            BitConverter.GetBytes((uint)finalPayloadLen).CopyTo(packet, 3);
            BitConverter.GetBytes(DateTime.UtcNow.Ticks).CopyTo(packet, 7);

            if (useCompressed)
                Buffer.BlockCopy(compressed, 0, packet, HeaderSize, compressed.Length);
            else
                Buffer.BlockCopy(payload, 0, packet, HeaderSize, payloadLen);

            return packet;
        }

        /// <summary>
        /// 编码纹理公告帧。通知接收端新纹理的 ID 和尺寸。
        /// </summary>
        /// <param name="texId">纹理 ID</param>
        /// <param name="texWidth">纹理宽度（像素）</param>
        /// <param name="texHeight">纹理高度（像素）</param>
        /// <returns>完整的纹理公告帧数据包</returns>
        public static byte[] EncodeTextureAnnounce(byte texId, ushort texWidth, ushort texHeight)
        {
            byte[] packet = new byte[HeaderSize + 5];

            packet[0] = (byte)FrameType.TextureAnnounce;
            BitConverter.GetBytes((ushort)0).CopyTo(packet, 1);
            BitConverter.GetBytes((uint)5).CopyTo(packet, 3);
            BitConverter.GetBytes(DateTime.UtcNow.Ticks).CopyTo(packet, 7);

            // 负载：texId(1B) + width(2B) + height(2B) = 5 字节
            packet[HeaderSize] = texId;
            BitConverter.GetBytes(texWidth).CopyTo(packet, HeaderSize + 1);
            BitConverter.GetBytes(texHeight).CopyTo(packet, HeaderSize + 3);

            return packet;
        }

        /// <summary>
        /// 尝试解析纹理公告帧。
        /// </summary>
        /// <param name="packet">帧数据包</param>
        /// <param name="texId">输出：纹理 ID</param>
        /// <param name="texWidth">输出：纹理宽度</param>
        /// <param name="texHeight">输出：纹理高度</param>
        /// <returns>解析成功返回 true，否则 false</returns>
        public static bool TryParseTextureAnnounce(byte[] packet, out byte texId, out ushort texWidth, out ushort texHeight)
        {
            texId = 0;
            texWidth = 0;
            texHeight = 0;

            if (packet.Length < HeaderSize + 5) return false;
            if (packet[0] != (byte)FrameType.TextureAnnounce) return false;

            texId = packet[HeaderSize];
            texWidth = BitConverter.ToUInt16(packet, HeaderSize + 1);
            texHeight = BitConverter.ToUInt16(packet, HeaderSize + 3);
            return true;
        }

        /// <summary>
        /// 编码订阅请求帧。客户端用于告知发送端需要哪些纹理。
        /// </summary>
        /// <param name="texIds">需要订阅的纹理 ID 数组，null 表示订阅全部</param>
        /// <returns>订阅请求帧数据包</returns>
        public static byte[] EncodeSubscribeReq(byte[] texIds)
        {
            int count = texIds != null ? texIds.Length : 0;
            byte[] packet = new byte[2 + count];

            // 格式：FrameType(1B) + Count(1B) + texIds...
            packet[0] = (byte)FrameType.SubscribeReq;
            packet[1] = (byte)count;
            if (count > 0)
                Buffer.BlockCopy(texIds, 0, packet, 2, count);

            return packet;
        }

        /// <summary>
        /// 尝试解析订阅请求帧。
        /// </summary>
        /// <param name="data">帧数据</param>
        /// <param name="texIds">输出：订阅的纹理 ID 数组，null 表示订阅全部</param>
        /// <returns>解析成功返回 true，否则 false</returns>
        public static bool TryParseSubscribeReq(byte[] data, out byte[] texIds)
        {
            texIds = null;

            if (data.Length < 2) return false;
            if (data[0] != (byte)FrameType.SubscribeReq) return false;

            int count = data[1];
            // count 为 0 表示订阅所有纹理
            if (count == 0)
            {
                texIds = null;
                return true;
            }

            if (data.Length < 2 + count) return false;
            texIds = new byte[count];
            Buffer.BlockCopy(data, 2, texIds, 0, count);
            return true;
        }
    }
}
