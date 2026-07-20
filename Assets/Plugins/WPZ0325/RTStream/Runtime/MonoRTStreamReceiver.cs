using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using Stopwatch = System.Diagnostics.Stopwatch;
using System.Net.Sockets;
using System.Threading;
using UnityEngine;

namespace WPZ0325.RTStream
{
    /// <summary>
    /// RT Stream 接收端组件。挂载到 GameObject 上，通过 TCP 接收发送端传输的帧数据，
    /// 使用 ComputeShader 将脏瓦片应用到输出 RenderTexture 上。
    /// 支持纹理公告处理、带宽统计和延迟测量。
    /// </summary>
    public class MonoRTStreamReceiver : MonoBehaviour
    {
        #region 公开属性

        /// <summary>是否已连接到发送端</summary>
        public bool IsConnected => _connected;
        /// <summary>上一帧批处理的数据包数量</summary>
        public int LastBatchSize => _lastBatchSize;
        /// <summary>本帧接收到的脏瓦片总数</summary>
        public int DirtyTilesReceived { get; private set; }
        /// <summary>网络延迟（毫秒）</summary>
        public float NetLagMs => _netLagMs;
        /// <summary>本地排队延迟（毫秒，从接收到处理的时间差）</summary>
        public float LocalLagMs => _localLagMs;
        /// <summary>远端发送端主机地址</summary>
        public string RemoteHost { get; private set; }
        /// <summary>远端发送端端口</summary>
        public int RemotePort { get; private set; }

        /// <summary>下行接收带宽（MB/s）</summary>
        public float DownRecvMBps => _downRecvBandwidth.MBps;
        /// <summary>下行处理带宽（MB/s，解码+GPU 应用）</summary>
        public float DownProcMBps => _downProcBandwidth.MBps;

        /// <summary>
        /// 尝试获取第一个已接收纹理公告的尺寸信息。
        /// </summary>
        /// <param name="width">输出：纹理宽度</param>
        /// <param name="height">输出：纹理高度</param>
        /// <returns>存在纹理元数据时返回 true</returns>
        public bool TryGetDiagTextureInfo(out int width, out int height)
        {
            foreach (TextureMeta meta in _meta.Values)
            {
                width = meta.Width;
                height = meta.Height;
                return true;
            }
            width = 0;
            height = 0;
            return false;
        }

        /// <summary>
        /// 距离最后一次收到数据的静默时间（毫秒）。
        /// </summary>
        public float SilenceMs
        {
            get
            {
                if (_lastRecvWatchTimestamp == 0) return 0;
                return (Stopwatch.GetTimestamp() - _lastRecvWatchTimestamp) * 1000f / Stopwatch.Frequency;
            }
        }

        #endregion

        #region 公开事件

        /// <summary>
        /// 脏瓦片被应用到渲染纹理后触发。
        /// </summary>
        public event System.Action<byte, int[]> OnDirtyTilesApplied;
        /// <summary>
        /// 接收到纹理公告帧后触发。参数为纹理 ID、宽度、高度。
        /// </summary>
        public event System.Action<byte, int, int> OnTextureAnnounce;

        #endregion

        #region 公开方法

        /// <summary>
        /// 连接到发送端并开始接收数据。
        /// </summary>
        /// <param name="host">发送端主机地址</param>
        /// <param name="port">发送端端口</param>
        /// <param name="subscribedTexIds">订阅的纹理 ID 数组，null 表示订阅全部</param>
        public void Connect(string host, int port, byte[] subscribedTexIds = null)
        {
            Disconnect();
            _lastBatchSize = 0;
            _downRecvBandwidth.Reset();
            _downProcBandwidth.Reset();
            _meta.Clear();
            _subscribedTexIds = subscribedTexIds;

            RemoteHost = host;
            RemotePort = port;

            _kApplyDelta = _tileApplyShader.FindKernel("KApplyDelta");

            try
            {
                _tcpClient = new TcpClient();
                _tcpClient.Connect(host, port);
                _stream = _tcpClient.GetStream();

                // 连接建立后立即发送订阅握手
                byte[] handshake = FrameCodec.EncodeSubscribeReq(_subscribedTexIds);
                _stream.Write(handshake, 0, handshake.Length);

                _connected = true;
                _running = true;
                _receiveThread = new Thread(ReceiveLoop) { IsBackground = true };
                _receiveThread.Start();
            }
            catch (Exception e)
            {
                Debug.LogError($"MonoRTStreamReceiver connect failed: {e.Message}");
                Close();
            }
        }

