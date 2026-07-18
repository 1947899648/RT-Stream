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
    KeyFrame = 0x01
}

public static class FrameCodec
{
    public const int HeaderSize = 15;
    private const ushort CompressFlag = 0x8000;
    private static int TileBytes => SceneConfig.TileSize * SceneConfig.TileSize * 4;

    public static int LastEncodeOrigBytes { get; set; }
    public static int LastEncodeComprBytes { get; set; }
    public static int LastDecodeComprBytes { get; set; }
    public static int LastDecodeDecompBytes { get; set; }

    public static byte[] EncodeDeltaFrame(List<DirtyTile> tiles)
    {
        int tileBytes = TileBytes;
        int payloadLen = tiles.Count * (4 + tileBytes);
        byte[] payload = new byte[payloadLen];

        int pos = 0;
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
            Debug.Log($"<color=#88ccff>LZ4 Enc Δ</color>  {origSize / 1024f:F1}KB → {comprSize / 1024f:F1}KB  ({comprSize * 100f / origSize:F0}%)");
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

    public static byte[] EncodeKeyFrame(int width, int height, byte[] pixels)
    {
        int payloadLen = 8 + pixels.Length;
        byte[] payload = new byte[payloadLen];

        BitConverter.GetBytes(width).CopyTo(payload, 0);
        BitConverter.GetBytes(height).CopyTo(payload, 4);
        Buffer.BlockCopy(pixels, 0, payload, 8, pixels.Length);

        byte[] compressed = LZ4.Wrap(payload, out int origSize, out int comprSize);
        bool useCompressed = compressed.Length < payloadLen;

        if (useCompressed)
        {
            LastEncodeOrigBytes = origSize;
            LastEncodeComprBytes = comprSize;
            Debug.Log($"<color=#88ccff>LZ4 Enc K</color>  {origSize / 1024f:F1}KB → {comprSize / 1024f:F1}KB  ({comprSize * 100f / origSize:F0}%)");
        }

        int finalPayloadLen = useCompressed ? compressed.Length : payloadLen;
        byte[] packet = new byte[HeaderSize + finalPayloadLen];

        packet[0] = (byte)FrameType.KeyFrame;
        ushort flags = useCompressed ? CompressFlag : (ushort)0;
        BitConverter.GetBytes(flags).CopyTo(packet, 1);
        BitConverter.GetBytes((uint)finalPayloadLen).CopyTo(packet, 3);
        BitConverter.GetBytes(DateTime.UtcNow.Ticks).CopyTo(packet, 7);

        if (useCompressed)
            Buffer.BlockCopy(compressed, 0, packet, HeaderSize, compressed.Length);
        else
            Buffer.BlockCopy(payload, 0, packet, HeaderSize, payloadLen);

        return packet;
    }

    public static FrameType GetFrameType(byte[] packet)
    {
        return (FrameType)packet[0];
    }

    public static long GetTimestamp(byte[] packet)
    {
        return BitConverter.ToInt64(packet, 7);
    }

    public static bool IsCompressed(byte[] packet)
    {
        return (BitConverter.ToUInt16(packet, 1) & CompressFlag) != 0;
    }

    public static List<DirtyTile> DecodeDeltaFrame(byte[] packet)
    {
        ushort flags = BitConverter.ToUInt16(packet, 1);
        bool compressed = (flags & CompressFlag) != 0;
        ushort tileCount = (ushort)(flags & 0x7FFF);

        byte[] payload;
        int readOffset;
        if (compressed)
        {
            int comprBlockLen = BitConverter.ToInt32(packet, 3);
            byte[] comprBlock = new byte[comprBlockLen];
            Buffer.BlockCopy(packet, HeaderSize, comprBlock, 0, comprBlockLen);
            payload = LZ4.Unwrap(comprBlock, out int comprSz, out int decompSz);
            LastDecodeComprBytes = comprSz;
            LastDecodeDecompBytes = decompSz;
            Debug.Log($"<color=#ccff88>LZ4 Dec Δ</color>  {comprSz / 1024f:F1}KB → {decompSz / 1024f:F1}KB  (×{decompSz / (float)comprSz:F1})");
            readOffset = 0;
        }
        else
        {
            payload = packet;
            readOffset = HeaderSize;
        }

        int tileBytes = TileBytes;
        List<DirtyTile> tiles = new List<DirtyTile>(tileCount);

        for (int i = 0; i < tileCount; i++)
        {
            int index = BitConverter.ToInt32(payload, readOffset);
            byte[] data = new byte[tileBytes];
            Buffer.BlockCopy(payload, readOffset + 4, data, 0, tileBytes);
            tiles.Add(new DirtyTile { index = index, data = data });
            readOffset += 4 + tileBytes;
        }
        return tiles;
    }

    public static void DecodeKeyFrame(byte[] packet, out int width, out int height, out byte[] pixels)
    {
        ushort flags = BitConverter.ToUInt16(packet, 1);
        bool compressed = (flags & CompressFlag) != 0;

        byte[] payload;
        int readOffset;
        if (compressed)
        {
            int comprBlockLen = BitConverter.ToInt32(packet, 3);
            byte[] comprBlock = new byte[comprBlockLen];
            Buffer.BlockCopy(packet, HeaderSize, comprBlock, 0, comprBlockLen);
            payload = LZ4.Unwrap(comprBlock, out int comprSz, out int decompSz);
            LastDecodeComprBytes = comprSz;
            LastDecodeDecompBytes = decompSz;
            Debug.Log($"<color=#ccff88>LZ4 Dec K</color>  {comprSz / 1024f:F1}KB → {decompSz / 1024f:F1}KB  (×{decompSz / (float)comprSz:F1})");
            readOffset = 0;
        }
        else
        {
            payload = packet;
            readOffset = HeaderSize;
        }

        width = BitConverter.ToInt32(payload, readOffset);
        height = BitConverter.ToInt32(payload, readOffset + 4);
        int pixelLen = width * height * 4;
        pixels = new byte[pixelLen];
        Buffer.BlockCopy(payload, readOffset + 8, pixels, 0, pixelLen);
    }
}
