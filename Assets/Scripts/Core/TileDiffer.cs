using System;
using System.Collections.Generic;
using Unity.Collections;
using UnityEngine;
using UnityEngine.Rendering;

public class TileDiffer
{
    public int tileSize = 16;
    public RenderTexture RT { get; private set; }

    private int _tilesX, _tilesY;
    private ulong[] _prevHashes;
    private List<DirtyTile> _dirtyTiles = new List<DirtyTile>();
    private byte[] _rawData;
    private bool _hasResults;
    private bool _requestInFlight;

    public byte[] LatestRawData => _rawData;
    public int TilesX => _tilesX;
    public int TilesY => _tilesY;

    public TileDiffer(RenderTexture rt)
    {
        RT = rt;
        _tilesX = rt.width / tileSize;
        _tilesY = rt.height / tileSize;
        _prevHashes = new ulong[_tilesX * _tilesY];
    }

    public void Update()
    {
        if (!_requestInFlight)
        {
            _requestInFlight = true;
            AsyncGPUReadback.Request(RT, 0, TextureFormat.RGBA32, OnReadback);
        }
    }

    private void OnReadback(AsyncGPUReadbackRequest request)
    {
        _requestInFlight = false;
        if (request.hasError) return;

        NativeArray<byte> data = request.GetData<byte>();
        _rawData = new byte[data.Length];
        data.CopyTo(_rawData);

        ComputeDirtyTiles();
        _hasResults = true;
    }

    private void ComputeDirtyTiles()
    {
        _dirtyTiles.Clear();
        int rowBytes = _tilesX * tileSize * 4;
        int tileBytes = tileSize * tileSize * 4;

        for (int ty = 0; ty < _tilesY; ty++)
        {
            for (int tx = 0; tx < _tilesX; tx++)
            {
                int idx = ty * _tilesX + tx;
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

    public bool TryGetDirtyTiles(out List<DirtyTile> tiles)
    {
        tiles = _dirtyTiles;
        bool had = _hasResults;
        _hasResults = false;
        return had;
    }
}