        /// <summary>
        /// 断开与发送端的连接并清理资源。
        /// </summary>
        public void Disconnect()
        {
            _running = false;
            _connected = false;
            _receiveThread?.Join(500);
            Close();
            // 清空接收队列中残留的数据
            while (_frameQueue.TryDequeue(out FrameEntry _)) { }
            _batch.Clear();
            ReleasePayloadBuffer();
            _meta.Clear();
        }

        /// <summary>
        /// 将指定纹理 ID 绑定到输出 RenderTexture。接收到的瓦片数据将应用到此纹理上。
        /// </summary>
        /// <param name="texId">纹理 ID</param>
        /// <param name="rt">输出 RenderTexture，需启用 RandomWrite</param>
        public void BindOutputTexture(byte texId, RenderTexture rt)
        {
            _outputTextures[texId] = rt;
        }

        /// <summary>
        /// 解除指定纹理 ID 的输出绑定。
        /// </summary>
        /// <param name="texId">纹理 ID</param>
        public void UnbindOutputTexture(byte texId)
        {
            _outputTextures.Remove(texId);
        }

        /// <summary>
        /// 获取所有已接收纹理公告的诊断信息列表。
        /// </summary>
        /// <returns>纹理信息列表</returns>
        public List<DiagTextureInfo> GetDiagTextureList()
        {
            List<DiagTextureInfo> list = new List<DiagTextureInfo>();
            foreach (KeyValuePair<byte, TextureMeta> kv in _meta)
            {
                list.Add(new DiagTextureInfo
                {
                    TexId = kv.Key,
                    Width = kv.Value.Width,
                    Height = kv.Value.Height
                });
            }
            return list;
        }

        #endregion

        #region Unity 生命周期

        void Update()
        {
            if (!_connected) return;

            _downRecvBandwidth.Sample();
            _downProcBandwidth.Sample();

            // 从并发队列中取出所有已接收的帧到批处理列表
            while (_frameQueue.TryDequeue(out FrameEntry entry))
            {
                _batch.Add(entry.data);
                _netLagMs = entry.netLagMs;
                long now = Stopwatch.GetTimestamp();
                _localLagMs = (now - entry.recvTimestamp) * 1000f / Stopwatch.Frequency;
                _lastRecvWatchTimestamp = now;
            }
            if (_batch.Count == 0) return;

            _lastBatchSize = _batch.Count;

            int dirtyReceived = 0;
            foreach (byte[] pkt in _batch)
            {
                byte frameType = pkt[0];

                // 纹理公告帧单独处理
                if (frameType == (byte)FrameType.TextureAnnounce)
                {
                    ProcessAnnounce(pkt);
                    continue;
                }

                // 增量帧：如果已压缩则先解压
                byte[] raw = FrameCodec.IsCompressed(pkt) ? UncompressPacket(pkt) : pkt;

                if (raw[0] != (byte)FrameType.DeltaFrame) continue;

                byte texId = FrameCodec.GetTexId(raw);
                ushort tc = FrameCodec.GetTileCount(raw);
                dirtyReceived += tc;

                // 提取每个瓦片的索引，用于触发事件
                int[] indices = new int[tc];
                int tileBytes = FrameCodec.GetBytesPerTile();
                int pos = FrameCodec.TilePayloadOffset;
                for (int j = 0; j < tc; j++)
                {
                    indices[j] = BitConverter.ToInt32(raw, pos);
                    pos += 4 + tileBytes;
                }

                OnDirtyTilesApplied?.Invoke(texId, indices);

                // 通过 GPU 将瓦片数据应用到输出 RT 上
                ApplyPacket(texId, raw);
            }
            DirtyTilesReceived = dirtyReceived;

            _batch.Clear();
        }

        void OnDestroy()
        {
            Disconnect();
            ReleasePayloadBuffer();
            _meta.Clear();
            _outputTextures.Clear();
        }

        #endregion

        #region 内部结构体

        /// <summary>
        /// 接收到的帧条目。包含帧数据和接收时间戳，用于批量处理和延迟计算。
        /// </summary>
        private struct FrameEntry
        {
            public byte[] data;
            /// <summary>接收时刻的 Stopwatch 时间戳</summary>
            public long recvTimestamp;
            /// <summary>网络延迟（ms），基于发送端时间戳计算</summary>
            public float netLagMs;
        }

