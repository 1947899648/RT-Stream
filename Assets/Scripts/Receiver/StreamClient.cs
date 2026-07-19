using System;
using System.Collections.Generic;
using UnityEngine;

public class StreamClient : MonoBehaviour
{
    [SerializeField] private ComputeShader _tileApplyShader;
    [SerializeField] private DrawableSurface _surface3D;
    [SerializeField] private DrawableSurface _surfaceUI;

    private Telepathy.Client _client;
    private int _myClientId;
    private bool _connected;
    private bool _inRoom;
    private int _myRoomId;

    private RenderTexture _displayRT_3D, _displayRT_UI;
    private ComputeBuffer _payloadBuffer_3D, _payloadBuffer_UI;
    private int _kApplyFull, _kApplyDelta;
    private int _texWidth, _texHeight;
    private bool _initialized3D, _initializedUI;

    private List<byte[]> _batch3D = new List<byte[]>();
    private List<byte[]> _batchUI = new List<byte[]>();

    private BandwidthMeter _downRecvBandwidth = new BandwidthMeter();
    private BandwidthMeter _downProcBandwidth = new BandwidthMeter();

    public event Action<List<RoomInfo>> OnRoomListReceived;
    public event Action OnRoomJoined;
    public event Action OnRoomLeft;

    public bool IsConnected => _connected;
    public bool InRoom => _inRoom;
    public int MyRoomId => _myRoomId;
    public RenderTexture DisplayRT_3D => _displayRT_3D;
    public RenderTexture DisplayRT_UI => _displayRT_UI;
    public float DownRecvMBps => _downRecvBandwidth.MBps;
    public float DownProcMBps => _downProcBandwidth.MBps;

    public int LastBatchSize => _batch3D.Count + _batchUI.Count;
    public int SkippedFrames => 0;
    public int DirtyTilesReceived { get; private set; }
    public float NetLagMs => 0f;
    public float LocalLagMs => 0f;
    public float SilenceMs => 0f;

    void Awake()
    {
        _kApplyFull = _tileApplyShader.FindKernel("KApplyFull");
        _kApplyDelta = _tileApplyShader.FindKernel("KApplyDelta");

        int size = SceneConfig.TextureSize;
        _texWidth = size;
        _texHeight = size;

        _displayRT_3D = CreateDisplayRT(size);
        _displayRT_UI = CreateDisplayRT(size);

        if (_surface3D != null)
        {
            _surface3D.Initialize(_displayRT_3D);
            _surface3D.IsAuthoritative = false;
            _surface3D.OnUserDraw += (c, s, pts) => SendDrawingCmd(0, c, s, pts);
        }
        if (_surfaceUI != null)
        {
            _surfaceUI.Initialize(_displayRT_UI);
            _surfaceUI.IsAuthoritative = false;
            _surfaceUI.OnUserDraw += (c, s, pts) => SendDrawingCmd(1, c, s, pts);
        }
    }

    public void Connect()
    {
        Disconnect();
        _initialized3D = false;
        _initializedUI = false;
        _downRecvBandwidth.Reset();
        _downProcBandwidth.Reset();

        _client = new Telepathy.Client(256 * 1024);
        _client.OnConnected = () =>
        {
            _connected = true;
            _client.Send(new ArraySegment<byte>(Protocol.ClientHelloMsg(Protocol.RoleUC)));
        };
        _client.OnData = OnData;
        _client.OnDisconnected = () => _connected = false;
        _client.Connect(SceneConfig.ServerIP, SceneConfig.Port);
    }

    public void Disconnect()
    {
        _connected = false;
        _inRoom = false;
        _client?.Disconnect();
        _batch3D.Clear();
        _batchUI.Clear();
    }

    public void JoinRoom(int roomId)
    {
        if (!_connected) return;
        _client.Send(new ArraySegment<byte>(Protocol.JoinRoomMsg(roomId)));
    }

    public void LeaveRoom()
    {
        if (!_inRoom) return;
        _client.Send(new ArraySegment<byte>(Protocol.LeaveRoomMsg()));
        _inRoom = false;
        _batch3D.Clear();
        _batchUI.Clear();
        OnRoomLeft?.Invoke();
    }

