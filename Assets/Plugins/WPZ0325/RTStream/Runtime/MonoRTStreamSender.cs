using System;
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

        /// <summary>
        /// 当前连接的客户端数量（线程安全）。
        /// </summary>
        public int ClientCount
        {
            get { lock (_clientsLock) return _clients.Count; }
        }

        /// <summary>
        /// 已注册的纹理数量（线程安全）。
        /// </summary>
        public int TextureCount
        {
            get { lock (_clientsLock) return _textures.Count; }
        }

        /// <summary>
        /// 诊断用：所有纹理上一轮回读的字节总数。
        /// </summary>
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

        /// <summary>
        /// 诊断用：本帧检测到的脏瓦片总数。
        /// </summary>
        public int DiagDirtyTiles { get; private set; }

        /// <summary>
        /// 尝试获取第一个已注册纹理的尺寸信息。
        /// </summary>
        /// <param name="width">输出：纹理宽度</param>
        /// <param name="height">输出：纹理高度</param>
        /// <returns>存在已注册纹理时返回 true</returns>
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

        /// <summary>
        /// 诊断用：当前注册的纹理总数。
        /// </summary>
        public int DiagTextureCount
        {
            get { lock (_clientsLock) return _textures.Count; }
        }

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

        /// <summary>
        /// 当脏瓦片被检测到时触发。参数为纹理 ID 和脏瓦片索引数组。
        /// </summary>
        public event System.Action<byte, int[]> OnDirtyTilesDetected;

        #endregion

        #region 公开方法

        /// <summary>
        /// 获取所有已注册纹理的诊断信息列表。
        /// </summary>
        /// <returns>纹理信息列表</returns>
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

        /// <summary>
        /// 注册一个渲染纹理用于流式传输。
        /// </summary>
        /// <param name="rt">待传输的 RenderTexture</param>
        /// <returns>分配的纹理 ID</returns>
        /// <exception cref="ArgumentNullException">rt 为 null 时抛出</exception>
        public byte RegisterTexture(RenderTexture rt)
        {
            if (rt == null) throw new ArgumentNullException(nameof(rt));

            byte texId;
            lock (_clientsLock)
            {
                texId = _nextTexId++;
                TileDiffer differ = new TileDiffer(rt, _tileDiffShader);
                _textures.Add(texId, new TextureEntry { RT = rt, Differ = differ });

                // 通知所有现有客户端新纹理已注册
                foreach (ClientConnection c in _clients)
                {
                    if (!c.Alive) continue;
                    if (c.IsSubscribed(texId))
                    {
                        // 如果客户端订阅了此纹理，标记需要发送关键帧
                        if (c.PendingKeyFrames != null)
                            c.PendingKeyFrames.Add(texId);
                    }
                    SendAnnounce(c, texId, (ushort)differ.TexWidth, (ushort)differ.TexHeight);
                }
            }

            Debug.Log($"MonoRTStreamSender: Registered texId={texId} ({rt.width}x{rt.height})");
            return texId;
        }

        /// <summary>
        /// 注销一个纹理并释放其关联资源。
        /// </summary>
        /// <param name="texId">要注销的纹理 ID</param>
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

        /// <summary>
        /// 启动 TCP 监听服务。
        /// </summary>
        /// <param name="port">监听端口号</param>
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

        /// <summary>
        /// 停止 TCP 监听服务并断开所有客户端。
        /// </summary>
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

        /// <summary>
        /// 获取所有客户端的诊断信息字符串（队列深度等）。
        /// </summary>
        /// <returns>诊断字符串，无客户端时返回 null</returns>
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

        #endregion

        #region Unity 生命周期

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

            // 无客户端连接时跳过检测以节省性能
            if (totalClients == 0) return;

            int totalDirty = 0;

            lock (_clientsLock)
            {
                foreach (KeyValuePair<byte, TextureEntry> kv in _textures)
                {
                    byte texId = kv.Key;
                    TextureEntry entry = kv.Value;

                    // 检查是否有客户端需要此纹理的关键帧
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
                        // 关键帧：将全帧数据拆分为瓦片后分批发给订阅客户端
                        int rawBytes = 8 + fullFrame.Length;
                        _rawDirtyBandwidth.Add(rawBytes);
                        OnDirtyTilesDetected?.Invoke(texId, null);
                        SendTiledKeyFrame(texId, texW, texH, fullFrame);
                    }
                    else if (dirtyTiles != null && dirtyTiles.Count > 0)
                    {
                        // 增量帧：将脏瓦片编码后广播给订阅客户端
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

            // 更新带宽统计
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

        /// <summary>
        /// 注册的纹理条目：包含 RenderTexture 引用及其对应的 TileDiffer 实例。
        /// </summary>
        private class TextureEntry
        {
            public RenderTexture RT;
            public TileDiffer Differ;
        }

        /// <summary>
        /// TCP 客户端连接管理。每个实例对应一个远程接收端，包含独立的发送线程和消息队列。
        /// </summary>
        private class ClientConnection
        {
            /// <summary>连接存活标志</summary>
            public volatile bool Alive = true;
            /// <summary>已订阅的纹理 ID 集合，null 表示订阅全部</summary>
            public HashSet<byte> SubscribedIds;
            /// <summary>待发送关键帧的纹理 ID 集合</summary>
            public HashSet<byte> PendingKeyFrames;

            private TcpClient _client;
            private Thread _sendThread;
            private Queue<byte[]> _queue = new Queue<byte[]>();
            private object _lock = new object();
            private byte[] _lenBuf = new byte[4];
            private BandwidthMeter _sendMeter;

            /// <summary>
            /// 创建客户端连接并启动发送线程。
            /// </summary>
            /// <param name="client">已建立的 TcpClient</param>
            /// <param name="sendMeter">用于统计发送带宽的计量器</param>
            /// <param name="subscribedIds">订阅的纹理 ID 集合</param>
            public ClientConnection(TcpClient client, BandwidthMeter sendMeter, HashSet<byte> subscribedIds)
            {
                _client = client;
                _sendMeter = sendMeter;
                SubscribedIds = subscribedIds;
                // 新连接时，所有已订阅纹理都需要发送关键帧（如果此连接订阅了该纹理）
                PendingKeyFrames = subscribedIds != null ? new HashSet<byte>(subscribedIds) : null;
                try { _client.NoDelay = true; } catch { }
                _sendThread = new Thread(SendLoop) { IsBackground = true };
                _sendThread.Start();
            }

            /// <summary>
            /// 发送队列中的消息数量。
            /// </summary>
            public int QueueDepth
            {
                get { lock (_lock) return _queue.Count; }
            }

            /// <summary>
            /// 检查是否订阅了指定纹理。
            /// </summary>
            /// <param name="texId">纹理 ID</param>
            /// <returns>true 表示已订阅</returns>
            public bool IsSubscribed(byte texId)
            {
                // subscribedIds 为 null 时表示订阅全部纹理
                return SubscribedIds == null || SubscribedIds.Contains(texId);
            }

            /// <summary>
            /// 将数据包加入发送队列并唤醒发送线程。
            /// </summary>
            /// <param name="packet">待发送的帧数据包</param>
            public void Enqueue(byte[] packet)
            {
                lock (_lock)
                {
                    _queue.Enqueue(packet);
                    Monitor.Pulse(_lock);
                }
            }

            // 发送线程主循环：从队列取包，以 4 字节长度头 + 数据体的格式写入 TCP 流
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

            /// <summary>
            /// 关闭连接并等待发送线程结束。
            /// </summary>
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

        // 单帧消息体最大尺寸限制，防止 TCP 粘包时缓冲区过大
        private const int MaxMsgSize = 256 * 1024;

        // 纹理 ID 分配器，自增分配
        private byte _nextTexId = 0;
        private Dictionary<byte, TextureEntry> _textures = new Dictionary<byte, TextureEntry>();

        // 带宽计量器：分别统计原始脏数据量、编码后数据量、实际发送量
        private BandwidthMeter _rawDirtyBandwidth = new BandwidthMeter();
        private BandwidthMeter _upEncBandwidth = new BandwidthMeter();
        private BandwidthMeter _upSendBandwidth = new BandwidthMeter();

        #endregion

        #region 连接与帧处理

        /// <summary>
        /// 向指定客户端发送纹理公告帧。
        /// </summary>
        void SendAnnounce(ClientConnection c, byte texId, ushort texWidth, ushort texHeight)
        {
            byte[] announce = FrameCodec.EncodeTextureAnnounce(texId, texWidth, texHeight);
            c.Enqueue(announce);
        }

        /// <summary>
        /// 将关键帧全帧数据拆分为多个增量帧包发送，避免单帧过大。
        /// </summary>
        void SendTiledKeyFrame(byte texId, ushort texWidth, ushort texHeight, byte[] fullFrame)
        {
            int tileSize = FrameCodec.TileSize;
            int tilesX = texWidth / tileSize;
            int tilesY = texHeight / tileSize;
            int tileBytes = tileSize * tileSize * 4;
            int tileRowBytes = tileSize * 4;
            int totalTiles = tilesX * tilesY;

            // 计算每批次可容纳的最大瓦片数
            int maxPerBatch = (MaxMsgSize - FrameCodec.HeaderSize) / (4 + tileBytes);
            if (maxPerBatch < 1) maxPerBatch = 1;

            List<DirtyTile> batch = new List<DirtyTile>(maxPerBatch);

            // 按行遍历全帧，将每个瓦片的像素数据提取出来
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

                // 达到批次上限或最后一个瓦片时，编码并广播
                if (batch.Count >= maxPerBatch || tileIndex == totalTiles - 1)
                {
                    byte[] packet = FrameCodec.EncodeDeltaFrame(texId, batch);
                    _upEncBandwidth.Add(packet.Length);

                    lock (_clientsLock)
                    {
                        foreach (ClientConnection c in _clients)
                        {
                            if (!c.Alive || !c.IsSubscribed(texId)) continue;
                            // 关键帧瓦片已发送，清除待发送标记
                            if (c.PendingKeyFrames != null)
                                c.PendingKeyFrames.Remove(texId);
                            c.Enqueue(packet);
                        }
                    }
                    batch.Clear();
                }
            }
        }

        /// <summary>
        /// 清理已断开连接的客户端。
        /// </summary>
        void CleanupDeadClients()
        {
            for (int i = _clients.Count - 1; i >= 0; i--)
            {
                if (_clients[i].Alive) continue;
                _clients[i].Shutdown();
                _clients.RemoveAt(i);
            }
        }

        // 接受新客户端连接的线程循环
        void AcceptLoop()
        {
            while (_running)
            {
                try
                {
                    TcpClient client = _listener.AcceptTcpClient();
                    client.NoDelay = true;

                    // 读取客户端握手消息，获取订阅的纹理 ID 列表
                    HashSet<byte> subscribedIds = ReadHandshake(client);

                    ClientConnection conn = new ClientConnection(client, _upSendBandwidth, subscribedIds);

                    lock (_clientsLock)
                    {
                        _clients.Add(conn);

                        // 向新客户端发送所有已注册纹理的公告
                        foreach (KeyValuePair<byte, TextureEntry> kv in _textures)
                        {
                            byte texId = kv.Key;
                            if (conn.IsSubscribed(texId))
                            {
                                SendAnnounce(conn, texId, (ushort)kv.Value.Differ.TexWidth, (ushort)kv.Value.Differ.TexHeight);
                                // 标记此纹理需要发送关键帧
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

        // 读取客户端握手数据，解析为订阅的纹理 ID 集合
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
                    // count 为 0 表示订阅所有纹理（返回 null）
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

        // 从网络流中精确读取指定字节数（阻塞直到读完或断开）
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
