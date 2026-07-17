using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using UnityEngine;

public class StreamHost : MonoBehaviour
{
    public int port = 7777;
    public int keyFrameInterval = 30;
    [SerializeField] private int _maxQueueDepth = 8;

    private TcpListener _listener;
    private List<ClientConnection> _clients = new List<ClientConnection>();
    private object _clientsLock = new object();
    private TileDiffer _tileDiffer;
    private int _seq;
    private Thread _acceptThread;
    private volatile bool _running;
    private int _texWidth, _texHeight;

    public int ClientCount
    {
        get { lock (_clientsLock) return _clients.Count; }
    }

    public int DiagSeq => _seq;

    private class ClientConnection
    {
        public volatile bool Alive = true;
        public bool NeedKeyFrame = true;
        public int DroppedFrames;

        private TcpClient _client;
        private Thread _sendThread;
        private Queue<byte[]> _queue = new Queue<byte[]>();
        private object _lock = new object();
        private int _maxDepth;
        private byte[] _lenBuf = new byte[4];

        public ClientConnection(TcpClient client, int maxDepth)
        {
            _client = client;
            _maxDepth = maxDepth;
            try { _client.NoDelay = true; } catch { }
            _sendThread = new Thread(SendLoop) { IsBackground = true };
            _sendThread.Start();
        }

        public int QueueDepth
        {
            get { lock (_lock) return _queue.Count; }
        }

        public void Enqueue(byte[] packet, bool isKeyFrame)
        {
            lock (_lock)
            {
                if (isKeyFrame)
                {
                    _queue.Clear();
                }
                else if (_queue.Count >= _maxDepth)
                {
                    _queue.Clear();
                    NeedKeyFrame = true;
                    DroppedFrames++;
                    return;
                }
                _queue.Enqueue(packet);
                Monitor.Pulse(_lock);
            }
        }

        private void SendLoop()
        {
            try
            {
                NetworkStream stream = _client.GetStream();
                while (Alive)
                {
                    byte[] packet;
                    lock (_lock)
                    {
                        while (_queue.Count == 0)
                        {
                            if (!Alive) return;
                            Monitor.Wait(_lock);
                        }
                        packet = _queue.Dequeue();
                    }

                    int len = packet.Length;
                    _lenBuf[0] = (byte)len;
                    _lenBuf[1] = (byte)(len >> 8);
                    _lenBuf[2] = (byte)(len >> 16);
                    _lenBuf[3] = (byte)(len >> 24);
                    stream.Write(_lenBuf, 0, 4);
                    stream.Write(packet, 0, len);
                }
            }
            catch { }
            finally
            {
                Alive = false;
            }
        }

        public void Shutdown()
        {
            Alive = false;
            lock (_lock) Monitor.Pulse(_lock);
            try { _client.Close(); } catch { }
            _sendThread.Join(500);
        }
    }

    void Start()
    {
        DrawingCanvas canvas = FindObjectOfType<DrawingCanvas>();
        _texWidth = SceneConfig.TextureSize;
        _texHeight = SceneConfig.TextureSize;
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
        CleanupDeadClients();

        bool hasDirty = _tileDiffer.TryGetDirtyTiles(out List<DirtyTile> dirtyTiles) && dirtyTiles.Count > 0;
        byte[] keyPacket = null;
        byte[] deltaPacket = null;

        lock (_clientsLock)
        {
            if (hasDirty)
            {
                bool periodicKey = _seq % keyFrameInterval == 0;
                foreach (ClientConnection c in _clients)
                {
                    if (!c.Alive) continue;
                    if (periodicKey || c.NeedKeyFrame)
                    {
                        if (keyPacket == null)
                            keyPacket = FrameCodec.EncodeKeyFrame(_texWidth, _texHeight, _tileDiffer.LatestRawData);
                        c.NeedKeyFrame = false;
                        c.Enqueue(keyPacket, true);
                    }
                    else
                    {
                        if (deltaPacket == null)
                            deltaPacket = FrameCodec.EncodeDeltaFrame(dirtyTiles);
                        c.Enqueue(deltaPacket, false);
                    }
                }
                _seq++;
            }
            else
            {
                byte[] raw = _tileDiffer.LatestRawData;
                if (raw == null) return;

                foreach (ClientConnection c in _clients)
                {
                    if (!c.Alive || !c.NeedKeyFrame) continue;
                    if (keyPacket == null)
                        keyPacket = FrameCodec.EncodeKeyFrame(_texWidth, _texHeight, raw);
                    c.NeedKeyFrame = false;
                    c.Enqueue(keyPacket, true);
                }
            }
        }
    }

    void CleanupDeadClients()
    {
        lock (_clientsLock)
        {
            for (int i = _clients.Count - 1; i >= 0; i--)
            {
                if (_clients[i].Alive) continue;
                _clients[i].Shutdown();
                _clients.RemoveAt(i);
            }
        }
    }

    void AcceptLoop()
    {
        while (_running)
        {
            try
            {
                TcpClient client = _listener.AcceptTcpClient();
                ClientConnection conn = new ClientConnection(client, _maxQueueDepth);
                lock (_clientsLock)
                {
                    _clients.Add(conn);
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

    public string GetClientDiagnostics()
    {
        lock (_clientsLock)
        {
            if (_clients.Count == 0) return null;

            StringBuilder sb = new StringBuilder();
            for (int i = 0; i < _clients.Count; i++)
            {
                if (i > 0) sb.Append("    ");
                ClientConnection c = _clients[i];
                sb.Append('C').Append(i)
                  .Append(" queue:").Append(c.QueueDepth)
                  .Append(" drop:").Append(c.DroppedFrames);
            }
            return sb.ToString();
        }
    }

    void OnDestroy()
    {
        _running = false;
        _listener?.Stop();

        lock (_clientsLock)
        {
            foreach (ClientConnection c in _clients) c.Shutdown();
            _clients.Clear();
        }

        _acceptThread?.Join(1000);
    }
}
