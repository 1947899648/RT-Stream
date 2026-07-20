using System;
using System.Collections.Generic;
using Unity.Collections;
using UnityEngine;
using UnityEngine.Rendering;

public class GpuTileDiffer
{
    private RenderTexture RT { get; set; }
    private int TilesX { get; set; }
    private int TilesY { get; set; }
    public int DiagReadbackBytes { get; private set; }
    public int TexWidth => _texWidth;
    public int TexHeight => _texHeight;

    private int _texWidth, _texHeight;
    private int _tileSize, _tilePixels, _tileBytes, _tileCount;

    private ComputeShader _cs;
    private int _kClear, _kHash, _kPrepArgs, _kGatherDirty, _kGatherAll;

    private ComputeBuffer _prevHashesBuffer;
    private ComputeBuffer _idxBuffer;
    private ComputeBuffer _argsBuffer;
    private ComputeBuffer _gatherBuffer;

    private byte[] _idxRaw;
    private int _dirtyCount;
    private int _roundBytes;

    private bool _resultReady;
    private List<DirtyTile> _resultTiles;
    private byte[] _resultFullFrame;
    private List<DirtyTile> _dirtyTiles = new List<DirtyTile>();

    enum Phase { Idle, WaitingIdx, WaitingGather, WaitingKeyGather }
    private Phase _phase = Phase.Idle;

    private AsyncGPUReadbackRequest _idxRequest;
    private AsyncGPUReadbackRequest _gatherRequest;

    public GpuTileDiffer(RenderTexture rt, ComputeShader shader)
    {
        RT = rt;
        _cs = shader;
        _texWidth = rt.width;
        _texHeight = rt.height;
        _tileSize = FrameCodec.TileSize;
        if (_tileSize > _texWidth) _tileSize = _texWidth;
        TilesX = _texWidth / _tileSize;
        TilesY = _texHeight / _tileSize;
        _tilePixels = _tileSize * _tileSize;
        _tileBytes = _tilePixels * 4;
        _tileCount = TilesX * TilesY;

        _kClear = _cs.FindKernel("KClear");
        _kHash = _cs.FindKernel("KHash");
        _kPrepArgs = _cs.FindKernel("KPrepArgs");
        _kGatherDirty = _cs.FindKernel("KGatherDirty");
        _kGatherAll = _cs.FindKernel("KGatherAll");

        _prevHashesBuffer = new ComputeBuffer(_tileCount * 2, sizeof(uint));
        _prevHashesBuffer.SetData(new uint[_tileCount * 2]);

        _idxBuffer = new ComputeBuffer(1 + _tileCount, sizeof(uint));
        _argsBuffer = new ComputeBuffer(3, sizeof(uint), ComputeBufferType.IndirectArguments);
        _gatherBuffer = new ComputeBuffer(_tileCount * _tilePixels, sizeof(uint));
    }

