using System;

public static class FastHash
{
    private const ulong FnvOffsetBasis = 14695981039346656037;
    private const ulong FnvPrime = 1099511628211;

    public static ulong Compute(byte[] data, int offset, int length)
    {
        ulong hash = FnvOffsetBasis;
        int end = offset + length;
        int i = offset;

        while (i + 8 <= end)
        {
            ulong v = BitConverter.ToUInt64(data, i);
            hash ^= v;
            hash *= FnvPrime;
            i += 8;
        }

        ulong last = 0;
        for (int j = 0; i < end; i++, j++)
            last |= (ulong)data[i] << (j * 8);
        hash ^= last;
        hash *= FnvPrime;

        return hash;
    }
}
