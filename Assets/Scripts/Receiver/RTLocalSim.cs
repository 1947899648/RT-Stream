using UnityEngine;

public class RTLocalSim : MonoBehaviour
{
    public RenderTexture targetRT;
    [Range(0.1f, 2f)]
    public float interval = 0.3f;

    private Texture2D tempTex;
    private float timer;
    private Color[] colors;

    void Start()
    {
        tempTex = new Texture2D(targetRT.width, targetRT.height, TextureFormat.RGBA32, false);
        colors = new Color[]
        {
            Color.red, Color.green, Color.blue,
            Color.yellow, Color.cyan, Color.magenta,
            Color.white, Color.black
        };
    }

    void Update()
    {
        timer += Time.deltaTime;
        if (timer < interval) return;
        timer = 0f;

        var w = tempTex.width;
        var h = tempTex.height;
        var pixels = tempTex.GetPixels();

        int pattern = Random.Range(0, 3);
        switch (pattern)
        {
            case 0:
                var c = colors[Random.Range(0, colors.Length)];
                for (int i = 0; i < pixels.Length; i++) pixels[i] = c;
                break;
            case 1:
                var c1 = colors[Random.Range(0, colors.Length)];
                var c2 = colors[Random.Range(0, colors.Length)];
                for (int y = 0; y < h; y++)
                {
                    var row = y % 20 < 10 ? c1 : c2;
                    for (int x = 0; x < w; x++) pixels[y * w + x] = row;
                }
                break;
            case 2:
                for (int y = 0; y < h; y++)
                    for (int x = 0; x < w; x++)
                        pixels[y * w + x] = new Color((float)x / w, (float)y / h, 0.5f);
                break;
        }

        tempTex.SetPixels(pixels);
        tempTex.Apply();
        Graphics.Blit(tempTex, targetRT);
    }

    void OnDestroy()
    {
        if (targetRT != null) targetRT.Release();
        if (tempTex != null) Destroy(tempTex);
    }
}
