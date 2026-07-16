using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Net.Sockets;
using System.Text;
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

    private byte[] _recvBuffer;
    private int _recvSize;

    void Awake()
    {
        hostIP = SceneConfig.HostIP;
        port = SceneConfig.Port;
    }

    public bool IsConnected => _connected;

    public void Connect()
    {
        Disconnect();
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
    }

    void Update()
    {
        if (!_connected) return;

        while (_frameQueue.TryDequeue(out byte[] packet))
        {
            ProcessFrame(packet);
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

                if (_recvSize < frameLen)
                {
                    _recvBuffer = new byte[frameLen];
                    _recvSize = frameLen;
                }

                if (!ReadExact(_stream, _recvBuffer, 0, frameLen)) break;

                byte[] frameData = new byte[frameLen];
                Buffer.BlockCopy(_recvBuffer, 0, frameData, 0, frameLen);
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

    void ProcessFrame(byte[] packet)
    {
        FrameType type = FrameCodec.GetFrameType(packet);
        if (type == FrameType.KeyFrame)
        {
            FrameCodec.DecodeKeyFrame(packet, out _texWidth, out _texHeight, out byte[] pixels);
            _tileBuffer = pixels;

            if (_tex2D != null) Destroy(_tex2D);
            _tex2D = new Texture2D(_texWidth, _texHeight, TextureFormat.RGBA32, false);
            _initialized = true;
        }
        else if (type == FrameType.DeltaFrame)
        {
            if (!_initialized) return;
            List<DirtyTile> tiles = FrameCodec.DecodeDeltaFrame(packet);
            int rowLen = _texWidth * 4;

            foreach (DirtyTile tile in tiles)
            {
                int tileX = (tile.index % (_texWidth / 16)) * 16;
                int tileY = (tile.index / (_texWidth / 16)) * 16;

                for (int y = 0; y < 16; y++)
                {
                    int dstOffset = ((tileY + y) * rowLen) + tileX * 4;
                    Buffer.BlockCopy(tile.data, y * 16 * 4, _tileBuffer, dstOffset, 16 * 4);
                }
            }
        }

        if (_initialized && _tileBuffer != null)
        {
            _tex2D.LoadRawTextureData(_tileBuffer);
            _tex2D.Apply();
            Graphics.Blit(_tex2D, SceneConfig.DisplayRT);
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
