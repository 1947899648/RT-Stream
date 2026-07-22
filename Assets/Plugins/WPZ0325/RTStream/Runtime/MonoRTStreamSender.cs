using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using UnityEngine;

namespace WPZ0325.RTStream
{
    /// <summary>
    /// RT Stream 发送端组件。挂载到 GameObject 上，通过 TCP 将渲染纹理的脏瓦片数据流式传输给远程客户端。
    /// 支持多纹理注册、客户端订阅管理、关键帧按需发送、以及带宽诊断。
    /// </summary>
    public class MonoRTStreamSender : MonoBehaviour
    {
        #region 公开属性

        /// <summary>当前连接的客户端数量（线程安全）</summary>
        public int ClientCount
        {
            get { lock (_clientsLock) return _clients.Count; }
        }

        /// <summary>已注册的纹理数量（线程安全）</summary>
        public int TextureCount
        {
            get { lock (_clientsLock) return _textures.Count; }
        }

        /// <summary>诊断用：所有纹理上一轮回读的字节总数</summary>
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

        /// <summary>诊断用：本帧检测到的脏瓦片总数</summary>
        public int DiagDirtyTiles { get; private set; }

        /// <summary>发送端是否正在运行</summary>
        public bool IsRunning => _running;
        /// <summary>当前监听端口号</summary>
        public int ListenPort { get; private set; }

        /// <summary>原始脏数据带宽（MB/s，含索引头）</summary>
        public float RawDirtyMBps => _rawDirtyBandwidth.MBps;
        /// <summary>编码后上行带宽（MB/s）</summary>
        public float UpEncMBps => _upEncBandwidth.MBps;
        /// <summary>实际发送上行带宽（MB/s，含 TCP 帧头）</summary>
        public float UpSendMBps => _upSendBandwidth.MBps;

        #endregion

        #region 公开事件

        /// <summary>Host 启动监听时触发</summary>
        public event System.Action OnHostStarted;
        /// <summary>Host 停止监听时触发</summary>
        public event System.Action OnHostStopped;
        /// <summary>RenderTexture 注册成功时触发</summary>
        public event System.Action<string, int, int> OnRenderTextureRegistered;
        /// <summary>RenderTexture 注销时触发</summary>
        public event System.Action<string> OnRenderTextureUnregistered;
        /// <summary>RenderTexture 同步开始时触发（由暂停恢复）</summary>
        public event System.Action<string> OnRenderTextureSyncStarted;
        /// <summary>RenderTexture 同步暂停时触发</summary>
        public event System.Action<string> OnRenderTextureSyncPaused;
        /// <summary>RenderTexture 全帧（关键帧）发送时触发</summary>
        public event System.Action<string> OnRenderTextureKeyFrameSent;
        /// <summary>RenderTexture 脏瓦片增量帧发送时触发</summary>
        public event System.Action<string, int[]> OnRenderTextureDirtyTilesSent;
        /// <summary>客户端连接时触发</summary>
        public event System.Action<int> OnClientConnected;
        /// <summary>客户端断开时触发</summary>
        public event System.Action<int> OnClientDisconnected;

        #endregion

        #region 公开方法

