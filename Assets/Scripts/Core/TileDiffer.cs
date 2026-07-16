using System;
using System.Collections.Generic;
using Unity.Collections;
using UnityEngine;
using UnityEngine.Rendering;

public class TileDiffer
{
    public const int TILE_SIZE = 16;
    public RenderTexture RT { get; private set; }

    private int _tilesX, _tilesY;
    private int _totalTiles;

    private ulong[] _prevHashes;

    private bool _useGPU;
    private ComputeShader _computeShader;
    private int _hashKernel;
    private ComputeBuffer _prevHashLowA, _prevHashHighA;
    private ComputeBuffer _prevHashLowB, _prevHashHighB;
    private ComputeBuffer _currHashLowA, _currHashHighA;
    private ComputeBuffer _currHashLowB, _currHashHighB;
    private ComputeBuffer _dirtyFlagBuffer;
    private bool _pingPong;

    private List<DirtyTile> _dirtyTiles = new List<DirtyTile>();
    private byte[] _rawData;
    private bool _hasResults;
    private bool _requestInFlight;
    private bool _fullRTReady;
    private bool _gpuReady;
    private bool _gpuFailed;
    private uint[] _dirtyFlags;

    public byte[] LatestRawData => _rawData;
    public int TilesX => _tilesX;
    public int TilesY => _tilesY;

    public TileDiffer(RenderTexture rt, ComputeShader computeShader = null)
    {
        RT = rt;
        _tilesX = rt.width / TILE_SIZE;
        _tilesY = rt.height / TILE_SIZE;
        _totalTiles = _tilesX * _tilesY;

        _useGPU = SystemInfo.supportsComputeShaders && SystemInfo.supportsAsyncGPUReadback && computeShader != null;
        if (_useGPU)
        {
            _computeShader = computeShader;

            if (_useGPU)
            {
                _hashKernel = _computeShader.FindKernel("ComputeHashes");
                _prevHashLowA = new ComputeBuffer(_totalTiles, sizeof(uint));
                _prevHashHighA = new ComputeBuffer(_totalTiles, sizeof(uint));
                _prevHashLowB = new ComputeBuffer(_totalTiles, sizeof(uint));
                _prevHashHighB = new ComputeBuffer(_totalTiles, sizeof(uint));
                _currHashLowA = new ComputeBuffer(_totalTiles, sizeof(uint));
                _currHashHighA = new ComputeBuffer(_totalTiles, sizeof(uint));
                _currHashLowB = new ComputeBuffer(_totalTiles, sizeof(uint));
                _currHashHighB = new ComputeBuffer(_totalTiles, sizeof(uint));
                _dirtyFlagBuffer = new ComputeBuffer(_totalTiles, sizeof(uint));
                _dirtyFlags = new uint[_totalTiles];
                return;
            }
        }

        _prevHashes = new ulong[_totalTiles];
    }

    public void Update()
    {
        if (!_requestInFlight)
        {
            _requestInFlight = true;
            _fullRTReady = false;
            _gpuReady = false;
            _gpuFailed = false;

            if (_useGPU)
                DispatchGPU();

            AsyncGPUReadback.Request(RT, 0, TextureFormat.RGBA32, OnFullRTReadback);
        }
    }

