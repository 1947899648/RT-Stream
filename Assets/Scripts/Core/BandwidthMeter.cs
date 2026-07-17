using System.Threading;
using UnityEngine;

public class BandwidthMeter
{
    private long _bytes;
    private float _lastSampleTime;
    private float _mbps;

    public float MBps => _mbps;

    public void Reset()
    {
        _bytes = 0;
        _lastSampleTime = 0f;
        _mbps = 0f;
    }

    public void Add(long count)
    {
        Interlocked.Add(ref _bytes, count);
    }

    public void Sample()
    {
        float now = Time.time;
        float elapsed = now - _lastSampleTime;
        if (_lastSampleTime == 0f)
        {
            _lastSampleTime = now;
            return;
        }
        if (elapsed >= 1f)
        {
            long bytes = Interlocked.Exchange(ref _bytes, 0);
            _mbps = bytes / elapsed / 1024f / 1024f;
            _lastSampleTime = now;
        }
    }
}
