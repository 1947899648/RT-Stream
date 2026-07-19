using System;
using System.Collections.Generic;
using Stopwatch = System.Diagnostics.Stopwatch;
using UnityEngine;

public class StreamClient : MonoBehaviour
{
    [SerializeField] private ComputeShader _tileApplyShader;

    private struct FrameEntry
    {
        public byte[] data;
        public long recvTimestamp;
    }

    private Telepathy.Client _client;
    private List<FrameEntry> _batch = new List<FrameEntry>();
    private bool _connected;
    private bool _initialized;
    private int _texWidth, _texHeight;
    private int _skippedFrames;
    private int _lastBatchSize;

    private ComputeBuffer _payloadBuffer;
    private int _kApplyFull, _kApplyDelta;

    private float _netLagMs;
    private float _localLagMs;
    private long _lastRecvWatchTimestamp;

    private BandwidthMeter _downRecvBandwidth = new BandwidthMeter();
    private BandwidthMeter _downProcBandwidth = new BandwidthMeter();
    public float DownRecvMBps => _downRecvBandwidth.MBps;
    public float DownProcMBps => _downProcBandwidth.MBps;

    public event System.Action<int[]> OnDirtyTilesApplied;

    public bool IsConnected => _connected;
    public int SkippedFrames => _skippedFrames;
    public int LastBatchSize => _lastBatchSize;
    public int DirtyTilesReceived { get; private set; }
    public float NetLagMs => _netLagMs;
    public float LocalLagMs => _localLagMs;
    public float SilenceMs
    {
        get
        {
            if (_lastRecvWatchTimestamp == 0) return 0;
            return (Stopwatch.GetTimestamp() - _lastRecvWatchTimestamp) * 1000f / Stopwatch.Frequency;
        }
    }

    public void Connect()
    {
        Disconnect();
        _skippedFrames = 0;
        _lastBatchSize = 0;
        _texWidth = SceneConfig.TextureSize;
        _texHeight = SceneConfig.TextureSize;
        _initialized = true;
        _downRecvBandwidth.Reset();
        _downProcBandwidth.Reset();

        _kApplyFull = _tileApplyShader.FindKernel("KApplyFull");
        _kApplyDelta = _tileApplyShader.FindKernel("KApplyDelta");

        int maxMsgSize = 256 * 1024;
        _client = new Telepathy.Client(maxMsgSize);
        _client.OnConnected = () => _connected = true;
        _client.OnData = (data) =>
        {
            byte[] copy = new byte[data.Count];
            Buffer.BlockCopy(data.Array, data.Offset, copy, 0, data.Count);
            _batch.Add(new FrameEntry { data = copy, recvTimestamp = Stopwatch.GetTimestamp() });
            _downRecvBandwidth.Add(data.Count);
        };
        _client.OnDisconnected = () => _connected = false;
        _client.Connect(SceneConfig.HostIP, SceneConfig.Port);
    }

    public void Disconnect()
    {
        _connected = false;
        _initialized = false;
        _client?.Disconnect();
        _batch.Clear();
        ReleasePayloadBuffer();
    }

    void Update()
    {
        _client?.Tick(100);

        _downRecvBandwidth.Sample();
        _downProcBandwidth.Sample();

        if (_batch.Count == 0) return;

        _lastBatchSize = _batch.Count;

        int startIdx = 0;
        for (int i = _batch.Count - 1; i >= 0; i--)
        {
            if (FrameCodec.GetFrameType(_batch[i].data) == FrameType.KeyFrame)
            {
                startIdx = i;
                break;
            }
        }
        _skippedFrames += startIdx;

        int dirtyReceived = 0;
        List<int> collectedIndices = new List<int>();
        bool gotKeyFrame = false;

        for (int i = startIdx; i < _batch.Count; i++)
        {
            byte[] pkt = _batch[i].data;
            long recvTs = _batch[i].recvTimestamp;

            if (FrameCodec.IsCompressed(pkt))
                pkt = UncompressPacket(pkt);

            ApplyPacket(pkt);
            FrameType ft = FrameCodec.GetFrameType(pkt);
            if (ft == FrameType.KeyFrame)
            {
                gotKeyFrame = true;
            }
            else if (ft == FrameType.DeltaFrame)
            {
                ushort tc = BitConverter.ToUInt16(pkt, 1);
                dirtyReceived += tc;
                int pos = FrameCodec.HeaderSize;
                int tileBytes = SceneConfig.TileSize * SceneConfig.TileSize * 4;
                for (int j = 0; j < tc; j++)
                {
                    collectedIndices.Add(BitConverter.ToInt32(pkt, pos));
                    pos += 4 + tileBytes;
                }
            }

            long sendTicks = FrameCodec.GetTimestamp(pkt);
            _netLagMs = (DateTime.UtcNow.Ticks - sendTicks) / (float)TimeSpan.TicksPerMillisecond;
            _localLagMs = (Stopwatch.GetTimestamp() - recvTs) * 1000f / Stopwatch.Frequency;
            _lastRecvWatchTimestamp = Stopwatch.GetTimestamp();
        }
        DirtyTilesReceived = dirtyReceived;

        if (gotKeyFrame)
            OnDirtyTilesApplied?.Invoke(null);
        else if (collectedIndices.Count > 0)
        {
            int[] arr = new int[collectedIndices.Count];
            collectedIndices.CopyTo(arr);
            OnDirtyTilesApplied?.Invoke(arr);
        }

        _batch.Clear();
    }

