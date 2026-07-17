using UnityEngine;

public static class SceneConfig
{
    public static string HostIP = "127.0.0.1";
    public static int Port = 7777;
    public static int TextureSize = 512;
    public const int TargetTilesPerSide = 8;
    public static int TileSize => Mathf.Max(8, TextureSize / TargetTilesPerSide);
    public static RenderTexture DisplayRT;
}
