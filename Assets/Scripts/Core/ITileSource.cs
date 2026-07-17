using System;
using System.Collections.Generic;

public interface ITileSource : IDisposable
{
    void Update(bool wantKeyFrame);
    bool TryGetResult(out List<DirtyTile> dirtyTiles, out byte[] fullFrame);
    int TilesX { get; }
    int TilesY { get; }
    int DiagReadbackBytes { get; }
}