        /// <summary>
        /// 纹理元数据：记录从发送端公告中获取的纹理尺寸。
        /// </summary>
        private struct TextureMeta
        {
            public ushort Width;
            public ushort Height;
        }

        #endregion

        #region 序列化与私有字段

        [SerializeField] private ComputeShader _tileApplyShader;

        private TcpClient _tcpClient;
        private NetworkStream _stream;
        private Thread _receiveThread;
        // 使用无锁并发队列收集接收线程的数据，主线程轮询消费
        private ConcurrentQueue<FrameEntry> _frameQueue = new ConcurrentQueue<FrameEntry>();
        private bool _connected;
        private bool _running;

        // 每帧批量处理：先将帧数据收集到 _batch，然后批量 Apply
        private List<byte[]> _batch = new List<byte[]>();
        private int _lastBatchSize;

        // GPU 端用于传递瓦片数据的 ComputeBuffer
        private ComputeBuffer _payloadBuffer;
        private int _kApplyDelta;
        private Dictionary<byte, TextureMeta> _meta = new Dictionary<byte, TextureMeta>();
        private Dictionary<byte, RenderTexture> _outputTextures = new Dictionary<byte, RenderTexture>();
        /// <summary>连接时指定的订阅纹理 ID 数组</summary>
        private byte[] _subscribedTexIds;

        private float _netLagMs;
        private float _localLagMs;
        /// <summary>最近一次接收数据的 Stopwatch 时间戳，用于计算静默时间</summary>
        private long _lastRecvWatchTimestamp;

        private BandwidthMeter _downRecvBandwidth = new BandwidthMeter();
        private BandwidthMeter _downProcBandwidth = new BandwidthMeter();

        #endregion

        #region 纹理元数据

        /// <summary>
        /// 处理纹理公告帧：解析并记录纹理元数据。
        /// </summary>
        void ProcessAnnounce(byte[] packet)
        {
            if (!FrameCodec.TryParseTextureAnnounce(packet, out byte texId, out ushort texW, out ushort texH))
                return;

            _meta[texId] = new TextureMeta { Width = texW, Height = texH };
            Debug.Log($"MonoRTStreamReceiver: TextureAnnounce texId={texId} ({texW}x{texH})");
            OnTextureAnnounce?.Invoke(texId, texW, texH);
        }

        #endregion

        #region 帧接收与解压

