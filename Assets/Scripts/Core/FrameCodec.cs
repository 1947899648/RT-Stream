using System;
using System.Collections.Generic;
using UnityEngine;

public struct DirtyTile
{
    public int index;
    public byte[] data;
}

public enum FrameType : byte
{
    DeltaFrame = 0x00,
    TextureAnnounce = 0x01,
    SubscribeReq = 0x02
}

public static class FrameCodec
{
    public const int HeaderSize = 15;
    public const int TilePayloadOffset = 16;
    private const ushort CompressFlag = 0x8000;

    public static int LastEncodeOrigBytes { get; set; }
    public static int LastEncodeComprBytes { get; set; }
    public static int LastDecodeComprBytes { get; set; }
    public static int LastDecodeDecompBytes { get; set; }

    public static int GetBytesPerTile()
    {
        int tileSize = SceneConfig.TileSize;
        return tileSize * tileSize * 4;
    }

    public static byte GetTexId(byte[] packet)
    {
        return packet[HeaderSize];
    }

    public static ushort GetTileCount(byte[] packet)
    {
        return (ushort)(BitConverter.ToUInt16(packet, 1) & 0x7FFF);
    }

    public static long GetTimestamp(byte[] packet)
    {
        return BitConverter.ToInt64(packet, 7);
    }

    public static bool IsCompressed(byte[] packet)
    {
        return (BitConverter.ToUInt16(packet, 1) & CompressFlag) != 0;
    }

    public static byte[] EncodeDeltaFrame(byte texId, List<DirtyTile> tiles)
    {
        int tileBytes = GetBytesPerTile();
        int totalTicketPayload = tiles.Count * (4 + tileBytes);
        int payloadLen = 1 + totalTicketPayload;
        byte[] payload = new byte[payloadLen];

        payload[0] = texId;
        int pos = 1;
        foreach (DirtyTile tile in tiles)
        {
            BitConverter.GetBytes(tile.index).CopyTo(payload, pos);
            Buffer.BlockCopy(tile.data, 0, payload, pos + 4, tileBytes);
            pos += 4 + tileBytes;
        }

        byte[] compressed = LZ4.Wrap(payload, out int origSize, out int comprSize);
        bool useCompressed = compressed.Length < payloadLen;

        if (useCompressed)
        {
            LastEncodeOrigBytes = origSize;
            LastEncodeComprBytes = comprSize;
            Debug.Log($"<color=#88ccff>LZ4 Enc \u0394</color>  texId={texId}  {origSize / 1024f:F1}KB \u2192 {comprSize / 1024f:F1}KB  ({comprSize * 100f / origSize:F0}%)");
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

    public static byte[] EncodeTextureAnnounce(byte texId, ushort texWidth, ushort texHeight)
    {
        byte[] packet = new byte[HeaderSize + 5];

        packet[0] = (byte)FrameType.TextureAnnounce;
        BitConverter.GetBytes((ushort)0).CopyTo(packet, 1);
        BitConverter.GetBytes((uint)5).CopyTo(packet, 3);
        BitConverter.GetBytes(DateTime.UtcNow.Ticks).CopyTo(packet, 7);

        packet[HeaderSize] = texId;
        BitConverter.GetBytes(texWidth).CopyTo(packet, HeaderSize + 1);
        BitConverter.GetBytes(texHeight).CopyTo(packet, HeaderSize + 3);

        return packet;
    }

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

    public static byte[] EncodeSubscribeReq(byte[] texIds)
    {
        int count = texIds != null ? texIds.Length : 0;
        byte[] packet = new byte[2 + count];

        packet[0] = (byte)FrameType.SubscribeReq;
        packet[1] = (byte)count;
        if (count > 0)
            Buffer.BlockCopy(texIds, 0, packet, 2, count);

        return packet;
    }

    public static bool TryParseSubscribeReq(byte[] data, out byte[] texIds)
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

        if (data.Length < 2 + count) return false;
        texIds = new byte[count];
        Buffer.BlockCopy(data, 2, texIds, 0, count);
        return true;
    }
}
