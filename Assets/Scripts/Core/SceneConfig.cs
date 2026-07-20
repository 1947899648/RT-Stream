using UnityEngine;

public static class SceneConfig
{
    public static string HostIP { get; set; } = "127.0.0.1";
    public static int Port { get; set; } = 7777;
    public const int TileSize = 64;
    public static RenderTexture DisplayRT { get; set; }
}