    public void SendDrawingCmd(byte canvasId, Color32 color, float size, Vector2[] points)
    {
        if (!_connected || !_inRoom) return;

        float[] flatPts = new float[points.Length * 2];
        for (int i = 0; i < points.Length; i++)
        {
            flatPts[i * 2] = points[i].x;
            flatPts[i * 2 + 1] = points[i].y;
        }

        byte[] msg = Protocol.DrawingCmdMsg(canvasId, _myClientId,
            color.r, color.g, color.b, color.a, size, flatPts);
        _client.Send(new ArraySegment<byte>(msg));
    }

    void OnData(ArraySegment<byte> data)
    {
        byte type = Protocol.GetMessageType(data);
        switch (type)
        {
            case Protocol.ClientId:
                _myClientId = BitConverter.ToInt32(data.Array, data.Offset + 1);
                _client.Send(new ArraySegment<byte>(Protocol.RequestRoomListMsg()));
                break;

            case Protocol.RoomList:
            {
                List<RoomInfo> rooms = new List<RoomInfo>();
                Protocol.ReadRoomList(data, rooms);
                OnRoomListReceived?.Invoke(rooms);
                break;
            }

            case Protocol.JoinAccepted:
                _inRoom = true;
                _myRoomId = BitConverter.ToInt32(data.Array, data.Offset + 1);
                _initialized3D = false;
                _initializedUI = false;
                _batch3D.Clear();
                _batchUI.Clear();
                OnRoomJoined?.Invoke();
                break;

            case Protocol.RoomUsersChanged:
                break;

            case Protocol.CanvasDirty:
            case Protocol.CanvasKey:
            {
                Protocol.ReadCanvasFrame(data, out byte cid, out byte[] framePkt);
                if (cid == 0) _batch3D.Add(framePkt);
                else _batchUI.Add(framePkt);
                _downRecvBandwidth.Add(data.Count);
                break;
            }
        }
    }

    void Update()
    {
        _client?.Tick(100);
        _downRecvBandwidth.Sample();
        _downProcBandwidth.Sample();

        ProcessBatch(_batch3D, _displayRT_3D, _payloadBuffer_3D, ref _initialized3D);
        ProcessBatch(_batchUI, _displayRT_UI, _payloadBuffer_UI, ref _initializedUI);
    }

    void ProcessBatch(List<byte[]> batch, RenderTexture rt, ComputeBuffer payloadBuf, ref bool initialized)
    {
        if (batch.Count == 0) return;

        int startIdx = 0;
        for (int i = batch.Count - 1; i >= 0; i--)
        {
            if (FrameCodec.GetFrameType(batch[i]) == FrameType.KeyFrame)
            {
                startIdx = i;
                break;
            }
        }

        for (int i = startIdx; i < batch.Count; i++)
        {
            byte[] pkt = batch[i];

            if (FrameCodec.IsCompressed(pkt))
                pkt = UncompressPacket(pkt);

            ApplyPacket(pkt, rt, ref payloadBuf, ref initialized);
        }

        batch.Clear();
    }

