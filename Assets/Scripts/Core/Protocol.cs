using System;
using System.Collections.Generic;
using System.Text;

public static class Protocol
{
    public const byte ClientHello      = 0x01;
    public const byte ClientId         = 0x02;
    public const byte CreateRoom       = 0x10;
    public const byte RoomCreated      = 0x11;
    public const byte RequestRoomList  = 0x12;
    public const byte RoomList         = 0x13;
    public const byte JoinRoom         = 0x14;
    public const byte JoinAccepted     = 0x15;
    public const byte LeaveRoom        = 0x16;
    public const byte RoomUsersChanged = 0x20;
    public const byte DrawingCmd       = 0x30;
    public const byte CanvasDirty      = 0x40;
    public const byte CanvasKey        = 0x41;

    public const byte RoleHC = 1;
    public const byte RoleUC = 2;

    public static byte[] CreateMessage(byte type, byte[] payload)
    {
        byte[] msg = new byte[1 + (payload != null ? payload.Length : 0)];
        msg[0] = type;
        if (payload != null) Buffer.BlockCopy(payload, 0, msg, 1, payload.Length);
        return msg;
    }

    public static byte GetMessageType(ArraySegment<byte> data)
    {
        return data.Array[data.Offset];
    }

    public static void WriteUShort(byte[] buf, int offset, ushort val)
    {
        buf[offset] = (byte)val;
        buf[offset + 1] = (byte)(val >> 8);
    }

    public static ushort ReadUShort(byte[] buf, int offset)
    {
        return (ushort)(buf[offset] | (buf[offset + 1] << 8));
    }

    public static byte[] ClientHelloMsg(byte role)
    {
        return CreateMessage(ClientHello, new byte[] { role });
    }

    public static byte[] ClientIdMsg(int clientId)
    {
        return CreateMessage(ClientId, BitConverter.GetBytes(clientId));
    }

    public static byte[] CreateRoomMsg(string roomName)
    {
        byte[] nameBytes = Encoding.UTF8.GetBytes(roomName);
        byte[] payload = new byte[2 + nameBytes.Length];
        WriteUShort(payload, 0, (ushort)nameBytes.Length);
        Buffer.BlockCopy(nameBytes, 0, payload, 2, nameBytes.Length);
        return CreateMessage(CreateRoom, payload);
    }

    public static string ReadCreateRoomPayload(ArraySegment<byte> data)
    {
        int off = data.Offset + 1;
        ushort len = ReadUShort(data.Array, off);
        return Encoding.UTF8.GetString(data.Array, off + 2, len);
    }

    public static byte[] RoomCreatedMsg(int roomId)
    {
        return CreateMessage(RoomCreated, BitConverter.GetBytes(roomId));
    }

    public static byte[] RequestRoomListMsg()
    {
        return CreateMessage(RequestRoomList, null);
    }

    public static byte[] JoinRoomMsg(int roomId)
    {
        return CreateMessage(JoinRoom, BitConverter.GetBytes(roomId));
    }

    public static byte[] JoinAcceptedMsg(int roomId, byte canvasCount)
    {
        byte[] payload = new byte[5];
        BitConverter.GetBytes(roomId).CopyTo(payload, 0);
        payload[4] = canvasCount;
        return CreateMessage(JoinAccepted, payload);
    }

    public static byte[] LeaveRoomMsg()
    {
        return CreateMessage(LeaveRoom, null);
    }

    public static byte[] RoomUsersChangedMsg(int[] userIds)
    {
        int count = userIds != null ? userIds.Length : 0;
        byte[] payload = new byte[4 + count * 4];
        BitConverter.GetBytes(count).CopyTo(payload, 0);
        for (int i = 0; i < count; i++)
            BitConverter.GetBytes(userIds[i]).CopyTo(payload, 4 + i * 4);
        return CreateMessage(RoomUsersChanged, payload);
    }

    // ── CanvasFrame 消息: type=CanvasDirty(0x40) 或 CanvasKey(0x41), payload=[canvasId:1B][framePacket:N]
    public static byte[] CanvasFrameMsg(byte type, byte canvasId, byte[] framePacket)
    {
        byte[] payload = new byte[1 + framePacket.Length];
        payload[0] = canvasId;
        Buffer.BlockCopy(framePacket, 0, payload, 1, framePacket.Length);
        return CreateMessage(type, payload);
    }

