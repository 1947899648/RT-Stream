using UnityEngine;

public class RTLocalSim : MonoBehaviour
{
    public RenderTexture targetRT;
    [Range(0.1f, 2f)]
    public float interval = 0.3f;

    private Texture2D _tempTex;
    private float _timer;
    private Color[] _colors;

    void Start()
    {
        _tempTex = new Texture2D(targetRT.width, targetRT.height, TextureFormat.RGBA32, false);
        _colors = new Color[]
        {
            Color.red, Color.green, Color.blue,
            Color.yellow, Color.cyan, Color.magenta,
            Color.white, Color.black
        };
    }

    void Update()
    {
        _timer += Time.deltaTime;
        if (_timer < interval) return;
        _timer = 0f;

        int w = _tempTex.width;
        int h = _tempTex.height;
        Color[] pixels = _tempTex.GetPixels();

        int pattern = Random.Range(0, 3);
        switch (pattern)
        {
            case 0:
                Color c = _colors[Random.Range(0, _colors.Length)];
                for (int i = 0; i < pixels.Length; i++) pixels[i] = c;
                break;
            case 1:
                Color c1 = _colors[Random.Range(0, _colors.Length)];
                Color c2 = _colors[Random.Range(0, _colors.Length)];
                for (int y = 0; y < h; y++)
                {
                    Color row = y % 20 < 10 ? c1 : c2;
                    for (int x = 0; x < w; x++) pixels[y * w + x] = row;
                }
                break;
            case 2:
                for (int y = 0; y < h; y++)
                    for (int x = 0; x < w; x++)
                        pixels[y * w + x] = new Color((float)x / w, (float)y / h, 0.5f);
                break;
        }

        _tempTex.SetPixels(pixels);
        _tempTex.Apply();
        Graphics.Blit(_tempTex, targetRT);
    }

    void OnDestroy()
    {
        if (targetRT != null) targetRT.Release();
        if (_tempTex != null) Destroy(_tempTex);
    }
}
