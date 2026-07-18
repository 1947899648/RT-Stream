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
    [SerializeField] private ComputeShader _tileDiffShader;

    private TcpListener _listener;
    private List<ClientConnection> _clients = new List<ClientConnection>();
    private object _clientsLock = new object();
    private ITileSource _tileSource;
    private int _seq;
    private Thread _acceptThread;
    private volatile bool _running;
    private int _texWidth, _texHeight;

    public int ClientCount
    {
        get { lock (_clientsLock) return _clients.Count; }
    }

    public int DiagSeq => _seq;
    public string DiagDiffBackend => (_tileSource is GpuTileDiffer) ? "GPU" : "CPU";
    public int DiagReadbackBytes => _tileSource != null ? _tileSource.DiagReadbackBytes : 0;
    public int DiagDirtyTiles { get; private set; }

    public event System.Action<int[]> OnDirtyTilesDetected;

    private BandwidthMeter _upEncBandwidth = new BandwidthMeter();
    private BandwidthMeter _upSendBandwidth = new BandwidthMeter();
    public float UpEncMBps => _upEncBandwidth.MBps;
    public float UpSendMBps => _upSendBandwidth.MBps;

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
        private BandwidthMeter _sendMeter;

        public ClientConnection(TcpClient client, int maxDepth, BandwidthMeter sendMeter)
        {
            _client = client;
            _maxDepth = maxDepth;
            _sendMeter = sendMeter;
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
                    _sendMeter.Add(4 + len);
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

        if (SystemInfo.supportsComputeShaders && _tileDiffShader != null)
        {
            _tileSource = new GpuTileDiffer(canvas.CanvasTexture, _tileDiffShader);
        }
        else
        {
            _tileSource = new TileDiffer(canvas.CanvasTexture);
        }

        _listener = new TcpListener(IPAddress.Any, port);
        _listener.Start();
        _running = true;
        _acceptThread = new Thread(AcceptLoop) { IsBackground = true };
        _acceptThread.Start();
    }

    void Update()
    {
        bool anyNeedKeyFrame = false;
        int clientCount;
        lock (_clientsLock)
        {
            CleanupDeadClients();
            clientCount = _clients.Count;
            foreach (ClientConnection c in _clients)
            {
                if (c.Alive && c.NeedKeyFrame) anyNeedKeyFrame = true;
            }
        }

        bool wantKeyFrame = anyNeedKeyFrame || (_seq > 0 && _seq % keyFrameInterval == 0);
        _tileSource.Update(wantKeyFrame);

        if (!_tileSource.TryGetResult(out List<DirtyTile> dirtyTiles, out byte[] fullFrame)) return;
        DiagDirtyTiles = dirtyTiles != null ? dirtyTiles.Count : 0;

        if (fullFrame != null)
        {
            OnDirtyTilesDetected?.Invoke(null);
        }
        else if (dirtyTiles != null && dirtyTiles.Count > 0)
        {
            int[] indices = new int[dirtyTiles.Count];
            for (int i = 0; i < indices.Length; i++)
                indices[i] = dirtyTiles[i].index;
            OnDirtyTilesDetected?.Invoke(indices);
        }

        if (clientCount == 0) return;

        lock (_clientsLock)
        {
            if (fullFrame != null)
            {
                byte[] keyPacket = FrameCodec.EncodeKeyFrame(_texWidth, _texHeight, fullFrame);
                _upEncBandwidth.Add(keyPacket.Length);
                foreach (ClientConnection c in _clients)
                {
                    if (!c.Alive) continue;
                    c.NeedKeyFrame = false;
                    c.Enqueue(keyPacket, true);
                }
                _seq++;
            }
            else if (dirtyTiles != null && dirtyTiles.Count > 0)
            {
                byte[] deltaPacket = FrameCodec.EncodeDeltaFrame(dirtyTiles);
                _upEncBandwidth.Add(deltaPacket.Length);
                foreach (ClientConnection c in _clients)
                {
                    if (!c.Alive) continue;
                    c.Enqueue(deltaPacket, false);
                }
                _seq++;
            }
        }

        _upEncBandwidth.Sample();
        _upSendBandwidth.Sample();
    }

    void CleanupDeadClients()
    {
        for (int i = _clients.Count - 1; i >= 0; i--)
        {
            if (_clients[i].Alive) continue;
            _clients[i].Shutdown();
            _clients.RemoveAt(i);
        }
    }

    void AcceptLoop()
    {
        while (_running)
        {
            try
            {
                TcpClient client = _listener.AcceptTcpClient();
                ClientConnection conn = new ClientConnection(client, _maxQueueDepth, _upSendBandwidth);
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
        _tileSource?.Dispose();
    }
}
