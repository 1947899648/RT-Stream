using System.Diagnostics;
using System.Threading;

namespace WPZ0325.RTStream
{
    public class BandwidthMeter
    {
        private long _bytes;
        private long _lastSampleTicks;
        private float _mbps;
    
        public float MBps => _mbps;
    
        public void Reset()
        {
            _bytes = 0;
            _lastSampleTicks = 0;
            _mbps = 0f;
        }
    
        public void Add(long count)
        {
            Interlocked.Add(ref _bytes, count);
        }
    
        public void Sample()
        {
            long now = Stopwatch.GetTimestamp();
            if (_lastSampleTicks == 0)
            {
                _lastSampleTicks = now;
                return;
            }
            float elapsed = (float)(now - _lastSampleTicks) / Stopwatch.Frequency;
            if (elapsed >= 1f)
            {
                long bytes = Interlocked.Exchange(ref _bytes, 0);
                _mbps = bytes / elapsed / 1024f / 1024f;
                _lastSampleTicks = now;
            }
        }
    }
}
