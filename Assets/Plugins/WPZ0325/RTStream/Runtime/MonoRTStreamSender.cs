using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using UnityEngine;

public class MonoRTStreamSender : MonoBehaviour
{
    [SerializeField] private ComputeShader _tileDiffShader;

    private TcpListener _listener;
    private List<ClientConnection> _clients = new List<ClientConnection>();
    private object _clientsLock = new object();
    private Thread _acceptThread;
    private volatile bool _running;
    private const int MaxMsgSize = 256 * 1024;

    private byte _nextTexId = 0;
    private Dictionary<byte, TextureEntry> _textures = new Dictionary<byte, TextureEntry>();

    private class TextureEntry
    {
        public RenderTexture RT;
        public TileDiffer Differ;
    }

    public int ClientCount
    {
        get { lock (_clientsLock) return _clients.Count; }
    }

    public int TextureCount
    {
        get { lock (_clientsLock) return _textures.Count; }
    }

    public int DiagReadbackBytes
    {
        get
        {
            int sum = 0;
            lock (_clientsLock)
            {
                foreach (TextureEntry entry in _textures.Values)
                    sum += entry.Differ.DiagReadbackBytes;
            }
            return sum;
        }
    }

    public int DiagDirtyTiles { get; private set; }

    public bool TryGetDiagTextureInfo(out int width, out int height)
    {
        lock (_clientsLock)
        {
            foreach (TextureEntry entry in _textures.Values)
            {
                width = entry.Differ.TexWidth;
                height = entry.Differ.TexHeight;
                return true;
            }
        }
        width = 0;
        height = 0;
        return false;
    }

    public int DiagTextureCount
    {
        get { lock (_clientsLock) return _textures.Count; }
    }

    public event System.Action<byte, int[]> OnDirtyTilesDetected;

    public List<DiagTextureInfo> GetDiagTextureList()
    {
        List<DiagTextureInfo> list = new List<DiagTextureInfo>();
        lock (_clientsLock)
        {
            foreach (KeyValuePair<byte, TextureEntry> kv in _textures)
            {
                list.Add(new DiagTextureInfo
                {
                    TexId = kv.Key,
                    Width = kv.Value.Differ.TexWidth,
                    Height = kv.Value.Differ.TexHeight
                });
            }
        }
        return list;
    }

    private BandwidthMeter _rawDirtyBandwidth = new BandwidthMeter();
    private BandwidthMeter _upEncBandwidth = new BandwidthMeter();
    private BandwidthMeter _upSendBandwidth = new BandwidthMeter();
    public float RawDirtyMBps => _rawDirtyBandwidth.MBps;
    public float UpEncMBps => _upEncBandwidth.MBps;
    public float UpSendMBps => _upSendBandwidth.MBps;

    private class ClientConnection
    {
        public volatile bool Alive = true;
        public HashSet<byte> SubscribedIds;
        public HashSet<byte> PendingKeyFrames;

        private TcpClient _client;
        private Thread _sendThread;
        private Queue<byte[]> _queue = new Queue<byte[]>();
        private object _lock = new object();
        private byte[] _lenBuf = new byte[4];
        private BandwidthMeter _sendMeter;

        public ClientConnection(TcpClient client, BandwidthMeter sendMeter, HashSet<byte> subscribedIds)
        {
            _client = client;
            _sendMeter = sendMeter;
            SubscribedIds = subscribedIds;
            PendingKeyFrames = subscribedIds != null ? new HashSet<byte>(subscribedIds) : null;
            try { _client.NoDelay = true; } catch { }
            _sendThread = new Thread(SendLoop) { IsBackground = true };
            _sendThread.Start();
        }

        public int QueueDepth
        {
            get { lock (_lock) return _queue.Count; }
        }

        public bool IsSubscribed(byte texId)
        {
            return SubscribedIds == null || SubscribedIds.Contains(texId);
        }