    void ApplyPacket(byte[] packet, RenderTexture rt, ref ComputeBuffer payloadBuf, ref bool initialized)
    {
        _downProcBandwidth.Add(packet.Length);

        FrameType type = FrameCodec.GetFrameType(packet);

        if (type == FrameType.KeyFrame)
        {
            int width = BitConverter.ToInt32(packet, FrameCodec.HeaderSize);
            int height = BitConverter.ToInt32(packet, FrameCodec.HeaderSize + 4);

            if (width != rt.width || height != rt.height)
            {
                ReleasePayloadBuffer(ref payloadBuf);
                return;
            }

            EnsurePayloadBuffer(ref payloadBuf, width, height);
            int pixelBytes = width * height * 4;
            payloadBuf.SetData(packet, FrameCodec.HeaderSize + 8, 0, pixelBytes);

            SetGpuParams(width, height);
            _tileApplyShader.SetBuffer(_kApplyFull, "_Payload", payloadBuf);
            _tileApplyShader.SetTexture(_kApplyFull, "_OutRT", rt);
            _tileApplyShader.Dispatch(_kApplyFull,
                Mathf.CeilToInt(width / 8f), Mathf.CeilToInt(height / 8f), 1);

            _texWidth = width;
            _texHeight = height;
            initialized = true;
        }
        else if (type == FrameType.DeltaFrame)
        {
            if (!initialized) return;

            int tileCount = BitConverter.ToUInt16(packet, 1);
            if (tileCount == 0) return;

            int payloadLen = packet.Length - FrameCodec.HeaderSize;
            EnsurePayloadBuffer(ref payloadBuf, _texWidth, _texHeight);
            if (payloadLen > payloadBuf.count * 4) return;

            payloadBuf.SetData(packet, FrameCodec.HeaderSize, 0, payloadLen);

            SetGpuParams(_texWidth, _texHeight);
            _tileApplyShader.SetBuffer(_kApplyDelta, "_Payload", payloadBuf);
            _tileApplyShader.SetTexture(_kApplyDelta, "_OutRT", rt);
            _tileApplyShader.Dispatch(_kApplyDelta, tileCount, 1, 1);
        }
    }

    byte[] UncompressPacket(byte[] packet)
    {
        int comprLen = BitConverter.ToInt32(packet, 3);
        byte[] comprBlock = new byte[comprLen];
        Buffer.BlockCopy(packet, FrameCodec.HeaderSize, comprBlock, 0, comprLen);
        byte[] payload = LZ4.Unwrap(comprBlock, out int comprSz, out int decompSz);
        FrameCodec.LastDecodeComprBytes = comprSz;
        FrameCodec.LastDecodeDecompBytes = decompSz;

        byte[] newPacket = new byte[FrameCodec.HeaderSize + payload.Length];
        Buffer.BlockCopy(packet, 0, newPacket, 0, FrameCodec.HeaderSize);

        ushort flags = BitConverter.ToUInt16(packet, 1);
        flags &= 0x7FFF;
        BitConverter.GetBytes(flags).CopyTo(newPacket, 1);
        BitConverter.GetBytes((uint)payload.Length).CopyTo(newPacket, 3);
        Buffer.BlockCopy(payload, 0, newPacket, FrameCodec.HeaderSize, payload.Length);
        return newPacket;
    }

    void SetGpuParams(int width, int height)
    {
        int tileSize = SceneConfig.TileSize;
        if (tileSize > width) tileSize = width;

        _tileApplyShader.SetInt("_TexWidth", width);
        _tileApplyShader.SetInt("_TexHeight", height);
        _tileApplyShader.SetInt("_TileSize", tileSize);
        _tileApplyShader.SetInt("_TilesX", width / tileSize);
        _tileApplyShader.SetInt("_FlipY", 0);
    }

    void EnsurePayloadBuffer(ref ComputeBuffer buf, int width, int height)
    {
        int tileSize = SceneConfig.TileSize;
        if (tileSize > width) tileSize = width;
        int tileBytes = tileSize * tileSize * 4;
        int tileCount = (width / tileSize) * (height / tileSize);
        int capBytes = tileCount * (4 + tileBytes);

        if (buf != null && buf.count * 4 >= capBytes) return;

        ReleasePayloadBuffer(ref buf);
        buf = new ComputeBuffer(capBytes / 4, 4, ComputeBufferType.Raw);
    }

    void ReleasePayloadBuffer(ref ComputeBuffer buf)
    {
        buf?.Release();
        buf = null;
    }

    RenderTexture CreateDisplayRT(int size)
    {
        return new RenderTexture(size, size, 0, RenderTextureFormat.ARGB32)
        {
            enableRandomWrite = true
        };
    }

    void OnDestroy()
    {
        Disconnect();
        ReleasePayloadBuffer(ref _payloadBuffer_3D);
        ReleasePayloadBuffer(ref _payloadBuffer_UI);
        if (_displayRT_3D != null) _displayRT_3D.Release();
        if (_displayRT_UI != null) _displayRT_UI.Release();
    }
}