    void ApplyPacket(byte[] packet)
    {
        _downProcBandwidth.Add(packet.Length);

        FrameType type = FrameCodec.GetFrameType(packet);
        TryApplyGpu(packet, type);
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

    bool TryApplyGpu(byte[] packet, FrameType type)
    {
        RenderTexture rt = SceneConfig.DisplayRT;
        if (rt == null || !rt.enableRandomWrite)
        {
            ReleasePayloadBuffer();
            return false;
        }

        if (type == FrameType.KeyFrame)
        {
            int width = BitConverter.ToInt32(packet, FrameCodec.HeaderSize);
            int height = BitConverter.ToInt32(packet, FrameCodec.HeaderSize + 4);

            if (width != rt.width || height != rt.height)
            {
                ReleasePayloadBuffer();
                return false;
            }

            EnsurePayloadBuffer(width, height);
            int pixelBytes = width * height * 4;
            _payloadBuffer.SetData(packet, FrameCodec.HeaderSize + 8, 0, pixelBytes);

            SetGpuParams(width, height);
            _tileApplyShader.SetBuffer(_kApplyFull, "_Payload", _payloadBuffer);
            _tileApplyShader.SetTexture(_kApplyFull, "_OutRT", rt);
            _tileApplyShader.Dispatch(_kApplyFull, Mathf.CeilToInt(width / 8f), Mathf.CeilToInt(height / 8f), 1);

            _texWidth = width;
            _texHeight = height;
            _initialized = true;
            return true;
        }

        if (type == FrameType.DeltaFrame)
        {
            if (!_initialized) return true;

            int tileCount = BitConverter.ToUInt16(packet, 1);
            if (tileCount == 0) return true;

            int payloadLen = packet.Length - FrameCodec.HeaderSize;
            EnsurePayloadBuffer(_texWidth, _texHeight);
            if (payloadLen > _payloadBuffer.count * 4) return true;

            _payloadBuffer.SetData(packet, FrameCodec.HeaderSize, 0, payloadLen);

            SetGpuParams(_texWidth, _texHeight);
            _tileApplyShader.SetBuffer(_kApplyDelta, "_Payload", _payloadBuffer);
            _tileApplyShader.SetTexture(_kApplyDelta, "_OutRT", rt);
            _tileApplyShader.Dispatch(_kApplyDelta, tileCount, 1, 1);
            return true;
        }

        return false;
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

    void EnsurePayloadBuffer(int width, int height)
    {
        int tileSize = SceneConfig.TileSize;
        if (tileSize > width) tileSize = width;
        int tileBytes = tileSize * tileSize * 4;
        int tileCount = (width / tileSize) * (height / tileSize);
        int capBytes = tileCount * (4 + tileBytes);

        if (_payloadBuffer != null && _payloadBuffer.count * 4 >= capBytes) return;

        ReleasePayloadBuffer();
        _payloadBuffer = new ComputeBuffer(capBytes / 4, 4, ComputeBufferType.Raw);
    }

    void ReleasePayloadBuffer()
    {
        _payloadBuffer?.Release();
        _payloadBuffer = null;
    }

    void OnDestroy()
    {
        Disconnect();
        ReleasePayloadBuffer();
    }
}
