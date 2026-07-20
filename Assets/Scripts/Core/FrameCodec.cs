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
    DeltaFrame = 0x00
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

    public static long GetTimestamp(byte[] packet)
    {
        return BitConverter.ToInt64(packet, 7);
    }

    public static bool IsCompressed(byte[] packet)
    {
        return (BitConverter.ToUInt16(packet, 1) & CompressFlag) != 0;
    }
}
