using System;
using System.Collections.Generic;
using Unity.Collections;
using UnityEngine;
using UnityEngine.Rendering;

namespace WPZ0325.RTStream
{
    /// <summary>
    /// GPU 瓦片差异检测器。使用 ComputeShader 比较当前帧与前一帧的瓦片哈希，
    /// 检测脏瓦片并通过 AsyncGPUReadback 回读到 CPU。
    /// 支持增量差异检测和全帧（关键帧）回读两种模式。
    /// </summary>
    public class TileDiffer
    {
        /// <summary>目标渲染纹理</summary>
        private RenderTexture RT { get; set; }
        /// <summary>水平方向的瓦片数量</summary>
        private int TilesX { get; set; }
        /// <summary>垂直方向的瓦片数量</summary>
        private int TilesY { get; set; }

        /// <summary>诊断用：最近一轮回读的总字节数</summary>
        public int DiagReadbackBytes { get; private set; }
        /// <summary>纹理宽度（像素）</summary>
        public int TexWidth => _texWidth;
        /// <summary>纹理高度（像素）</summary>
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

        // 状态机阶段：控制 AsyncGPUReadback 的异步流程
        enum Phase { Idle, WaitingIdx, WaitingGather, WaitingKeyGather }
        private Phase _phase = Phase.Idle;

        private AsyncGPUReadbackRequest _idxRequest;
        private AsyncGPUReadbackRequest _gatherRequest;

        /// <summary>
        /// 初始化瓦片差异检测器。
        /// </summary>
        /// <param name="rt">目标渲染纹理</param>
        /// <param name="shader">用于差异检测的 ComputeShader</param>
        public TileDiffer(RenderTexture rt, ComputeShader shader)
        {
            RT = rt;
            _cs = shader;
            _texWidth = rt.width;
            _texHeight = rt.height;
            _tileSize = FrameCodec.TileSize;
            // 纹理尺寸小于瓦片尺寸时，将瓦片尺寸缩小至纹理宽度
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

            // 前一帧哈希缓冲区，每个瓦片 2 个 uint（用于双重哈希降低冲突）
            _prevHashesBuffer = new ComputeBuffer(_tileCount * 2, sizeof(uint));
            _prevHashesBuffer.SetData(new uint[_tileCount * 2]);

            // 索引缓冲区：[0]=脏瓦片数，[1..]=脏瓦片索引列表
            _idxBuffer = new ComputeBuffer(1 + _tileCount, sizeof(uint));
            // IndirectArguments 缓冲区，供 DispatchIndirect 使用
            _argsBuffer = new ComputeBuffer(3, sizeof(uint), ComputeBufferType.IndirectArguments);
            // 收集缓冲区：容纳所有瓦片的像素数据
            _gatherBuffer = new ComputeBuffer(_tileCount * _tilePixels, sizeof(uint));
        }

        /// <summary>
        /// 每帧调用，驱动差异检测状态机。
        /// </summary>
        /// <param name="wantKeyFrame">true 时跳过差异比较，直接回读全帧数据</param>
        public void Update(bool wantKeyFrame)
        {
            // 状态机：根据当前阶段轮询异步请求
            switch (_phase)
            {
                case Phase.WaitingIdx: PollIdx(); return;
                case Phase.WaitingGather: PollGather(); return;
                case Phase.WaitingKeyGather: PollKeyGather(); return;
            }

            // 上次结果未被取出时不发起新检测
            if (_resultReady) return;

            _roundBytes = 0;

            _cs.SetInt("_TileSize", _tileSize);
            _cs.SetInt("_TilesX", TilesX);
            _cs.SetInt("_TilesY", TilesY);

            // 步骤1：清除索引缓冲区
            _cs.SetBuffer(_kClear, "_IdxBuffer", _idxBuffer);
            _cs.Dispatch(_kClear, 1, 1, 1);

            // 步骤2：计算每个瓦片的哈希值，与前一帧比较，将脏瓦片索引写入 _idxBuffer
            _cs.SetTexture(_kHash, "_RT", RT);
            _cs.SetBuffer(_kHash, "_PrevHashes", _prevHashesBuffer);
            _cs.SetBuffer(_kHash, "_IdxBuffer", _idxBuffer);
            _cs.Dispatch(_kHash, TilesX, TilesY, 1);

            if (wantKeyFrame)
            {
                // 关键帧路径：跳过差异检测，直接收集全部瓦片数据
                _cs.SetTexture(_kGatherAll, "_RT", RT);
                _cs.SetBuffer(_kGatherAll, "_GatherBuffer", _gatherBuffer);
                _cs.Dispatch(_kGatherAll, TilesX, TilesY, 1);

                _gatherRequest = AsyncGPUReadback.Request(_gatherBuffer);
                _phase = Phase.WaitingKeyGather;
            }
            else
            {
                // 增量路径：准备 IndirectArguments，仅收集脏瓦片数据
                _cs.SetBuffer(_kPrepArgs, "_IdxBuffer", _idxBuffer);
                _cs.SetBuffer(_kPrepArgs, "_ArgsBuffer", _argsBuffer);
                _cs.Dispatch(_kPrepArgs, 1, 1, 1);

                _cs.SetTexture(_kGatherDirty, "_RT", RT);
                _cs.SetBuffer(_kGatherDirty, "_IdxBuffer", _idxBuffer);
                _cs.SetBuffer(_kGatherDirty, "_GatherBuffer", _gatherBuffer);
                // 使用间接调度，GPU 端决定实际需要处理的线程组数量
                _cs.DispatchIndirect(_kGatherDirty, _argsBuffer);

                _idxRequest = AsyncGPUReadback.Request(_idxBuffer);
                _phase = Phase.WaitingIdx;
            }
        }

