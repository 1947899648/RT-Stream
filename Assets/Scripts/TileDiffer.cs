using System;
using System.Collections.Generic;
using System.Threading;
using UnityEngine;
using UnityEngine.Rendering;

public class TileDiffer
{
    public int tileSize = 16;
    public RenderTexture RT { get; private set; }

    private int tilesX, tilesY;
    private ulong[] prevHashes;
    private List<DirtyTile> dirtyTiles = new List<DirtyTile>();
    private byte[] rawData;
    private bool hasResults;
    private bool requestInFlight;

    public byte[] LatestRawData => rawData;
    public int TilesX => tilesX;
    public int TilesY => tilesY;

    public TileDiffer(RenderTexture rt)
    {
        RT = rt;
        tilesX = rt.width / tileSize;
        tilesY = rt.height / tileSize;
        prevHashes = new ulong[tilesX * tilesY];
    }

    public void Update()
    {
        if (!requestInFlight)
        {
            requestInFlight = true;
            AsyncGPUReadback.Request(RT, 0, TextureFormat.RGBA32, OnReadback);
        }
    }

    private void OnReadback(AsyncGPUReadbackRequest request)
    {
        requestInFlight = false;
        if (request.hasError) return;

        var data = request.GetData<byte>();
        rawData = new byte[data.Length];
        data.CopyTo(rawData);

        ComputeDirtyTiles();
        hasResults = true;
    }

    private void ComputeDirtyTiles()
    {
        dirtyTiles.Clear();
        int rowBytes = tilesX * tileSize * 4;
        int tileBytes = tileSize * tileSize * 4;

        for (int ty = 0; ty < tilesY; ty++)
        {
            for (int tx = 0; tx < tilesX; tx++)
            {
                int idx = ty * tilesX + tx;
                int srcStart = ty * tileSize * rowBytes + tx * tileSize * 4;

                byte[] tileData = new byte[tileBytes];
                for (int y = 0; y < tileSize; y++)
                {
                    int srcRow = srcStart + y * rowBytes;
                    Buffer.BlockCopy(rawData, srcRow, tileData, y * tileSize * 4, tileSize * 4);
                }

                ulong hash = FastHash.Compute(tileData, 0, tileBytes);
                if (hash != prevHashes[idx])
                {
                    prevHashes[idx] = hash;
                    dirtyTiles.Add(new DirtyTile { index = idx, data = tileData });
                }
            }
        }
    }

    public bool TryGetDirtyTiles(out List<DirtyTile> tiles)
    {
        tiles = dirtyTiles;
        bool had = hasResults;
        hasResults = false;
        return had;
    }
}
