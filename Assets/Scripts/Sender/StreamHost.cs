using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using UnityEngine;

public class StreamHost : MonoBehaviour
{
    public int port = 7777;
    public ComputeShader tileHashShader;

    private TcpListener _listener;
    private List<TcpClient> _clients = new List<TcpClient>();
    private TileDiffer _tileDiffer;
    private Thread _acceptThread;
    private bool _running;
    private int _texWidth, _texHeight;

    private byte[] _prefixedBuffer;
    private int _prefixedSize;

    public int ClientCount
    {
        get { lock (_clients) return _clients.Count; }
    }

    void Start()
    {
        DrawingCanvas canvas = FindObjectOfType<DrawingCanvas>();
        _texWidth = SceneConfig.TextureSize;
        _texHeight = SceneConfig.TextureSize;
        _tileDiffer = new TileDiffer(canvas.CanvasTexture, tileHashShader);

        _listener = new TcpListener(IPAddress.Any, port);
        _listener.Start();
        _running = true;
        _acceptThread = new Thread(AcceptLoop) { IsBackground = true };
        _acceptThread.Start();
    }

    void Update()
    {
        _tileDiffer.Update();

        if (!_tileDiffer.TryGetDirtyTiles(out List<DirtyTile> dirtyTiles)) return;
        if (dirtyTiles.Count == 0) return;

        byte[] packet = FrameCodec.EncodeDeltaFrame(dirtyTiles);
        SendToAll(packet);
    }

    void AcceptLoop()
    {
        while (_running)
        {
            try
            {
                TcpClient client = _listener.AcceptTcpClient();
                lock (_clients)
                {
                    _clients.Add(client);
                }

                byte[] lastData = _tileDiffer.LatestRawData;
                if (lastData != null)
                {
                    byte[] keyFrame = FrameCodec.EncodeKeyFrame(_texWidth, _texHeight, lastData);
                    SendToOne(client, keyFrame);
                }
            }
            catch (SocketException)
            {
                break;
            }
            catch (ObjectDisposedException)
            {
                break;
            }
        }
    }

    void BuildPrefixedBuffer(byte[] data)
    {
        int needed = 4 + data.Length;
        if (_prefixedSize < needed)
        {
            _prefixedBuffer = new byte[needed];
            _prefixedSize = needed;
        }
        BitConverter.GetBytes(data.Length).CopyTo(_prefixedBuffer, 0);
        Buffer.BlockCopy(data, 0, _prefixedBuffer, 4, data.Length);
    }

    void SendToAll(byte[] data)
    {
        BuildPrefixedBuffer(data);

        lock (_clients)
        {
            for (int i = _clients.Count - 1; i >= 0; i--)
            {
                try
                {
                    _clients[i].GetStream().Write(_prefixedBuffer, 0, _prefixedSize);
                }
                catch
                {
                    _clients[i].Close();
                    _clients.RemoveAt(i);
                }
            }
        }
    }

    void SendToOne(TcpClient client, byte[] data)
    {
        try
        {
            BuildPrefixedBuffer(data);
            client.GetStream().Write(_prefixedBuffer, 0, _prefixedSize);
        }
        catch { }
    }

    void OnDestroy()
    {
        _running = false;
        _listener?.Stop();

        lock (_clients)
        {
            foreach (TcpClient c in _clients) c.Close();
            _clients.Clear();
        }

        _acceptThread?.Join(1000);
        _tileDiffer.Release();
    }
}