    public void Update(bool wantKeyFrame)
    {
        switch (_phase)
        {
            case Phase.WaitingIdx: PollIdx(); return;
            case Phase.WaitingGather: PollGather(); return;
            case Phase.WaitingKeyGather: PollKeyGather(); return;
        }

        if (_resultReady) return;

        _roundBytes = 0;

        _cs.SetInt("_TileSize", _tileSize);
        _cs.SetInt("_TilesX", TilesX);
        _cs.SetInt("_TilesY", TilesY);

        _cs.SetBuffer(_kClear, "_IdxBuffer", _idxBuffer);
        _cs.Dispatch(_kClear, 1, 1, 1);

        _cs.SetTexture(_kHash, "_RT", RT);
        _cs.SetBuffer(_kHash, "_PrevHashes", _prevHashesBuffer);
        _cs.SetBuffer(_kHash, "_IdxBuffer", _idxBuffer);
        _cs.Dispatch(_kHash, TilesX, TilesY, 1);

        if (wantKeyFrame)
        {
            _cs.SetTexture(_kGatherAll, "_RT", RT);
            _cs.SetBuffer(_kGatherAll, "_GatherBuffer", _gatherBuffer);
            _cs.Dispatch(_kGatherAll, TilesX, TilesY, 1);

            _gatherRequest = AsyncGPUReadback.Request(_gatherBuffer);
            _phase = Phase.WaitingKeyGather;
        }
        else
        {
            _cs.SetBuffer(_kPrepArgs, "_IdxBuffer", _idxBuffer);
            _cs.SetBuffer(_kPrepArgs, "_ArgsBuffer", _argsBuffer);
            _cs.Dispatch(_kPrepArgs, 1, 1, 1);

            _cs.SetTexture(_kGatherDirty, "_RT", RT);
            _cs.SetBuffer(_kGatherDirty, "_IdxBuffer", _idxBuffer);
            _cs.SetBuffer(_kGatherDirty, "_GatherBuffer", _gatherBuffer);
            _cs.DispatchIndirect(_kGatherDirty, _argsBuffer);

            _idxRequest = AsyncGPUReadback.Request(_idxBuffer);
            _phase = Phase.WaitingIdx;
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
        dirtyTiles = _resultTiles;
        fullFrame = _resultFullFrame;
        _resultTiles = null;
        _resultFullFrame = null;
        return true;
    }

    void PollIdx()
    {
        if (!_idxRequest.done) return;
        if (_idxRequest.hasError) { _phase = Phase.Idle; return; }

        _idxRaw = _idxRequest.GetData<byte>().ToArray();
        _roundBytes = _idxRaw.Length;
        _dirtyCount = BitConverter.ToInt32(_idxRaw, 0);
        if (_dirtyCount > _tileCount) _dirtyCount = _tileCount;

        if (_dirtyCount > 0)
        {
            _gatherRequest = AsyncGPUReadback.Request(_gatherBuffer, _dirtyCount * _tilePixels * sizeof(uint), 0);
            _phase = Phase.WaitingGather;
            return;
        }

        _dirtyTiles.Clear();
        _resultTiles = _dirtyTiles;
        _resultFullFrame = null;
        DiagReadbackBytes = _roundBytes;
        _resultReady = true;
        _phase = Phase.Idle;
    }

    void PollGather()
    {
        if (!_gatherRequest.done) return;
        if (_gatherRequest.hasError) { _phase = Phase.Idle; return; }

        NativeArray<byte> gather = _gatherRequest.GetData<byte>();
        _roundBytes += gather.Length;

        _dirtyTiles.Clear();
        int gatherLen = gather.Length;

        for (int i = 0; i < _dirtyCount; i++)
        {
            int srcOff = i * _tileBytes;
            if (srcOff + _tileBytes > gatherLen) break;

            int tileIndex = BitConverter.ToInt32(_idxRaw, 4 + i * 4);
            byte[] tileData = new byte[_tileBytes];
            NativeArray<byte>.Copy(gather, srcOff, tileData, 0, _tileBytes);
            _dirtyTiles.Add(new DirtyTile { index = tileIndex, data = tileData });
        }

        _resultTiles = _dirtyTiles;
        _resultFullFrame = null;
        DiagReadbackBytes = _roundBytes;
        _resultReady = true;
        _phase = Phase.Idle;
    }

    void PollKeyGather()
    {
        if (!_gatherRequest.done) return;
        if (_gatherRequest.hasError) { _phase = Phase.Idle; return; }

        NativeArray<byte> gather = _gatherRequest.GetData<byte>();
        _roundBytes = gather.Length;

        byte[] full = new byte[_roundBytes];
        NativeArray<byte>.Copy(gather, 0, full, 0, _roundBytes);

        _resultTiles = null;
        _resultFullFrame = full;
        DiagReadbackBytes = _roundBytes;
        _resultReady = true;
        _phase = Phase.Idle;
    }

    public void Dispose()
    {
        _prevHashesBuffer?.Release();
        _idxBuffer?.Release();
        _argsBuffer?.Release();
        _gatherBuffer?.Release();
        _prevHashesBuffer = null;
        _idxBuffer = null;
        _argsBuffer = null;
        _gatherBuffer = null;
    }
}