        public void Enqueue(byte[] packet)
        {
            lock (_lock)
            {
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

    public byte RegisterTexture(RenderTexture rt)
    {
        if (rt == null) throw new ArgumentNullException(nameof(rt));

        byte texId;
        lock (_clientsLock)
        {
            texId = _nextTexId++;
            TileDiffer differ = new TileDiffer(rt, _tileDiffShader);
            _textures.Add(texId, new TextureEntry { RT = rt, Differ = differ });

            foreach (ClientConnection c in _clients)
            {
                if (!c.Alive) continue;
                if (c.IsSubscribed(texId))
                {
                    if (c.PendingKeyFrames != null)
                        c.PendingKeyFrames.Add(texId);
                }
                SendAnnounce(c, texId, (ushort)differ.TexWidth, (ushort)differ.TexHeight);
            }
        }

        Debug.Log($"MonoRTStreamSender: Registered texId={texId} ({rt.width}x{rt.height})");
        return texId;
    }

    public void UnregisterTexture(byte texId)
    {
        lock (_clientsLock)
        {
            if (_textures.TryGetValue(texId, out TextureEntry entry))
            {
                entry.Differ.Dispose();
                _textures.Remove(texId);
            }
        }
    }

    public bool IsRunning => _running;
    public int ListenPort { get; private set; }

    public void StartHost(int port)
    {
        StopHost();

        ListenPort = port;
        _listener = new TcpListener(IPAddress.Any, port);
        _listener.Start();
        _running = true;
        _acceptThread = new Thread(AcceptLoop) { IsBackground = true };
        _acceptThread.Start();

        Debug.Log($"MonoRTStreamSender: Started on port {port}");
    }

    public void StopHost()
    {
        _running = false;
        _listener?.Stop();
        _acceptThread?.Join(1000);

        lock (_clientsLock)
        {
            foreach (ClientConnection c in _clients) c.Shutdown();
            _clients.Clear();
        }

        _listener = null;
        _acceptThread = null;

        Debug.Log("MonoRTStreamSender: Stopped");
    }

    void Start()
    {
    }

    void Update()
    {
        int totalClients;
        lock (_clientsLock)
        {
            CleanupDeadClients();
            totalClients = _clients.Count;
        }

        if (totalClients == 0) return;

        int totalDirty = 0;

        lock (_clientsLock)
        {
            foreach (KeyValuePair<byte, TextureEntry> kv in _textures)
            {
                byte texId = kv.Key;
                TextureEntry entry = kv.Value;

                bool wantKeyFrame = false;
                foreach (ClientConnection c in _clients)
                {
                    if (!c.Alive) continue;
                    if (c.IsSubscribed(texId) && c.PendingKeyFrames != null && c.PendingKeyFrames.Contains(texId))
                    {
                        wantKeyFrame = true;
                        break;
                    }
                }

                entry.Differ.Update(wantKeyFrame);

                if (!entry.Differ.TryGetResult(out List<DirtyTile> dirtyTiles, out byte[] fullFrame)) continue;

                ushort texW = (ushort)entry.Differ.TexWidth;
                ushort texH = (ushort)entry.Differ.TexHeight;

                if (fullFrame != null)
                {
                    int rawBytes = 8 + fullFrame.Length;
                    _rawDirtyBandwidth.Add(rawBytes);
                    OnDirtyTilesDetected?.Invoke(texId, null);
                    SendTiledKeyFrame(texId, texW, texH, fullFrame);
                }
                else if (dirtyTiles != null && dirtyTiles.Count > 0)
                {
                    int tileBytes = FrameCodec.GetBytesPerTile();
                    int rawBytes = dirtyTiles.Count * (4 + tileBytes);
                    _rawDirtyBandwidth.Add(rawBytes);
                    totalDirty += dirtyTiles.Count;

                    int[] indices = new int[dirtyTiles.Count];
                    for (int i = 0; i < dirtyTiles.Count; i++)
                        indices[i] = dirtyTiles[i].index;
                    OnDirtyTilesDetected?.Invoke(texId, indices);

                    byte[] deltaPacket = FrameCodec.EncodeDeltaFrame(texId, dirtyTiles);
                    _upEncBandwidth.Add(deltaPacket.Length);

                    foreach (ClientConnection c in _clients)
                    {
                        if (!c.Alive || !c.IsSubscribed(texId)) continue;
                        c.Enqueue(deltaPacket);
                    }
                }
            }
        }

        DiagDirtyTiles = totalDirty;

        _rawDirtyBandwidth.Sample();
        _upEncBandwidth.Sample();
        _upSendBandwidth.Sample();
    }

    void SendAnnounce(ClientConnection c, byte texId, ushort texWidth, ushort texHeight)
    {
        byte[] announce = FrameCodec.EncodeTextureAnnounce(texId, texWidth, texHeight);
        c.Enqueue(announce);
    }

    void SendTiledKeyFrame(byte texId, ushort texWidth, ushort texHeight, byte[] fullFrame)
    {
        int tileSize = FrameCodec.TileSize;
        int tilesX = texWidth / tileSize;
        int tilesY = texHeight / tileSize;
        int tileBytes = tileSize * tileSize * 4;
        int tileRowBytes = tileSize * 4;
        int totalTiles = tilesX * tilesY;

        int maxPerBatch = (MaxMsgSize - FrameCodec.HeaderSize) / (4 + tileBytes);
        if (maxPerBatch < 1) maxPerBatch = 1;

        List<DirtyTile> batch = new List<DirtyTile>(maxPerBatch);

        for (int tileIndex = 0; tileIndex < totalTiles; tileIndex++)
        {
            int tx = tileIndex % tilesX;
            int ty = tileIndex / tilesX;
            byte[] tileData = new byte[tileBytes];
            for (int row = 0; row < tileSize; row++)
            {
                int srcOff = ((ty * tileSize + row) * texWidth + tx * tileSize) * 4;
                Buffer.BlockCopy(fullFrame, srcOff, tileData, row * tileRowBytes, tileRowBytes);
            }
            batch.Add(new DirtyTile { index = tileIndex, data = tileData });

            if (batch.Count >= maxPerBatch || tileIndex == totalTiles - 1)
            {
                byte[] packet = FrameCodec.EncodeDeltaFrame(texId, batch);
                _upEncBandwidth.Add(packet.Length);

                lock (_clientsLock)
                {
                    foreach (ClientConnection c in _clients)
                    {
                        if (!c.Alive || !c.IsSubscribed(texId)) continue;
                        if (c.PendingKeyFrames != null)
                            c.PendingKeyFrames.Remove(texId);
                        c.Enqueue(packet);
                    }
                }
                batch.Clear();
            }
        }
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
                client.NoDelay = true;

                HashSet<byte> subscribedIds = ReadHandshake(client);

                ClientConnection conn = new ClientConnection(client, _upSendBandwidth, subscribedIds);

                lock (_clientsLock)
                {
                    _clients.Add(conn);

                    foreach (KeyValuePair<byte, TextureEntry> kv in _textures)
                    {
                        byte texId = kv.Key;
                        if (conn.IsSubscribed(texId))
                        {
                            SendAnnounce(conn, texId, (ushort)kv.Value.Differ.TexWidth, (ushort)kv.Value.Differ.TexHeight);
                            if (conn.PendingKeyFrames != null)
                                conn.PendingKeyFrames.Add(texId);
                        }
                    }
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

    HashSet<byte> ReadHandshake(TcpClient client)
    {
        try
        {
            client.ReceiveTimeout = 200;
            NetworkStream stream = client.GetStream();
            byte[] header = new byte[2];
            if (ReadExact(stream, header, 0, 2) && header[0] == (byte)FrameType.SubscribeReq)
            {
                int count = header[1];
                if (count == 0) return null;

                byte[] idData = new byte[count];
                if (ReadExact(stream, idData, 0, count))
                {
                    HashSet<byte> ids = new HashSet<byte>();
                    for (int i = 0; i < count; i++)
                        ids.Add(idData[i]);
                    return ids;
                }
            }
        }
        catch { }
        return null;
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
                  .Append(" queue:").Append(c.QueueDepth);
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

            foreach (TextureEntry entry in _textures.Values)
                entry.Differ.Dispose();
            _textures.Clear();
        }

        _acceptThread?.Join(1000);
    }
}
