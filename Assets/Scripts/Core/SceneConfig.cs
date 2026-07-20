using UnityEngine;

public static class SceneConfig
{
    public static string HostIP = "127.0.0.1";
    public static int Port = 7777;
    public static int TextureWidth = 512;
    public static int TextureHeight = 512;
    // 固定 64px 而非动态计算，原因：
    // 1. 高分辨率下动态 tile 过大，单个脏块包含大量未变化像素，浪费带宽
    // 2. GPU 总工作量不变（总像素数相同），仅分区大小变化
    // 3. 64px 保证 8 整除（ComputeShader THREADS=8），无边界对齐问题
    public const int TileSize = 64;
    public static RenderTexture DisplayRT;
}
