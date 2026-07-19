using System;
using System.Collections.Generic;
using UnityEngine;

public class StreamHost : MonoBehaviour
{
    [SerializeField] private ComputeShader _tileDiffShader;

    private Telepathy.Server _server;
    private HashSet<int> _connectedIds = new HashSet<int>();
    private HashSet<int> _needKeyFrame = new HashSet<int>();
    private GpuTileDiffer _tileSource;
    private int _texWidth, _texHeight;

    private const int MaxMsgSize = 256 * 1024;

    public int ClientCount => _connectedIds.Count;

    public int DiagReadbackBytes => _tileSource != null ? _tileSource.DiagReadbackBytes : 0;
    public int DiagDirtyTiles { get; private set; }

    public event System.Action<int[]> OnDirtyTilesDetected;

    private BandwidthMeter _rawDirtyBandwidth = new BandwidthMeter();
    private BandwidthMeter _upEncBandwidth = new BandwidthMeter();
    private BandwidthMeter _upSendBandwidth = new BandwidthMeter();
    public float RawDirtyMBps => _rawDirtyBandwidth.MBps;
    public float UpEncMBps => _upEncBandwidth.MBps;
    public float UpSendMBps => _upSendBandwidth.MBps;

    void Start()
    {
        DrawingCanvas canvas = FindObjectOfType<DrawingCanvas>();
        _texWidth = SceneConfig.TextureSize;
        _texHeight = SceneConfig.TextureSize;

        _tileSource = new GpuTileDiffer(canvas.CanvasTexture, _tileDiffShader);

        _server = new Telepathy.Server(MaxMsgSize);
        _server.OnConnected = (connId, ip) =>
        {
            _connectedIds.Add(connId);
            _needKeyFrame.Add(connId);
        };
        _server.OnData = (connId, data) => { };
        _server.OnDisconnected = (connId) =>
        {
            _connectedIds.Remove(connId);
            _needKeyFrame.Remove(connId);
        };
        _server.Start(SceneConfig.Port);
    }

    void Update()
    {
        _server.Tick(100);

        bool wantKeyFrame = false;
        foreach (int _ in _needKeyFrame) { wantKeyFrame = true; break; }
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

        if (_connectedIds.Count == 0) return;

        if (fullFrame != null)
        {
            int rawBytes = 8 + fullFrame.Length;
            _rawDirtyBandwidth.Add(rawBytes);
            SendTiledKeyFrame(fullFrame);
            _needKeyFrame.Clear();
        }
        else if (dirtyTiles != null && dirtyTiles.Count > 0)
        {
            int tileDataBytes = SceneConfig.TileSize * SceneConfig.TileSize * 4;
            int rawBytes = dirtyTiles.Count * (4 + tileDataBytes);
            _rawDirtyBandwidth.Add(rawBytes);
            byte[] deltaPacket = FrameCodec.EncodeDeltaFrame(dirtyTiles);
            _upEncBandwidth.Add(deltaPacket.Length);
            _upSendBandwidth.Add(Broadcast(deltaPacket));
        }

        _rawDirtyBandwidth.Sample();
        _upEncBandwidth.Sample();
        _upSendBandwidth.Sample();
    }

    public string GetClientDiagnostics()
    {
        return null;
    }

    void SendTiledKeyFrame(byte[] fullFrame)
    {
        int tileSize = SceneConfig.TileSize;
        int tilesX = _texWidth / tileSize;
        int tilesY = _texHeight / tileSize;
        int tileBytes = tileSize * tileSize * 4;
        int totalTiles = tilesX * tilesY;
        int tileRowBytes = tileSize * 4;
        int maxPerBatch = (MaxMsgSize - FrameCodec.HeaderSize - 4) / (4 + tileBytes);
        if (maxPerBatch < 1) maxPerBatch = 1;

        int sent = 0;

        List<DirtyTile> batch = new List<DirtyTile>(maxPerBatch);
        for (int tileIndex = 0; tileIndex < totalTiles; tileIndex++)
        {
            int tx = tileIndex % tilesX;
            int ty = tileIndex / tilesX;
            byte[] tileData = new byte[tileBytes];
            for (int row = 0; row < tileSize; row++)
            {
                int srcOffset = ((ty * tileSize + row) * _texWidth + tx * tileSize) * 4;
                Buffer.BlockCopy(fullFrame, srcOffset, tileData, row * tileRowBytes, tileRowBytes);
            }
            batch.Add(new DirtyTile { index = tileIndex, data = tileData });

            if (batch.Count >= maxPerBatch || tileIndex == totalTiles - 1)
            {
                byte[] packet = FrameCodec.EncodeDeltaFrame(batch);
                _upEncBandwidth.Add(packet.Length);
                sent += Broadcast(packet);
                batch.Clear();
            }
        }

        _upSendBandwidth.Add(sent);
    }

    int Broadcast(byte[] packet)
    {
        int totalSent = 0;
        ArraySegment<byte> seg = new ArraySegment<byte>(packet);
        foreach (int connId in _connectedIds)
        {
            if (_server.Send(connId, seg))
                totalSent += packet.Length + 4;
        }
        return totalSent;
    }

    void OnDestroy()
    {
        _server?.Stop();
        _tileSource?.Dispose();
    }
}