        /// <summary>
        /// 注册一个渲染纹理用于流式传输。
        /// </summary>
        /// <param name="texId">纹理的全局唯一标识名称</param>
        /// <param name="rt">待传输的 RenderTexture</param>
        /// <exception cref="ArgumentNullException">rt 为 null 时抛出</exception>
        /// <exception cref="ArgumentException">texId 为空或已存在时抛出</exception>
        public void RegisterTexture(string texId, RenderTexture rt)
        {
            if (string.IsNullOrEmpty(texId)) throw new ArgumentException("texId cannot be null or empty", nameof(texId));
            if (rt == null) throw new ArgumentNullException(nameof(rt));
            if (!_running) throw new InvalidOperationException("Host is not running. Call StartHost first.");

            lock (_clientsLock)
            {
                if (_textures.ContainsKey(texId))
                    throw new ArgumentException($"texId \"{texId}\" is already registered", nameof(texId));

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

            Debug.Log($"MonoRTStreamSender: Registered texId=\"{texId}\" ({rt.width}x{rt.height})");
            OnRenderTextureRegistered?.Invoke(texId, rt.width, rt.height);
        }

        /// <summary>
        /// 注销一个纹理并释放其关联资源。
        /// </summary>
        public void UnregisterTexture(string texId)
        {
            lock (_clientsLock)
            {
                if (_textures.TryGetValue(texId, out TextureEntry entry))
                {
                    entry.Differ.Dispose();
                    _textures.Remove(texId);
                    OnRenderTextureUnregistered?.Invoke(texId);
                }
            }
        }

        /// <summary>设置纹理的启用/暂停状态。暂停时不再检测脏瓦片</summary>
        public void SetTextureEnabled(string texId, bool enabled)
        {
            lock (_clientsLock)
            {
                if (!_textures.TryGetValue(texId, out TextureEntry entry)) return;
                if (entry.Enabled == enabled) return;

                entry.Enabled = enabled;
                if (enabled)
                {
                    foreach (ClientConnection c in _clients)
                    {
                        if (!c.Alive) continue;
                        if (c.IsSubscribed(texId) && c.PendingKeyFrames != null)
                            c.PendingKeyFrames.Add(texId);
                    }
                    OnRenderTextureSyncStarted?.Invoke(texId);
                }
                else
                {
                    OnRenderTextureSyncPaused?.Invoke(texId);
                }
            }
        }

        /// <summary>查询纹理当前是否启用</summary>
        public bool IsTextureEnabled(string texId)
        {
            lock (_clientsLock)
            {
                if (_textures.TryGetValue(texId, out TextureEntry entry))
                    return entry.Enabled;
            }
            return false;
        }

        /// <summary>启动 TCP 监听服务</summary>
        public void StartHost(string ip, int port)
        {
            _cleanupHostInternal();

            ListenPort = port;
            _listener = new TcpListener(IPAddress.Parse(ip), port);
            _listener.Start();
            _running = true;
            _acceptThread = new Thread(AcceptLoop) { IsBackground = true };
            _acceptThread.Start();

            Debug.Log($"MonoRTStreamSender: Started on {ip}:{port}");
            OnHostStarted?.Invoke();
        }

        /// <summary>停止 TCP 监听服务并断开所有客户端</summary>
        public void StopHost()
        {
            bool wasRunning = _running;
            _cleanupHostInternal();

            Debug.Log("MonoRTStreamSender: Stopped");
            if (wasRunning)
                OnHostStopped?.Invoke();
        }

        /// <summary>获取所有客户端的诊断信息字符串（队列深度等）</summary>
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
                    sb.Append('C').Append(i).Append(" queue:").Append(c.QueueDepth);
                }
                return sb.ToString();
            }
        }

        #endregion

        #region Unity 生命周期
        void Update()
        {
            while (_mainThreadActions.TryDequeue(out System.Action action))
                action();

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
                foreach (KeyValuePair<string, TextureEntry> kv in _textures)
                {
                    string texId = kv.Key;
                    TextureEntry entry = kv.Value;

                    if (!entry.Enabled) continue;                    bool wantKeyFrame = false;
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
                        OnRenderTextureKeyFrameSent?.Invoke(texId);
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
                        OnRenderTextureDirtyTilesSent?.Invoke(texId, indices);

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

        #endregion

        #region 内部类型

        private class TextureEntry
        {
            public RenderTexture RT;
            public TileDiffer Differ;
            public bool Enabled = true;
        }

        private class ClientConnection
        {
            public volatile bool Alive = true;
            public HashSet<string> SubscribedIds;
            public HashSet<string> PendingKeyFrames;

            private TcpClient _client;
            private Thread _sendThread;
            private Queue<byte[]> _queue = new Queue<byte[]>();
            private object _lock = new object();
            private byte[] _lenBuf = new byte[4];
            private BandwidthMeter _sendMeter;

            public ClientConnection(TcpClient client, BandwidthMeter sendMeter, HashSet<string> subscribedIds)
            {
                _client = client;
                _sendMeter = sendMeter;
                SubscribedIds = subscribedIds;
                PendingKeyFrames = subscribedIds != null ? new HashSet<string>(subscribedIds) : null;
                try { _client.NoDelay = true; } catch { }
                _sendThread = new Thread(SendLoop) { IsBackground = true };
                _sendThread.Start();
            }

            public int QueueDepth
            {
                get { lock (_lock) return _queue.Count; }
            }

            public bool IsSubscribed(string texId)
            {
                return SubscribedIds == null || SubscribedIds.Contains(texId);
            }

            public bool IsConnected()
            {
                try
                {
                    Socket sock = _client.Client;
                    return !sock.Poll(0, SelectMode.SelectRead) || sock.Available > 0;
                }
                catch { return false; }
            }

            public void Enqueue(byte[] packet)
            {
                lock (_lock)
                {
                    _queue.Enqueue(packet);
                    Monitor.Pulse(_lock);
                }
            }

            private bool _isSocketAlive()
            {
                try
                {
                    Socket sock = _client.Client;
                    return !sock.Poll(0, SelectMode.SelectRead) || sock.Available > 0;
                }
                catch { return false; }
            }

            private void SendLoop()
            {
                try
                {
                    NetworkStream stream = _client.GetStream();
                    while (Alive)
                    {
                        try
                        {
                            if (!_isSocketAlive()) { Alive = false; return; }
                        }
                        catch { Alive = false; return; }

                        byte[] packet;
                        lock (_lock)
                        {
                            while (_queue.Count == 0)
                            {
                                if (!Alive) return;
                                Monitor.Wait(_lock, 1000);
                                try
                                {
                                    if (!_isSocketAlive()) { Alive = false; return; }
                                }
                                catch { Alive = false; return; }
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
                        try
                        {
                            if (!_isSocketAlive()) { Alive = false; return; }
                        }
                        catch { Alive = false; return; }
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

        #endregion

        #region 序列化与私有字段

        [SerializeField] private ComputeShader _tileDiffShader;

        private TcpListener _listener;
        private List<ClientConnection> _clients = new List<ClientConnection>();
        private object _clientsLock = new object();
        private Thread _acceptThread;
        private volatile bool _running;

        private const int MaxMsgSize = 256 * 1024;

        private Dictionary<string, TextureEntry> _textures = new Dictionary<string, TextureEntry>();

        private BandwidthMeter _rawDirtyBandwidth = new BandwidthMeter();
        private BandwidthMeter _upEncBandwidth = new BandwidthMeter();
        private BandwidthMeter _upSendBandwidth = new BandwidthMeter();

        private ConcurrentQueue<System.Action> _mainThreadActions = new ConcurrentQueue<System.Action>();

        #endregion

        #region 连接与帧处理

        private void _cleanupHostInternal()
        {
            _running = false;
            _listener?.Stop();
            _acceptThread?.Join(1000);

            lock (_clientsLock)
            {
                foreach (ClientConnection c in _clients) c.Shutdown();
                _clients.Clear();

                foreach (KeyValuePair<string, TextureEntry> kv in _textures)
                {
                    kv.Value.Differ.Dispose();
                }
                _textures.Clear();
            }

            _listener = null;
            _acceptThread = null;
        }

        void SendAnnounce(ClientConnection c, string texId, ushort texWidth, ushort texHeight)
        {
            byte[] announce = FrameCodec.EncodeTextureAnnounce(texId, texWidth, texHeight);
            c.Enqueue(announce);
        }

        void SendTiledKeyFrame(string texId, ushort texWidth, ushort texHeight, byte[] fullFrame)
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
            int removed = 0;
            for (int i = _clients.Count - 1; i >= 0; i--)
            {
                if (_clients[i].Alive && _clients[i].IsConnected()) continue;
                _clients[i].Shutdown();
                _clients.RemoveAt(i);
                removed++;
            }
            if (removed > 0)
            {
                int total = _clients.Count;
                _mainThreadActions.Enqueue(() => OnClientDisconnected?.Invoke(total));
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

                    HashSet<string> subscribedIds = ReadHandshake(client);
                    if (subscribedIds == null)
                    {
                        try { client.Close(); } catch { }
                        continue;
                    }

                    ClientConnection conn = new ClientConnection(client, _upSendBandwidth, subscribedIds);

                    lock (_clientsLock)
                    {
                        _clients.Add(conn);

                        foreach (KeyValuePair<string, TextureEntry> kv in _textures)
                        {
                            string texId = kv.Key;
                            if (conn.IsSubscribed(texId))
                            {
                                SendAnnounce(conn, texId, (ushort)kv.Value.Differ.TexWidth, (ushort)kv.Value.Differ.TexHeight);
                                if (conn.PendingKeyFrames != null)
                                    conn.PendingKeyFrames.Add(texId);
                            }
                        }
                        int clientCount = _clients.Count;
                        _mainThreadActions.Enqueue(() => OnClientConnected?.Invoke(clientCount));
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

        HashSet<string> ReadHandshake(TcpClient client)
        {
            try
            {
                client.ReceiveTimeout = 200;
                NetworkStream stream = client.GetStream();
                byte[] header = new byte[2];
                if (!ReadExact(stream, header, 0, 2) || header[0] != (byte)FrameType.SubscribeReq)
                    return null;

                int count = header[1];
                if (count == 0) return null;

                HashSet<string> ids = new HashSet<string>();
                for (int i = 0; i < count; i++)
                {
                    byte[] lenBuf = new byte[1];
                    if (!ReadExact(stream, lenBuf, 0, 1)) return null;
                    int nameLen = lenBuf[0];
                    byte[] nameBuf = new byte[nameLen];
                    if (!ReadExact(stream, nameBuf, 0, nameLen)) return null;
                    ids.Add(Encoding.UTF8.GetString(nameBuf, 0, nameLen));
                }
                return ids;
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

        #endregion
    }
}
