using System;
using System.Collections.Generic;
using System.Threading;

enum ClientRole { HC = 1, UC = 2 }

class ClientSession
{
    public int clientId;
    public ClientRole role;
    public int? roomId;
    public string roomName;
}

class Room
{
    public int roomId;
    public string name;
    public int hcClientId;
    public HashSet<int> ucClientIds = new HashSet<int>();
    public int UserCount => ucClientIds.Count;
}

class Program
{
    static Telepathy.Server _server;
    static Dictionary<int, ClientSession> _clients = new Dictionary<int, ClientSession>();
    static Dictionary<int, Room> _rooms = new Dictionary<int, Room>();
    static int _nextRoomId = 1;
    static int _nextClientId = 1;

    static void Main(string[] args)
    {
        int port = args.Length > 0 && int.TryParse(args[0], out int p) ? p : 7777;

        _server = new Telepathy.Server(256 * 1024);
        _server.OnConnected = OnConnected;
        _server.OnData = OnData;
        _server.OnDisconnected = OnDisconnected;
        _server.Start(port);

        Console.WriteLine($"Server started on port {port}. Press Ctrl+C to stop.");

        try
        {
            while (true)
            {
                _server.Tick(100);
                Thread.Sleep(1);
            }
        }
        catch (Exception) { }
        finally
        {
            _server.Stop();
            Console.WriteLine("Server stopped.");
        }
    }

    static void OnConnected(int connId, string ip)
    {
        Console.WriteLine($"[CONNECT] id={connId} ip={ip}");
    }

    static void OnDisconnected(int connId)
    {
        Console.WriteLine($"[DISCONNECT] id={connId}");
        if (!_clients.TryGetValue(connId, out ClientSession session)) return;

        if (session.role == ClientRole.HC)
        {
            if (session.roomId.HasValue)
            {
                Room room = _rooms[session.roomId.Value];
                foreach (int ucId in room.ucClientIds)
                {
                    if (_clients.TryGetValue(ucId, out ClientSession uc))
                        uc.roomId = null;
                }
                _rooms.Remove(session.roomId.Value);
                Console.WriteLine($"[ROOM] room {session.roomId.Value} closed (HC disconnected)");
            }
        }
        else if (session.role == ClientRole.UC)
        {
            if (session.roomId.HasValue && _rooms.TryGetValue(session.roomId.Value, out Room room))
            {
                room.ucClientIds.Remove(connId);
                NotifyRoomUsersChanged(room);
            }
        }
        _clients.Remove(connId);
    }

    static void OnData(int connId, ArraySegment<byte> data)
    {
        if (data.Count < 1) return;
        byte type = data.Array[data.Offset];

        switch (type)
        {
            case Protocol.ClientHello:
                HandleClientHello(connId, data);
                break;

            case Protocol.CreateRoom:
                HandleCreateRoom(connId, data);
                break;

            case Protocol.RequestRoomList:
                HandleRequestRoomList(connId);
                break;

            case Protocol.JoinRoom:
                HandleJoinRoom(connId, data);
                break;

            case Protocol.LeaveRoom:
                HandleLeaveRoom(connId);
                break;

            case Protocol.DrawingCmd:
                ForwardToHC(connId, data);
                break;

            case Protocol.CanvasDirty:
            case Protocol.CanvasKey:
                BroadcastToRoomUCs(connId, data);
                break;
        }
    }

    static void HandleClientHello(int connId, ArraySegment<byte> data)
    {
        if (data.Count < 2) return;
        byte role = data.Array[data.Offset + 1];
        if (role != (byte)ClientRole.HC && role != (byte)ClientRole.UC) return;

        int clientId = _nextClientId++;
        ClientSession session = new ClientSession
        {
            clientId = clientId,
            role = (ClientRole)role,
        };
        _clients[connId] = session;
        _server.Send(connId, new ArraySegment<byte>(Protocol.ClientIdMsg(clientId)));
        Console.WriteLine($"[HELLO] id={connId} assigned clientId={clientId} role={(ClientRole)role}");
    }

    static void HandleCreateRoom(int connId, ArraySegment<byte> data)
    {
        if (!_clients.TryGetValue(connId, out ClientSession session) || session.role != ClientRole.HC) return;
        if (session.roomId.HasValue) return; // already has a room

        string roomName = Protocol.ReadCreateRoomPayload(data);
        int roomId = _nextRoomId++;
        Room room = new Room { roomId = roomId, name = roomName, hcClientId = connId };
        _rooms[roomId] = room;
        session.roomId = roomId;
        session.roomName = roomName;

        _server.Send(connId, new ArraySegment<byte>(Protocol.RoomCreatedMsg(roomId)));
        Console.WriteLine($"[ROOM] created id={roomId} name=\"{roomName}\" by HC connId={connId}");
    }

    static void HandleRequestRoomList(int connId)
    {
        List<RoomInfo> list = new List<RoomInfo>();
        foreach (Room r in _rooms.Values)
            list.Add(new RoomInfo { roomId = r.roomId, name = r.name, userCount = r.UserCount });
        _server.Send(connId, new ArraySegment<byte>(Protocol.RoomListMsg(list)));
    }

    static void HandleJoinRoom(int connId, ArraySegment<byte> data)
    {
        if (!_clients.TryGetValue(connId, out ClientSession session) || session.role != ClientRole.UC) return;
        if (session.roomId.HasValue) return; // already in a room

        int roomId = BitConverter.ToInt32(data.Array, data.Offset + 1);
        if (!_rooms.TryGetValue(roomId, out Room room)) return;

        room.ucClientIds.Add(connId);
        session.roomId = roomId;

        _server.Send(connId, new ArraySegment<byte>(Protocol.JoinAcceptedMsg(roomId, 2)));
        NotifyRoomUsersChanged(room);
        Console.WriteLine($"[JOIN] UC connId={connId} joined room {roomId} \"{room.name}\"");
    }

    static void HandleLeaveRoom(int connId)
    {
        if (!_clients.TryGetValue(connId, out ClientSession session)) return;
        if (!session.roomId.HasValue) return;
        if (!_rooms.TryGetValue(session.roomId.Value, out Room room)) return;

        room.ucClientIds.Remove(connId);
        session.roomId = null;
        NotifyRoomUsersChanged(room);
        Console.WriteLine($"[LEAVE] UC connId={connId} left room {room.roomId}");
    }

    static void ForwardToHC(int connId, ArraySegment<byte> data)
    {
        if (!_clients.TryGetValue(connId, out ClientSession session)) return;
        if (!session.roomId.HasValue) return;
        if (!_rooms.TryGetValue(session.roomId.Value, out Room room)) return;
        _server.Send(room.hcClientId, data);
    }

    static void BroadcastToRoomUCs(int connId, ArraySegment<byte> data)
    {
        if (!_clients.TryGetValue(connId, out ClientSession session)) return;
        if (!session.roomId.HasValue) return;
        if (!_rooms.TryGetValue(session.roomId.Value, out Room room)) return;
        foreach (int ucId in room.ucClientIds)
            _server.Send(ucId, data);
    }

    static void NotifyRoomUsersChanged(Room room)
    {
        int[] userIds = new int[room.ucClientIds.Count];
        int i = 0;
        foreach (int ucId in room.ucClientIds)
            userIds[i++] = ucId;
        byte[] msg = Protocol.RoomUsersChangedMsg(userIds);
        _server.Send(room.hcClientId, new ArraySegment<byte>(msg));
        foreach (int ucId in room.ucClientIds)
            _server.Send(ucId, new ArraySegment<byte>(msg));
    }
}
