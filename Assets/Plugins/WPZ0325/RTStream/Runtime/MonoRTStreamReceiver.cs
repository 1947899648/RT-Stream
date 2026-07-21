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
        /// <summary>本地排队延迟（毫秒）</summary>
        public float LocalLagMs => _localLagMs;
        /// <summary>远端发送端主机地址</summary>
        public string RemoteHost { get; private set; }
        /// <summary>远端发送端端口</summary>
        public int RemotePort { get; private set; }

        /// <summary>下行接收带宽（MB/s）</summary>
        public float DownRecvMBps => _downRecvBandwidth.MBps;
        /// <summary>下行处理带宽（MB/s，解码+GPU应用）</summary>
        public float DownProcMBps => _downProcBandwidth.MBps;

        /// <summary>距离最后一次收到数据的静默时间（毫秒）</summary>
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

        /// <summary>成功连接到 Host 时触发</summary>
        public event System.Action OnConnectedToHost;
        /// <summary>与 Host 断开时触发</summary>
        public event System.Action OnDisconnectedFromHost;
        /// <summary>连接 Host 失败时触发</summary>
        public event System.Action<string> OnConnectionFailed;
        /// <summary>收到纹理公告帧（订阅纹理成功）时触发</summary>
        public event System.Action<string, int, int> OnRenderTextureSubscribed;
        /// <summary>取消订阅纹理时触发（收到 TextureRemove 帧）</summary>
        public event System.Action<string> OnRenderTextureUnsubscribed;
        /// <summary>纹理脏瓦片数据接收并应用到输出 RT 时触发</summary>
        public event System.Action<string, int[]> OnRenderTextureDirtyTilesReceived;

        #endregion

        #region 公开方法

        /// <summary>连接到发送端并开始接收数据</summary>
        public void Connect(string host, int port, string[] subscribedTexIds = null)
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

                byte[] handshake = FrameCodec.EncodeSubscribeReq(_subscribedTexIds);
                _stream.Write(handshake, 0, handshake.Length);

                _connected = true;
                _running = true;
                _receiveThread = new Thread(ReceiveLoop) { IsBackground = true };
                _receiveThread.Start();
                OnConnectedToHost?.Invoke();
            }
            catch (Exception e)
            {
                Debug.LogError($"MonoRTStreamReceiver connect failed: {e.Message}");
                Close();
                OnConnectionFailed?.Invoke(e.Message);
            }
        }

        /// <summary>断开与发送端的连接并清理资源</summary>
        public void Disconnect()
        {
            _running = false;
            _connected = false;
            _receiveThread?.Join(500);
            Close();
            while (_frameQueue.TryDequeue(out FrameEntry _)) { }
            _batch.Clear();
            ReleasePayloadBuffer();
            _meta.Clear();
            OnDisconnectedFromHost?.Invoke();
        }

        /// <summary>将指定纹理绑定到输出 RenderTexture</summary>
        public void BindOutputTexture(string texId, RenderTexture rt)
        {
            _outputTextures[texId] = rt;
        }

        /// <summary>解除指定纹理的输出绑定</summary>
        public void UnbindOutputTexture(string texId)
        {
            _outputTextures.Remove(texId);
        }

        #endregion

        #region Unity 生命周期

        void Update()
        {
            while (_mainThreadActions.TryDequeue(out System.Action action))
                action();

            if (!_connected) return;

            _downRecvBandwidth.Sample();
            _downProcBandwidth.Sample();

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

                if (frameType == (byte)FrameType.TextureAnnounce)
                {
                    ProcessAnnounce(pkt);
                    continue;
                }

                byte[] raw = FrameCodec.IsCompressed(pkt) ? UncompressPacket(pkt) : pkt;

                if (raw[0] != (byte)FrameType.DeltaFrame) continue;

                string texId = FrameCodec.GetTexId(raw);
                ushort tc = FrameCodec.GetTileCount(raw);
                dirtyReceived += tc;

                int[] indices = new int[tc];
                int tileBytes = FrameCodec.GetBytesPerTile();
                int pos = FrameCodec.GetTilePayloadOffset(raw);
                for (int j = 0; j < tc; j++)
                {
                    indices[j] = BitConverter.ToInt32(raw, pos);
                    pos += 4 + tileBytes;
                }

                OnRenderTextureDirtyTilesReceived?.Invoke(texId, indices);

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

        private struct FrameEntry
        {
            public byte[] data;
            public long recvTimestamp;
            public float netLagMs;
        }

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
        private ConcurrentQueue<FrameEntry> _frameQueue = new ConcurrentQueue<FrameEntry>();
        private bool _connected;
        private bool _running;

        private List<byte[]> _batch = new List<byte[]>();
        private int _lastBatchSize;

        private ComputeBuffer _payloadBuffer;
        private int _kApplyDelta;
        private Dictionary<string, TextureMeta> _meta = new Dictionary<string, TextureMeta>();
        private Dictionary<string, RenderTexture> _outputTextures = new Dictionary<string, RenderTexture>();
        private string[] _subscribedTexIds;

        private float _netLagMs;
        private float _localLagMs;
        private long _lastRecvWatchTimestamp;

        private BandwidthMeter _downRecvBandwidth = new BandwidthMeter();
        private BandwidthMeter _downProcBandwidth = new BandwidthMeter();

        private ConcurrentQueue<System.Action> _mainThreadActions = new ConcurrentQueue<System.Action>();

        #endregion

        #region 纹理元数据

        void ProcessAnnounce(byte[] packet)
        {
            if (!FrameCodec.TryParseTextureAnnounce(packet, out string texId, out ushort texW, out ushort texH))
                return;

            _meta[texId] = new TextureMeta { Width = texW, Height = texH };
            Debug.Log($"MonoRTStreamReceiver: TextureAnnounce texId=\"{texId}\" ({texW}x{texH})");
            OnRenderTextureSubscribed?.Invoke(texId, texW, texH);
        }

        #endregion

        #region 帧接收与解压

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
            _mainThreadActions.Enqueue(() => OnDisconnectedFromHost?.Invoke());
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

        byte[] UncompressPacket(byte[] packet)
        {
            int comprLen = BitConverter.ToInt32(packet, 3);
            byte[] comprBlock = new byte[comprLen];
            Buffer.BlockCopy(packet, FrameCodec.HeaderSize, comprBlock, 0, comprLen);
            byte[] payload = LZ4.Unwrap(comprBlock, out int comprSz, out int decompSz);
            FrameCodec.LastDecodeComprBytes = comprSz;
            FrameCodec.LastDecodeDecompBytes = decompSz;

            byte[] newPacket = new byte[FrameCodec.HeaderSize + payload.Length];
            Buffer.BlockCopy(packet, 0, newPacket, 0, FrameCodec.HeaderSize);

            ushort flags = BitConverter.ToUInt16(packet, 1);
            flags &= 0x7FFF;
            BitConverter.GetBytes(flags).CopyTo(newPacket, 1);
            BitConverter.GetBytes((uint)payload.Length).CopyTo(newPacket, 3);
            Buffer.BlockCopy(payload, 0, newPacket, FrameCodec.HeaderSize, payload.Length);
            return newPacket;
        }

        #endregion

        #region GPU应用

        void ApplyPacket(string texId, byte[] packet)
        {
            _downProcBandwidth.Add(packet.Length);
            TryApplyGpu(texId, packet);
        }

        bool TryApplyGpu(string texId, byte[] packet)
        {
            if (!_meta.TryGetValue(texId, out TextureMeta meta))
                return true;

            if (!_outputTextures.TryGetValue(texId, out RenderTexture rt) || rt == null)
                return false;

            if (!rt.enableRandomWrite)
            {
                ReleasePayloadBuffer();
                return false;
            }

            int tileCount = FrameCodec.GetTileCount(packet);
            if (tileCount == 0) return true;

            int tilePayloadOffset = FrameCodec.GetTilePayloadOffset(packet);
            int effectivePayloadLen = packet.Length - tilePayloadOffset;
            EnsurePayloadBuffer(meta.Width, meta.Height);
            if (effectivePayloadLen > _payloadBuffer.count * 4) return true;

            _payloadBuffer.SetData(packet, tilePayloadOffset, 0, effectivePayloadLen);

            SetGpuParams(meta.Width, meta.Height);
            _tileApplyShader.SetBuffer(_kApplyDelta, "_Payload", _payloadBuffer);
            _tileApplyShader.SetTexture(_kApplyDelta, "_OutRT", rt);
            _tileApplyShader.Dispatch(_kApplyDelta, tileCount, 1, 1);
            return true;
        }

        void SetGpuParams(int width, int height)
        {
            int tileSize = FrameCodec.TileSize;
            if (tileSize > width) tileSize = width;

            _tileApplyShader.SetInt("_TexWidth", width);
            _tileApplyShader.SetInt("_TexHeight", height);
            _tileApplyShader.SetInt("_TileSize", tileSize);
            _tileApplyShader.SetInt("_TilesX", width / tileSize);
            _tileApplyShader.SetInt("_FlipY", 0);
        }

        void EnsurePayloadBuffer(int width, int height)
        {
            int tileSize = FrameCodec.TileSize;
            if (tileSize > width) tileSize = width;
            int tileBytes = tileSize * tileSize * 4;
            int tileCount = (width / tileSize) * (height / tileSize);
            int capBytes = tileCount * (4 + tileBytes);

            if (_payloadBuffer != null && _payloadBuffer.count * 4 >= capBytes) return;

            ReleasePayloadBuffer();
            _payloadBuffer = new ComputeBuffer(capBytes / 4, 4, ComputeBufferType.Raw);
        }

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
