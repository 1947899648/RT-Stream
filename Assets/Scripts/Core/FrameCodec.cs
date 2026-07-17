using System;
using System.Collections.Generic;

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
    private const int HeaderSize = 7;
    private static int TileBytes => SceneConfig.TileSize * SceneConfig.TileSize * 4;

    public static byte[] EncodeDeltaFrame(List<DirtyTile> tiles)
    {
        int tileBytes = TileBytes;
        int payloadLen = tiles.Count * (4 + tileBytes);
        byte[] packet = new byte[HeaderSize + payloadLen];

        packet[0] = (byte)FrameType.DeltaFrame;
        BitConverter.GetBytes((ushort)tiles.Count).CopyTo(packet, 1);
        BitConverter.GetBytes((uint)payloadLen).CopyTo(packet, 3);

        int pos = HeaderSize;
        foreach (DirtyTile tile in tiles)
        {
            BitConverter.GetBytes(tile.index).CopyTo(packet, pos);
            Buffer.BlockCopy(tile.data, 0, packet, pos + 4, tileBytes);
            pos += 4 + tileBytes;
        }
        return packet;
    }

    public static byte[] EncodeKeyFrame(int width, int height, byte[] pixels)
    {
        int payloadLen = 8 + pixels.Length;
        byte[] packet = new byte[HeaderSize + payloadLen];

        packet[0] = (byte)FrameType.KeyFrame;
        BitConverter.GetBytes((ushort)0).CopyTo(packet, 1);
        BitConverter.GetBytes((uint)payloadLen).CopyTo(packet, 3);

        BitConverter.GetBytes(width).CopyTo(packet, HeaderSize);
        BitConverter.GetBytes(height).CopyTo(packet, HeaderSize + 4);
        Buffer.BlockCopy(pixels, 0, packet, HeaderSize + 8, pixels.Length);

        return packet;
    }

    public static FrameType GetFrameType(byte[] packet)
    {
        return (FrameType)packet[0];
    }

    public static List<DirtyTile> DecodeDeltaFrame(byte[] packet)
    {
        ushort tileCount = BitConverter.ToUInt16(packet, 1);
        int tileBytes = TileBytes;
        List<DirtyTile> tiles = new List<DirtyTile>(tileCount);
        int pos = HeaderSize;

        for (int i = 0; i < tileCount; i++)
        {
            int index = BitConverter.ToInt32(packet, pos);
            byte[] data = new byte[tileBytes];
            Buffer.BlockCopy(packet, pos + 4, data, 0, tileBytes);
            tiles.Add(new DirtyTile { index = index, data = data });
            pos += 4 + tileBytes;
        }
        return tiles;
    }

    public static void DecodeKeyFrame(byte[] packet, out int width, out int height, out byte[] pixels)
    {
        width = BitConverter.ToInt32(packet, HeaderSize);
        height = BitConverter.ToInt32(packet, HeaderSize + 4);
        int pixelLen = width * height * 4;
        pixels = new byte[pixelLen];
        Buffer.BlockCopy(packet, HeaderSize + 8, pixels, 0, pixelLen);
    }
}
