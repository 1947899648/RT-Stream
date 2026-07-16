using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using UnityEngine;

public class StreamHost : MonoBehaviour
{
    public int port = 7777;
    public int targetFps = 30;
    public int keyFrameInterval = 30;

    private TcpListener _listener;
    private List<TcpClient> _clients = new List<TcpClient>();
    private TileDiffer _tileDiffer;
    private int _seq;
    private float _timer;
    private Thread _acceptThread;
    private bool _running;
    private int _texWidth, _texHeight;

    public int ClientCount
    {
        get { lock (_clients) return _clients.Count; }
    }

    void Start()
    {
        DrawingCanvas canvas = FindObjectOfType<DrawingCanvas>();
        _texWidth = canvas.textureWidth;
        _texHeight = canvas.textureHeight;
        _tileDiffer = new TileDiffer(canvas.CanvasTexture);

        _listener = new TcpListener(IPAddress.Any, port);
        _listener.Start();
        _running = true;
        _acceptThread = new Thread(AcceptLoop) { IsBackground = true };
        _acceptThread.Start();
    }

    void Update()
    {
        _tileDiffer.Update();

        _timer += Time.deltaTime;
        float interval = 1f / targetFps;
        if (_timer < interval) return;
        _timer -= interval;

        if (!_tileDiffer.TryGetDirtyTiles(out List<DirtyTile> dirtyTiles)) return;
        if (dirtyTiles.Count == 0) return;

        byte[] packet;
        if (_seq % keyFrameInterval == 0)
        {
            packet = FrameCodec.EncodeKeyFrame(_texWidth, _texHeight, _tileDiffer.LatestRawData);
        }
        else
        {
            packet = FrameCodec.EncodeDeltaFrame(dirtyTiles);
        }

        SendToAll(packet);
        _seq++;
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

    void SendToAll(byte[] data)
    {
        byte[] prefixed = new byte[4 + data.Length];
        BitConverter.GetBytes(data.Length).CopyTo(prefixed, 0);
        Buffer.BlockCopy(data, 0, prefixed, 4, data.Length);

        lock (_clients)
        {
            for (int i = _clients.Count - 1; i >= 0; i--)
            {
                try
                {
                    _clients[i].GetStream().Write(prefixed, 0, prefixed.Length);
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
            byte[] prefixed = new byte[4 + data.Length];
            BitConverter.GetBytes(data.Length).CopyTo(prefixed, 0);
            Buffer.BlockCopy(data, 0, prefixed, 4, data.Length);
            client.GetStream().Write(prefixed, 0, prefixed.Length);
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
    }
}
