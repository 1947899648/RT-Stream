using UnityEngine;

public class RTLocalSim : MonoBehaviour
{
    [Range(0.1f, 2f)]
    public float interval = 0.3f;

    private Texture2D _tempTex;
    private float _timer;
    private Color[] _colors;
    private Color[] _pixels;

    void Start()
    {
        _tempTex = new Texture2D(SceneConfig.DisplayRT.width, SceneConfig.DisplayRT.height, TextureFormat.RGBA32, false);
        _colors = new Color[]
        {
            Color.red, Color.green, Color.blue,
            Color.yellow, Color.cyan, Color.magenta,
            Color.white, Color.black
        };
        _pixels = new Color[_tempTex.width * _tempTex.height];
    }

    void Update()
    {
        _timer += Time.deltaTime;
        if (_timer < interval) return;
        _timer = 0f;

        int w = _tempTex.width;
        int h = _tempTex.height;

        int pattern = Random.Range(0, 3);
        switch (pattern)
        {
            case 0:
                Color c = _colors[Random.Range(0, _colors.Length)];
                for (int i = 0; i < _pixels.Length; i++) _pixels[i] = c;
                break;
            case 1:
                Color c1 = _colors[Random.Range(0, _colors.Length)];
                Color c2 = _colors[Random.Range(0, _colors.Length)];
                for (int y = 0; y < h; y++)
                {
                    Color row = y % 20 < 10 ? c1 : c2;
                    for (int x = 0; x < w; x++) _pixels[y * w + x] = row;
                }
                break;
            case 2:
                for (int y = 0; y < h; y++)
                    for (int x = 0; x < w; x++)
                        _pixels[y * w + x] = new Color((float)x / w, (float)y / h, 0.5f);
                break;
        }

        _tempTex.SetPixels(_pixels);
        _tempTex.Apply();
        Graphics.Blit(_tempTex, SceneConfig.DisplayRT);
    }

    void OnDestroy()
    {
        if (_tempTex != null) Destroy(_tempTex);
    }
}