        /// <summary>
        /// 尝试获取检测结果。调用后结果即被消费，下次调用返回 false 直到新结果就绪。
        /// </summary>
        /// <param name="dirtyTiles">脏瓦片列表（增量模式下有效）</param>
        /// <param name="fullFrame">全帧原始数据（关键帧模式下有效）</param>
        /// <returns>true 表示结果可用，false 表示检测尚未完成</returns>
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

        // 轮询索引回读请求完成
        void PollIdx()
        {
            if (!_idxRequest.done) return;
            if (_idxRequest.hasError) { _phase = Phase.Idle; return; }

            _idxRaw = _idxRequest.GetData<byte>().ToArray();
            _roundBytes = _idxRaw.Length;
            // 索引缓冲区首 4 字节为脏瓦片数量
            _dirtyCount = BitConverter.ToInt32(_idxRaw, 0);
            if (_dirtyCount > _tileCount) _dirtyCount = _tileCount;

            if (_dirtyCount > 0)
            {
                // 有脏瓦片：发起瓦片数据收集回读
                _gatherRequest = AsyncGPUReadback.Request(_gatherBuffer, _dirtyCount * _tilePixels * sizeof(uint), 0);
                _phase = Phase.WaitingGather;
                return;
            }

            // 无脏瓦片：直接完成
            _dirtyTiles.Clear();
            _resultTiles = _dirtyTiles;
            _resultFullFrame = null;
            DiagReadbackBytes = _roundBytes;
            _resultReady = true;
            _phase = Phase.Idle;
        }

        // 轮询脏瓦片数据回读请求完成
        void PollGather()
        {
            if (!_gatherRequest.done) return;
            if (_gatherRequest.hasError) { _phase = Phase.Idle; return; }

            NativeArray<byte> gather = _gatherRequest.GetData<byte>();
            _roundBytes += gather.Length;

            _dirtyTiles.Clear();
            int gatherLen = gather.Length;

            // 按脏瓦片数量遍历，从索引缓冲区读取瓦片索引，从收集缓冲区拷贝像素数据
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

        // 轮询关键帧全量回读请求完成
        void PollKeyGather()
        {
            if (!_gatherRequest.done) return;
            if (_gatherRequest.hasError) { _phase = Phase.Idle; return; }

            NativeArray<byte> gather = _gatherRequest.GetData<byte>();
            _roundBytes = gather.Length;

            // 关键帧输出完整的原始像素数据
            byte[] full = new byte[_roundBytes];
            NativeArray<byte>.Copy(gather, 0, full, 0, _roundBytes);

            _resultTiles = null;
            _resultFullFrame = full;
            DiagReadbackBytes = _roundBytes;
            _resultReady = true;
            _phase = Phase.Idle;
        }

        /// <summary>
        /// 释放所有 GPU 缓冲区资源。
        /// </summary>
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
}
