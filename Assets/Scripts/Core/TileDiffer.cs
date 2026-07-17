using System;
using System.Collections.Generic;
using Unity.Collections;
using UnityEngine;
using UnityEngine.Rendering;

public class TileDiffer : ITileSource
{
    public int tileSize;
    public RenderTexture RT { get; private set; }
    public int TilesX { get; private set; }
    public int TilesY { get; private set; }
    public int DiagReadbackBytes { get; private set; }

    private ulong[] _prevHashes;
    private List<DirtyTile> _dirtyTiles = new List<DirtyTile>();
    private byte[] _rawData;
    private bool _requestInFlight;
    private bool _resultReady;
    private bool _wantKeyFrame;

    public TileDiffer(RenderTexture rt)
    {
        RT = rt;
        tileSize = SceneConfig.TileSize;
        if (tileSize > rt.width) tileSize = rt.width;
        TilesX = rt.width / tileSize;
        TilesY = rt.height / tileSize;
        _prevHashes = new ulong[TilesX * TilesY];
    }

    public void Update(bool wantKeyFrame)
    {
        _wantKeyFrame |= wantKeyFrame;

        if (!_requestInFlight && !_resultReady)
        {
            _requestInFlight = true;
            AsyncGPUReadback.Request(RT, 0, TextureFormat.RGBA32, OnReadback);
        }
    }

    public bool TryGetResult(out List<DirtyTile> dirtyTiles, out byte[] fullFrame)
    {
        if (!_resultReady)
        {
            dirtyTiles = null;
            fullFrame = null;
            return false;
        }

        _resultReady = false;
        if (_wantKeyFrame)
        {
            _wantKeyFrame = false;
            dirtyTiles = null;
            fullFrame = _rawData;
        }
        else
        {
            dirtyTiles = _dirtyTiles;
            fullFrame = null;
        }
        return true;
    }

    private void OnReadback(AsyncGPUReadbackRequest request)
    {
        _requestInFlight = false;
        if (request.hasError) return;

        NativeArray<byte> data = request.GetData<byte>();
        DiagReadbackBytes = data.Length;
        _rawData = new byte[data.Length];
        data.CopyTo(_rawData);

        ComputeDirtyTiles();
        _resultReady = true;
    }

    private void ComputeDirtyTiles()
    {
        _dirtyTiles.Clear();
        int rowBytes = TilesX * tileSize * 4;
        int tileBytes = tileSize * tileSize * 4;

        for (int ty = 0; ty < TilesY; ty++)
        {
            for (int tx = 0; tx < TilesX; tx++)
            {
                int idx = ty * TilesX + tx;
                int srcStart = ty * tileSize * rowBytes + tx * tileSize * 4;

                byte[] tileData = new byte[tileBytes];
                for (int y = 0; y < tileSize; y++)
                {
                    int srcRow = srcStart + y * rowBytes;
                    Buffer.BlockCopy(_rawData, srcRow, tileData, y * tileSize * 4, tileSize * 4);
                }

                ulong hash = FastHash.Compute(tileData, 0, tileBytes);
                if (hash != _prevHashes[idx])
                {
                    _prevHashes[idx] = hash;
                    _dirtyTiles.Add(new DirtyTile { index = idx, data = tileData });
                }
            }
        }
    }

    public void Dispose() { }
}
