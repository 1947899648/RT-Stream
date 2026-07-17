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

    private TcpClient _tcpClient;
    private NetworkStream _stream;
    private Thread _receiveThread;
    private ConcurrentQueue<byte[]> _frameQueue = new ConcurrentQueue<byte[]>();
    private bool _connected;
    private bool _running;

    private Texture2D _tex2D;
    private byte[] _tileBuffer;
    private bool _initialized;
    private int _texWidth, _texHeight;
    private List<byte[]> _batch = new List<byte[]>();
    private int _skippedFrames;
    private int _lastBatchSize;

    void Awake()
    {
        hostIP = SceneConfig.HostIP;
        port = SceneConfig.Port;
    }

    public bool IsConnected => _connected;
    public int SkippedFrames => _skippedFrames;
    public int LastBatchSize => _lastBatchSize;

    public void Connect()
    {
        Disconnect();
        _skippedFrames = 0;
        _lastBatchSize = 0;
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
        while (_frameQueue.TryDequeue(out _)) { }
        _batch.Clear();
    }

    void Update()
    {
        if (!_connected) return;

        while (_frameQueue.TryDequeue(out byte[] packet))
            _batch.Add(packet);
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

        for (int i = startIdx; i < _batch.Count; i++)
            ApplyPacket(_batch[i]);

        _batch.Clear();

        if (_initialized && _tileBuffer != null)
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
                _frameQueue.Enqueue(frameData);
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
    }
}
