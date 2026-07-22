using System;
using System.Collections.Generic;
using System.Net.Sockets;
using System.Text;

namespace WPZ0325.RTStream
{
    /// <summary>
    /// 脏矩形瓦片数据。包含瓦片索引和对应的像素数据，用于增量帧传输。
    /// </summary>
    public struct DirtyTile
    {
        /// <summary>瓦片在纹理中的线性索引</summary>
        public int Index;
        /// <summary>瓦片的原始像素数据（RGBA32，TileSize×TileSize×4 字节）</summary>
        public byte[] Data;
    }

    /// <summary>
    /// 诊断用纹理信息。用于在 UI 中展示当前已注册纹理的基本属性。
    /// </summary>
    public struct DiagTextureInfo
    {
        /// <summary>纹理标识名称</summary>
        public string TexId;
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
        #region 公开常量

        /// <summary>瓦片尺寸（64×64 像素）</summary>
        public const int TileSize = 64;
        /// <summary>协议帧头部字节数</summary>
        public const int HeaderSize = 15;

        private const ushort CompressFlag = 0x8000;

        #endregion

        #region 公开属性

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
        public static int GetBytesPerTile()
        {
            int tileSize = TileSize;
            return tileSize * tileSize * 4;
        }

        /// <summary>
        /// 获取帧数据中瓦片负载的起始偏移（动态，取决于 texId 长度）。
        /// </summary>
        public static int GetTilePayloadOffset(byte[] packet)
        {
            return HeaderSize + 1 + packet[HeaderSize];
        }

        #endregion

        #region 帧编码