        // 接收线程主循环：按 4 字节长度头 + 数据体格式读取帧
        void ReceiveLoop()
        {
            byte[] lenBuf = new byte[4];
            while (_running && _tcpClient != null && _tcpClient.Connected)
            {
                try
                {
                    if (!ReadExact(_stream, lenBuf, 0, 4)) break;
                    int frameLen = BitConverter.ToInt32(lenBuf, 0);
                    if (frameLen <= 0) break;

                    byte[] frameData = new byte[frameLen];
                    if (!ReadExact(_stream, frameData, 0, frameLen)) break;
                    _downRecvBandwidth.Add(frameLen);
                    // 计算网络延迟：当前时间减去发送端写入的时间戳
                    long sendTicks = FrameCodec.GetTimestamp(frameData);
                    float netLagMs = (DateTime.UtcNow.Ticks - sendTicks) / (float)TimeSpan.TicksPerMillisecond;
                    _frameQueue.Enqueue(new FrameEntry { data = frameData, recvTimestamp = Stopwatch.GetTimestamp(), netLagMs = netLagMs });
                }
                catch
                {
                    break;
                }
            }
            _connected = false;
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

        /// <summary>
        /// 解压压缩帧包：提取压缩块，LZ4 解压，重建完整帧包。
        /// </summary>
        byte[] UncompressPacket(byte[] packet)
        {
            // 从帧头读取压缩后的负载长度
            int comprLen = BitConverter.ToInt32(packet, 3);
            byte[] comprBlock = new byte[comprLen];
            Buffer.BlockCopy(packet, FrameCodec.HeaderSize, comprBlock, 0, comprLen);
            byte[] payload = LZ4.Unwrap(comprBlock, out int comprSz, out int decompSz);
            FrameCodec.LastDecodeComprBytes = comprSz;
            FrameCodec.LastDecodeDecompBytes = decompSz;

            // 重建帧包：保留原头部，替换负载为解压后数据，清除压缩标志
            byte[] newPacket = new byte[FrameCodec.HeaderSize + payload.Length];
            Buffer.BlockCopy(packet, 0, newPacket, 0, FrameCodec.HeaderSize);

            ushort flags = BitConverter.ToUInt16(packet, 1);
            flags &= 0x7FFF;    // 清除压缩标志位
            BitConverter.GetBytes(flags).CopyTo(newPacket, 1);
            BitConverter.GetBytes((uint)payload.Length).CopyTo(newPacket, 3);
            Buffer.BlockCopy(payload, 0, newPacket, FrameCodec.HeaderSize, payload.Length);
            return newPacket;
        }

        #endregion

        #region GPU应用

        /// <summary>
        /// 将解码后的帧数据通过 GPU ComputeShader 应用到输出纹理。
        /// </summary>
        void ApplyPacket(byte texId, byte[] packet)
        {
            _downProcBandwidth.Add(packet.Length);
            TryApplyGpu(texId, packet);
        }

        /// <summary>
        /// 使用 GPU ComputeShader 将瓦片数据写入输出 RenderTexture。
        /// </summary>
        bool TryApplyGpu(byte texId, byte[] packet)
        {
            if (!_meta.TryGetValue(texId, out TextureMeta meta))
                return true;

            if (!_outputTextures.TryGetValue(texId, out RenderTexture rt) || rt == null)
                return false;

            // 输出纹理需启用 RandomWrite 以支持 ComputeShader 写入
            if (!rt.enableRandomWrite)
            {
                ReleasePayloadBuffer();
                return false;
            }

            int tileCount = FrameCodec.GetTileCount(packet);
            if (tileCount == 0) return true;

            int effectivePayloadLen = packet.Length - FrameCodec.TilePayloadOffset;
            EnsurePayloadBuffer(meta.Width, meta.Height);
            if (effectivePayloadLen > _payloadBuffer.count * 4) return true;

            // 将瓦片负载数据上传到 GPU
            _payloadBuffer.SetData(packet, FrameCodec.TilePayloadOffset, 0, effectivePayloadLen);

            SetGpuParams(meta.Width, meta.Height);
            _tileApplyShader.SetBuffer(_kApplyDelta, "_Payload", _payloadBuffer);
            _tileApplyShader.SetTexture(_kApplyDelta, "_OutRT", rt);
            // 每个瓦片一个线程组
            _tileApplyShader.Dispatch(_kApplyDelta, tileCount, 1, 1);
            return true;
        }

        /// <summary>
        /// 设置 ComputeShader 的纹理和瓦片参数。
        /// </summary>
        void SetGpuParams(int width, int height)
        {
            int tileSize = FrameCodec.TileSize;
            if (tileSize > width) tileSize = width;

            _tileApplyShader.SetInt("_TexWidth", width);
            _tileApplyShader.SetInt("_TexHeight", height);
            _tileApplyShader.SetInt("_TileSize", tileSize);
            _tileApplyShader.SetInt("_TilesX", width / tileSize);
            // _FlipY = 0：不翻转 Y 轴（保持正常的图像方向）
            _tileApplyShader.SetInt("_FlipY", 0);
        }

        /// <summary>
        /// 确保 GPU 负载缓冲区能够容纳当前纹理所需的全部瓦片数据。
        /// </summary>
        void EnsurePayloadBuffer(int width, int height)
        {
            int tileSize = FrameCodec.TileSize;
            if (tileSize > width) tileSize = width;
            int tileBytes = tileSize * tileSize * 4;
            int tileCount = (width / tileSize) * (height / tileSize);
            // 缓冲容量 = 瓦片数 × (索引 4B + 数据 tileBytes)
            int capBytes = tileCount * (4 + tileBytes);

            // 缓冲区已足够大则复用
            if (_payloadBuffer != null && _payloadBuffer.count * 4 >= capBytes) return;

            ReleasePayloadBuffer();
            _payloadBuffer = new ComputeBuffer(capBytes / 4, 4, ComputeBufferType.Raw);
        }

        /// <summary>
        /// 释放 GPU 负载缓冲区。
        /// </summary>
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

        #endregion
    }
}
