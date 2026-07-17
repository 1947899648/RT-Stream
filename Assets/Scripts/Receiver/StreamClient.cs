using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Net.Sockets;
using System.Threading;
using UnityEngine;

public class StreamClient : MonoBehaviour
{
    public string hostIP;
    public int port;
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

    private Texture2D _tex2D;
    private byte[] _tileBuffer;
    private bool _initialized;
    private int _texWidth, _texHeight;
    private List<byte[]> _batch = new List<byte[]>();
    private int _skippedFrames;
    private int _lastBatchSize;

    private bool _useGpuApply;
    private ComputeBuffer _payloadBuffer;
    private int _kApplyFull, _kApplyDelta;

    private float _netLagMs;
    private float _localLagMs;
    private long _lastRecvWatchTimestamp;

    void Awake()
    {
        hostIP = SceneConfig.HostIP;
        port = SceneConfig.Port;
    }

    public bool IsConnected => _connected;
    public int SkippedFrames => _skippedFrames;
    public int LastBatchSize => _lastBatchSize;
    public string ApplyBackend => _useGpuApply ? "GPU" : "CPU";
    public int DirtyTilesReceived { get; private set; }
    public float NetLagMs => _netLagMs;
    public float LocalLagMs => _localLagMs;
    public float SilenceMs
    {
        get
        {
            if (_lastRecvWatchTimestamp == 0) return 0;
            return (System.Diagnostics.Stopwatch.GetTimestamp() - _lastRecvWatchTimestamp) * 1000f / System.Diagnostics.Stopwatch.Frequency;
        }
    }

    public void Connect()
    {
        Disconnect();
        _skippedFrames = 0;
        _lastBatchSize = 0;

        _useGpuApply = SystemInfo.supportsComputeShaders && _tileApplyShader != null;
        if (_useGpuApply)
        {
            _kApplyFull = _tileApplyShader.FindKernel("KApplyFull");
            _kApplyDelta = _tileApplyShader.FindKernel("KApplyDelta");
        }

        try
        {
            _tcpClient = new TcpClient();
            _tcpClient.Connect(hostIP, port);
            _stream = _tcpClient.GetStream();
            _connected = true;
            _running = true;
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

        while (_frameQueue.TryDequeue(out FrameEntry entry))
        {
            _batch.Add(entry.data);
            _netLagMs = entry.netLagMs;
            long now = System.Diagnostics.Stopwatch.GetTimestamp();
            _localLagMs = (now - entry.recvTimestamp) * 1000f / System.Diagnostics.Stopwatch.Frequency;
            _lastRecvWatchTimestamp = now;
        }
        if (_batch.Count == 0) return;

        _lastBatchSize = _batch.Count;

        int startIdx = 0;
        for (int i = _batch.Count - 1; i >= 0; i--)
        {
            if (FrameCodec.GetFrameType(_batch[i]) == FrameType.KeyFrame)
            {
                startIdx = i;
                break;
            }
        }
        _skippedFrames += startIdx;

        int dirtyReceived = 0;
        for (int i = startIdx; i < _batch.Count; i++)
        {
            ApplyPacket(_batch[i]);
            if (FrameCodec.GetFrameType(_batch[i]) == FrameType.DeltaFrame)
                dirtyReceived += BitConverter.ToUInt16(_batch[i], 1);
        }
        DirtyTilesReceived = dirtyReceived;

        _batch.Clear();

        if (!_useGpuApply && _initialized && _tileBuffer != null)
        {
            _tex2D.LoadRawTextureData(_tileBuffer);
            _tex2D.Apply();
            Graphics.Blit(_tex2D, SceneConfig.DisplayRT);
        }
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
                if (frameLen <= 0 || frameLen > 64 * 1024 * 1024) break;

                byte[] frameData = new byte[frameLen];
                if (!ReadExact(_stream, frameData, 0, frameLen)) break;
                long sendTicks = FrameCodec.GetTimestamp(frameData);
                float netLagMs = (DateTime.UtcNow.Ticks - sendTicks) / (float)TimeSpan.TicksPerMillisecond;
                _frameQueue.Enqueue(new FrameEntry { data = frameData, recvTimestamp = System.Diagnostics.Stopwatch.GetTimestamp(), netLagMs = netLagMs });
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
        FrameType type = FrameCodec.GetFrameType(packet);
        if (_useGpuApply && TryApplyGpu(packet, type)) return;

        if (type == FrameType.KeyFrame)
        {
            FrameCodec.DecodeKeyFrame(packet, out int width, out int height, out byte[] pixels);
            _tileBuffer = pixels;

            if (_tex2D == null || _tex2D.width != width || _tex2D.height != height)
            {
                if (_tex2D != null) Destroy(_tex2D);
                _tex2D = new Texture2D(width, height, TextureFormat.RGBA32, false);
            }
            _texWidth = width;
            _texHeight = height;
            _initialized = true;
        }
        else if (type == FrameType.DeltaFrame)
        {
            if (!_initialized) return;
            List<DirtyTile> tiles = FrameCodec.DecodeDeltaFrame(packet);
            int tileSize = SceneConfig.TileSize;
            if (tileSize > _texWidth) tileSize = _texWidth;
            int rowLen = _texWidth * 4;
            int tileRowBytes = tileSize * 4;
            int tilesPerRow = _texWidth / tileSize;

            foreach (DirtyTile tile in tiles)
            {
                int tileX = (tile.index % tilesPerRow) * tileSize;
                int tileY = (tile.index / tilesPerRow) * tileSize;

                for (int y = 0; y < tileSize; y++)
                {
                    int dstOffset = ((tileY + y) * rowLen) + tileX * 4;
                    Buffer.BlockCopy(tile.data, y * tileRowBytes, _tileBuffer, dstOffset, tileRowBytes);
                }
            }
        }
    }

    bool TryApplyGpu(byte[] packet, FrameType type)
    {
        RenderTexture rt = SceneConfig.DisplayRT;
        if (rt == null || !rt.enableRandomWrite)
        {
            _useGpuApply = false;
            ReleasePayloadBuffer();
            return false;
        }

        if (type == FrameType.KeyFrame)
        {
            int width = BitConverter.ToInt32(packet, FrameCodec.HeaderSize);
            int height = BitConverter.ToInt32(packet, FrameCodec.HeaderSize + 4);

            if (width != rt.width || height != rt.height)
            {
                _useGpuApply = false;
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
        if (_tex2D != null) Destroy(_tex2D);
        ReleasePayloadBuffer();
    }
}