        /// <summary>
        /// 编码增量帧。将脏瓦片列表序列化为帧数据，并可选压缩。
        /// </summary>
        /// <param name="texId">纹理标识名称</param>
        /// <param name="tiles">脏瓦片列表</param>
        /// <returns>完整的增量帧数据包</returns>
        public static byte[] EncodeDeltaFrame(string texId, List<DirtyTile> tiles)
        {
            byte[] texIdBytes = Encoding.UTF8.GetBytes(texId);
            int texIdLen = texIdBytes.Length;

            int tileBytes = GetBytesPerTile();
            int totalTicketPayload = tiles.Count * (4 + tileBytes);
            int payloadLen = 1 + texIdLen + totalTicketPayload;
            byte[] payload = new byte[payloadLen];

            payload[0] = (byte)texIdLen;
            Buffer.BlockCopy(texIdBytes, 0, payload, 1, texIdLen);

            int pos = 1 + texIdLen;
            foreach (DirtyTile tile in tiles)
            {
                BitConverter.GetBytes(tile.Index).CopyTo(payload, pos);
                Buffer.BlockCopy(tile.Data, 0, payload, pos + 4, tileBytes);
                pos += 4 + tileBytes;
            }

            byte[] compressed = LZ4.Wrap(payload, out int origSize, out int comprSize);
            bool useCompressed = compressed.Length < payloadLen;

            if (useCompressed)
            {
                LastEncodeOrigBytes = origSize;
                LastEncodeComprBytes = comprSize;
            }

            int finalPayloadLen = useCompressed ? compressed.Length : payloadLen;
            byte[] packet = new byte[HeaderSize + finalPayloadLen];

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
        /// 编码纹理公告帧。通知接收端新纹理的名称和尺寸。
        /// </summary>
        public static byte[] EncodeTextureAnnounce(string texId, ushort texWidth, ushort texHeight)
        {
            byte[] texIdBytes = Encoding.UTF8.GetBytes(texId);
            int texIdLen = texIdBytes.Length;
            int payloadLen = 1 + texIdLen + 4;
            byte[] packet = new byte[HeaderSize + payloadLen];

            packet[0] = (byte)FrameType.TextureAnnounce;
            BitConverter.GetBytes((ushort)0).CopyTo(packet, 1);
            BitConverter.GetBytes((uint)payloadLen).CopyTo(packet, 3);
            BitConverter.GetBytes(DateTime.UtcNow.Ticks).CopyTo(packet, 7);

            packet[HeaderSize] = (byte)texIdLen;
            Buffer.BlockCopy(texIdBytes, 0, packet, HeaderSize + 1, texIdLen);
            BitConverter.GetBytes(texWidth).CopyTo(packet, HeaderSize + 1 + texIdLen);
            BitConverter.GetBytes(texHeight).CopyTo(packet, HeaderSize + 1 + texIdLen + 2);

            return packet;
        }

        /// <summary>
        /// 编码订阅请求帧。客户端用于告知发送端需要哪些纹理。
        /// </summary>
        /// <param name="texIds">需要订阅的纹理标识数组，null 表示订阅全部</param>
        public static byte[] EncodeSubscribeReq(string[] texIds)
        {
            int count = texIds != null ? texIds.Length : 0;
            int totalBytes = 0;
            byte[][] idBytes = null;

            if (count > 0)
            {
                idBytes = new byte[count][];
                for (int i = 0; i < count; i++)
                {
                    idBytes[i] = Encoding.UTF8.GetBytes(texIds[i]);
                    totalBytes += 1 + idBytes[i].Length;
                }
            }

            byte[] packet = new byte[2 + totalBytes];

            packet[0] = (byte)FrameType.SubscribeReq;
            packet[1] = (byte)count;
            int pos = 2;
            for (int i = 0; i < count; i++)
            {
                packet[pos] = (byte)idBytes[i].Length;
                Buffer.BlockCopy(idBytes[i], 0, packet, pos + 1, idBytes[i].Length);
                pos += 1 + idBytes[i].Length;
            }

            return packet;
        }

        #endregion

        #region 帧解析

        /// <summary>
        /// 从数据包中读取纹理标识。
        /// </summary>
        public static string GetTexId(byte[] packet)
        {
            int len = packet[HeaderSize];
            return Encoding.UTF8.GetString(packet, HeaderSize + 1, len);
        }

        /// <summary>
        /// 从数据包中读取瓦片数量（不含压缩标志位）。
        /// </summary>
        public static ushort GetTileCount(byte[] packet)
        {
            return (ushort)(BitConverter.ToUInt16(packet, 1) & 0x7FFF);
        }

        /// <summary>
        /// 从数据包中读取时间戳（发送端 UTC Ticks）。
        /// </summary>
        public static long GetTimestamp(byte[] packet)
        {
            return BitConverter.ToInt64(packet, 7);
        }

        /// <summary>
        /// 判断数据包的负载是否经过 LZ4 压缩。
        /// </summary>
        public static bool IsCompressed(byte[] packet)
        {
            return (BitConverter.ToUInt16(packet, 1) & CompressFlag) != 0;
        }

        /// <summary>
        /// 尝试解析纹理公告帧。
        /// </summary>
        public static bool TryParseTextureAnnounce(byte[] packet, out string texId, out ushort texWidth, out ushort texHeight)
        {
            texId = null;
            texWidth = 0;
            texHeight = 0;

            if (packet.Length < HeaderSize + 1) return false;
            if (packet[0] != (byte)FrameType.TextureAnnounce) return false;

            int idLen = packet[HeaderSize];
            if (packet.Length < HeaderSize + 1 + idLen + 4) return false;

            texId = Encoding.UTF8.GetString(packet, HeaderSize + 1, idLen);
            texWidth = BitConverter.ToUInt16(packet, HeaderSize + 1 + idLen);
            texHeight = BitConverter.ToUInt16(packet, HeaderSize + 1 + idLen + 2);
            return true;
        }

        /// <summary>
        /// 尝试解析订阅请求帧。
        /// </summary>
        public static bool TryParseSubscribeReq(byte[] data, out string[] texIds)
        {
            texIds = null;

            if (data.Length < 2) return false;
            if (data[0] != (byte)FrameType.SubscribeReq) return false;

            int count = data[1];
            if (count == 0)
            {
                texIds = null;
                return true;
            }

            texIds = new string[count];
            int pos = 2;
            for (int i = 0; i < count; i++)
            {
                if (data.Length < pos + 1) return false;
                int idLen = data[pos++];
                if (data.Length < pos + idLen) return false;
                texIds[i] = Encoding.UTF8.GetString(data, pos, idLen);
                pos += idLen;
            }
            return true;
        }

        /// <summary>
        /// 从网络流中读取指定数量的字节，阻塞直到填满缓冲区或流结束。
        /// </summary>
        public static bool ReadExact(NetworkStream s, byte[] buf, int offset, int count)
        {
            int received = 0;
            while (received < count)
            {
                int n = s.Read(buf, offset + received, count - received);
                if (n <= 0) return false;
                received += n;
            }
            return true;
        }

        #endregion
    }
}
