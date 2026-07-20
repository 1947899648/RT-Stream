using System.Diagnostics;
using System.Threading;

namespace WPZ0325.RTStream
{
    /// <summary>
    /// 带宽计量器。使用累加 + 定时采样的方式估算实时吞吐量（MB/s）。
    /// 线程安全，适合在发送/接收线程中累加字节数后由主线程取样。
    /// </summary>
    public class BandwidthMeter
    {
        #region 公开属性

        /// <summary>
        /// 最近一次采样得出的带宽值（MB/s）。
        /// </summary>
        public float MBps => _mbps;

        #endregion

        #region 公开方法

        /// <summary>
        /// 重置所有计数器与采样状态。
        /// </summary>
        public void Reset()
        {
            _bytes = 0;
            _lastSampleTicks = 0;
            _mbps = 0f;
        }

        /// <summary>
        /// 累加字节数（线程安全）。
        /// </summary>
        /// <param name="count">本次传输的字节数</param>
        public void Add(long count)
        {
            Interlocked.Add(ref _bytes, count);
        }

        /// <summary>
        /// 取样并计算带宽。至少间隔 1 秒才会生成新的带宽值。
        /// </summary>
        public void Sample()
        {
            long now = Stopwatch.GetTimestamp();
            // 首次调用仅记录时间戳，不计算
            if (_lastSampleTicks == 0)
            {
                _lastSampleTicks = now;
                return;
            }
            float elapsed = (float)(now - _lastSampleTicks) / Stopwatch.Frequency;
            // 间隔达到 1 秒以上时更新带宽值并重置计数器
            if (elapsed >= 1f)
            {
                long bytes = Interlocked.Exchange(ref _bytes, 0);
                _mbps = bytes / elapsed / 1024f / 1024f;
                _lastSampleTicks = now;
            }
        }

        #endregion

        #region 私有字段

        // 通过原子操作累加的字节总数
        private long _bytes;
        // 上次采样的时间戳
        private long _lastSampleTicks;
        // 最近一次计算出的带宽（MB/s）
        private float _mbps;

        #endregion
    }
}