    public static void ReadCanvasFrame(ArraySegment<byte> data, out byte canvasId, out byte[] framePacket)
    {
        int off = data.Offset + 1;
        canvasId = data.Array[off];
        int frameLen = data.Count - 2; // type(1) + canvasId(1) = 2 bytes header
        framePacket = new byte[frameLen];
        Buffer.BlockCopy(data.Array, off + 1, framePacket, 0, frameLen);
    }

    // ── DrawingCmd: [canvasId:1B][userId:4B][rgba:4B][brushSize:4B][pointCount:4B][points:N]
    public static byte[] DrawingCmdMsg(byte canvasId, int userId, byte r, byte g, byte b, byte a,
        float brushSize, float[] points) // points alternate x,y,x,y...
    {
        int ptCount = points.Length / 2;
        byte[] payload = new byte[1 + 4 + 4 + 4 + 4 + ptCount * 8];
        int off = 0;
        payload[off++] = canvasId;
        BitConverter.GetBytes(userId).CopyTo(payload, off); off += 4;
        payload[off++] = r;
        payload[off++] = g;
        payload[off++] = b;
        payload[off++] = a;
        BitConverter.GetBytes(brushSize).CopyTo(payload, off); off += 4;
        BitConverter.GetBytes(ptCount).CopyTo(payload, off); off += 4;
        for (int i = 0; i < ptCount; i++)
        {
            BitConverter.GetBytes(points[i * 2]).CopyTo(payload, off); off += 4;
            BitConverter.GetBytes(points[i * 2 + 1]).CopyTo(payload, off); off += 4;
        }
        return CreateMessage(DrawingCmd, payload);
    }

    public static void ReadDrawingCmd(ArraySegment<byte> data,
        out byte canvasId, out int userId, out byte r, out byte g, out byte b, out byte a,
        out float brushSize, out float[] points)
    {
        int off = data.Offset + 1;
        canvasId = data.Array[off]; off += 1;
        userId = BitConverter.ToInt32(data.Array, off); off += 4;
        r = data.Array[off++];
        g = data.Array[off++];
        b = data.Array[off++];
        a = data.Array[off++];
        brushSize = BitConverter.ToSingle(data.Array, off); off += 4;
        int ptCount = BitConverter.ToInt32(data.Array, off); off += 4;
        points = new float[ptCount * 2];
        for (int i = 0; i < ptCount * 2; i++)
        {
            points[i] = BitConverter.ToSingle(data.Array, off);
            off += 4;
        }
    }

    // ── RoomList: [count:4B][{roomId:4B, nameLen:2B, name:UTF8, userCount:4B}]×N
    public static byte[] RoomListMsg(List<RoomInfo> rooms)
    {
        int total = 4;
        foreach (RoomInfo r in rooms)
            total += 4 + 2 + Encoding.UTF8.GetByteCount(r.name) + 4;

        byte[] payload = new byte[total];
        int off = 0;
        BitConverter.GetBytes(rooms.Count).CopyTo(payload, off); off += 4;
        foreach (RoomInfo r in rooms)
        {
            BitConverter.GetBytes(r.roomId).CopyTo(payload, off); off += 4;
            byte[] nb = Encoding.UTF8.GetBytes(r.name);
            WriteUShort(payload, off, (ushort)nb.Length); off += 2;
            Buffer.BlockCopy(nb, 0, payload, off, nb.Length); off += nb.Length;
            BitConverter.GetBytes(r.userCount).CopyTo(payload, off); off += 4;
        }
        return CreateMessage(RoomList, payload);
    }

    public static void ReadRoomList(ArraySegment<byte> data, List<RoomInfo> outList)
    {
        outList.Clear();
        int off = data.Offset + 1;
        int count = BitConverter.ToInt32(data.Array, off); off += 4;
        for (int i = 0; i < count; i++)
        {
            int rid = BitConverter.ToInt32(data.Array, off); off += 4;
            ushort nl = ReadUShort(data.Array, off); off += 2;
            string name = Encoding.UTF8.GetString(data.Array, off, nl); off += nl;
            int uc = BitConverter.ToInt32(data.Array, off); off += 4;
            outList.Add(new RoomInfo { roomId = rid, name = name, userCount = uc });
        }
    }
}

public struct RoomInfo
{
    public int roomId;
    public string name;
    public int userCount;
}