    void DispatchGPU()
    {
        ComputeBuffer prevLow = _pingPong ? _prevHashLowB : _prevHashLowA;
        ComputeBuffer prevHigh = _pingPong ? _prevHashHighB : _prevHashHighA;
        ComputeBuffer currLow = _pingPong ? _currHashLowB : _currHashLowA;
        ComputeBuffer currHigh = _pingPong ? _currHashHighB : _currHashHighA;

        _computeShader.SetTexture(_hashKernel, "_SourceTex", RT);
        _computeShader.SetInt("_TilesX", _tilesX);
        _computeShader.SetInt("_TilesY", _tilesY);
        _computeShader.SetInt("_TileSize", TILE_SIZE);
        _computeShader.SetBuffer(_hashKernel, "_PrevHashLow", prevLow);
        _computeShader.SetBuffer(_hashKernel, "_PrevHashHigh", prevHigh);
        _computeShader.SetBuffer(_hashKernel, "_CurrHashLow", currLow);
        _computeShader.SetBuffer(_hashKernel, "_CurrHashHigh", currHigh);
        _computeShader.SetBuffer(_hashKernel, "_DirtyFlags", _dirtyFlagBuffer);

        int groups = Mathf.CeilToInt(_totalTiles / 64f);
        _computeShader.Dispatch(_hashKernel, groups, 1, 1);

        AsyncGPUReadback.Request(_dirtyFlagBuffer, OnGPUReadback);
    }

    void OnGPUReadback(AsyncGPUReadbackRequest request)
    {
        if (request.hasError)
        {
            _gpuFailed = true;
        }
        else
        {
            NativeArray<uint> data = request.GetData<uint>();
            data.CopyTo(_dirtyFlags);
        }
        _gpuReady = true;
        TryBuild();
    }

    void OnFullRTReadback(AsyncGPUReadbackRequest request)
    {
        _requestInFlight = false;
        if (request.hasError) return;

        NativeArray<byte> data = request.GetData<byte>();
        _rawData = new byte[data.Length];
        data.CopyTo(_rawData);
        _fullRTReady = true;
        TryBuild();
    }

    void TryBuild()
    {
        if (!_fullRTReady || !_gpuReady) return;

        if (_useGPU && !_gpuFailed)
            BuildDirtyTilesGPU();
        else
            ComputeDirtyTilesCPU();

        _hasResults = true;
    }

    void BuildDirtyTilesGPU()
    {
        _dirtyTiles.Clear();
        int rowBytes = _tilesX * TILE_SIZE * 4;
        int tileBytes = TILE_SIZE * TILE_SIZE * 4;

        for (int idx = 0; idx < _totalTiles; idx++)
        {
            if (_dirtyFlags[idx] == 0) continue;

            int tx = idx % _tilesX;
            int ty = idx / _tilesX;
            int srcStart = ty * TILE_SIZE * rowBytes + tx * TILE_SIZE * 4;

            byte[] tileData = new byte[tileBytes];
            for (int y = 0; y < TILE_SIZE; y++)
            {
                int srcRow = srcStart + y * rowBytes;
                Buffer.BlockCopy(_rawData, srcRow, tileData, y * TILE_SIZE * 4, TILE_SIZE * 4);
            }
            _dirtyTiles.Add(new DirtyTile { index = idx, data = tileData });
        }

        _pingPong = !_pingPong;
    }

    void ComputeDirtyTilesCPU()
    {
        _dirtyTiles.Clear();
        int rowBytes = _tilesX * TILE_SIZE * 4;
        int tileBytes = TILE_SIZE * TILE_SIZE * 4;

        for (int ty = 0; ty < _tilesY; ty++)
        {
            for (int tx = 0; tx < _tilesX; tx++)
            {
                int idx = ty * _tilesX + tx;
                int srcStart = ty * TILE_SIZE * rowBytes + tx * TILE_SIZE * 4;

                byte[] tileData = new byte[tileBytes];
                for (int y = 0; y < TILE_SIZE; y++)
                {
                    int srcRow = srcStart + y * rowBytes;
                    Buffer.BlockCopy(_rawData, srcRow, tileData, y * TILE_SIZE * 4, TILE_SIZE * 4);
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

    public void Release()
    {
        _prevHashLowA?.Release();
        _prevHashHighA?.Release();
        _prevHashLowB?.Release();
        _prevHashHighB?.Release();
        _currHashLowA?.Release();
        _currHashHighA?.Release();
        _currHashLowB?.Release();
        _currHashHighB?.Release();
        _dirtyFlagBuffer?.Release();
    }
}
