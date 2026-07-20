using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using Stopwatch = System.Diagnostics.Stopwatch;
using System.Net.Sockets;
using System.Threading;
using UnityEngine;

public class StreamClient : MonoBehaviour
{
    [SerializeField] private ComputeShader _tileApplyShader;

    private struct FrameEntry
    {
        public byte[] data;
        public long recvTimestamp;
        public float netLagMs;
    }

    private TcpClient _tcpClient;
    private NetworkStream _stream;
    private Thread _receiveThread;
    private ConcurrentQueue<FrameEntry> _frameQueue = new ConcurrentQueue<FrameEntry>();
    private bool _connected;
    private bool _running;

    private bool _initialized;
    private int _texWidth, _texHeight;
    private List<byte[]> _batch = new List<byte[]>();
    private int _lastBatchSize;

    private ComputeBuffer _payloadBuffer;
    private int _kApplyDelta;

    private float _netLagMs;
    private float _localLagMs;
    private long _lastRecvWatchTimestamp;

    private BandwidthMeter _downRecvBandwidth = new BandwidthMeter();
    private BandwidthMeter _downProcBandwidth = new BandwidthMeter();
    public float DownRecvMBps => _downRecvBandwidth.MBps;
    public float DownProcMBps => _downProcBandwidth.MBps;

    public event System.Action<int[]> OnDirtyTilesApplied;

    public bool IsConnected => _connected;
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
        _lastBatchSize = 0;
        _downRecvBandwidth.Reset();
        _downProcBandwidth.Reset();

        _kApplyDelta = _tileApplyShader.FindKernel("KApplyDelta");

        try
        {
            _tcpClient = new TcpClient();
            _tcpClient.Connect(SceneConfig.HostIP, SceneConfig.Port);
            _stream = _tcpClient.GetStream();
            _connected = true;
            _running = true;
            _texWidth = SceneConfig.TextureSize;
            _texHeight = SceneConfig.TextureSize;
            _initialized = true;
            _receiveThread = new Thread(ReceiveLoop) { IsBackground = true };
            _receiveThread.Start();
        }
        catch (Exception e)
        {
            Debug.LogError($"StreamClient connect failed: {e.Message}");
            Close();
        }
    }

    public void Disconnect()
    {
        _running = false;
        _connected = false;
        _initialized = false;
        _receiveThread?.Join(500);
        Close();
        while (_frameQueue.TryDequeue(out FrameEntry _)) { }
        _batch.Clear();
        ReleasePayloadBuffer();
    }

    void Update()
    {
        if (!_connected) return;

        _downRecvBandwidth.Sample();
        _downProcBandwidth.Sample();

        while (_frameQueue.TryDequeue(out FrameEntry entry))
        {
            _batch.Add(entry.data);
            _netLagMs = entry.netLagMs;
            long now = Stopwatch.GetTimestamp();
            _localLagMs = (now - entry.recvTimestamp) * 1000f / Stopwatch.Frequency;
            _lastRecvWatchTimestamp = now;
        }
        if (_batch.Count == 0) return;

        _lastBatchSize = _batch.Count;

        int dirtyReceived = 0;
        List<int> collectedIndices = new List<int>();
        foreach (byte[] pkt in _batch)
        {
            byte[] raw = FrameCodec.IsCompressed(pkt) ? UncompressPacket(pkt) : pkt;
            ApplyPacket(raw);
            ushort tc = BitConverter.ToUInt16(raw, 1);
            dirtyReceived += tc;
            int pos = FrameCodec.HeaderSize;
            int tileBytes = SceneConfig.TileSize * SceneConfig.TileSize * 4;
            for (int j = 0; j < tc; j++)
            {
                collectedIndices.Add(BitConverter.ToInt32(raw, pos));
                pos += 4 + tileBytes;
            }
        }
        DirtyTilesReceived = dirtyReceived;

        if (collectedIndices.Count > 0)
        {
            int[] arr = new int[collectedIndices.Count];
            collectedIndices.CopyTo(arr);
            OnDirtyTilesApplied?.Invoke(arr);
        }

        _batch.Clear();
    }

    void ReceiveLoop()
    {
        byte[] lenBuf = new byte[4];
        while (_running && _tcpClient != null && _tcpClient.Connected)
        {
            try
            {
                if (!ReadExact(_stream, lenBuf, 0, 4)) break;
                int frameLen = BitConverter.ToInt32(lenBuf, 0);
                if (frameLen <= 0) break;

                byte[] frameData = new byte[frameLen];
                if (!ReadExact(_stream, frameData, 0, frameLen)) break;
                _downRecvBandwidth.Add(frameLen);
                long sendTicks = FrameCodec.GetTimestamp(frameData);
                float netLagMs = (DateTime.UtcNow.Ticks - sendTicks) / (float)TimeSpan.TicksPerMillisecond;
                _frameQueue.Enqueue(new FrameEntry { data = frameData, recvTimestamp = Stopwatch.GetTimestamp(), netLagMs = netLagMs });
            }
            catch
            {
                break;
            }
        }
        _connected = false;
    }

    bool ReadExact(NetworkStream s, byte[] buf, int offset, int count)
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

    void ApplyPacket(byte[] packet)
    {
        _downProcBandwidth.Add(packet.Length);
        TryApplyGpu(packet);
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

    bool TryApplyGpu(byte[] packet)
    {
        RenderTexture rt = SceneConfig.DisplayRT;
        if (rt == null || !rt.enableRandomWrite)
        {
            ReleasePayloadBuffer();
            return false;
        }

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

    void Close()
    {
        _stream?.Close();
        _tcpClient?.Close();
        _stream = null;
        _tcpClient = null;
    }

    void OnDestroy()
    {
        Disconnect();
        ReleasePayloadBuffer();
    }
}
