using System.Collections.Generic;
using UnityEngine;

public class TileVisualizer : MonoBehaviour
{
    [SerializeField] private int _mapSize = 200;
    [SerializeField] private Color _gridColor = new Color(0, 1, 0, 0.25f);
    [SerializeField] private Color _dirtyColor = new Color(1, 0, 0, 0.5f);

    private Dictionary<int, float> _dirtyTimestamps = new Dictionary<int, float>();
    private const float _keepDuration = 0.5f;
    private StreamHost _host;
    private StreamClient _client;
    private Texture2D _gridTex;
    private int _tilesX, _tilesY, _tilePixel;

    void Start()
    {
        _host = FindObjectOfType<StreamHost>();
        _client = FindObjectOfType<StreamClient>();

        if (_host != null)
            _host.OnDirtyTilesDetected += OnDirtyTiles;
        if (_client != null)
            _client.OnDirtyTilesApplied += OnDirtyTiles;

        int tileSize = SceneConfig.TileSize;
        _tilesX = SceneConfig.TextureSize / tileSize;
        _tilesY = _tilesX;
        _tilePixel = _mapSize / _tilesX;
        BuildGridTexture();
    }

    void BuildGridTexture()
    {
        _gridTex = new Texture2D(_mapSize, _mapSize, TextureFormat.RGBA32, false);
        Color[] pixels = new Color[_mapSize * _mapSize];
        Color bgColor = new Color(0.1f, 0.1f, 0.1f, 0.6f);

        for (int y = 0; y < _mapSize; y++)
        {
            for (int x = 0; x < _mapSize; x++)
            {
                bool onGrid = (x % _tilePixel == 0 && x > 0) || (y % _tilePixel == 0 && y > 0);
                pixels[y * _mapSize + x] = onGrid ? _gridColor : bgColor;
            }
        }
        _gridTex.SetPixels(pixels);
        _gridTex.Apply();
    }

    void OnDirtyTiles(int[] indices)
    {
        float now = Time.time;
        if (indices == null)
        {
            int total = _tilesX * _tilesY;
            for (int i = 0; i < total; i++)
                _dirtyTimestamps[i] = now;
        }
        else
        {
            for (int i = 0; i < indices.Length; i++)
                _dirtyTimestamps[indices[i]] = now;
        }
    }

    void Update()
    {
        if (_dirtyTimestamps.Count == 0) return;

        float threshold = Time.time - _keepDuration;
        List<int> expired = new List<int>();
        foreach (KeyValuePair<int, float> kv in _dirtyTimestamps)
            if (kv.Value < threshold)
                expired.Add(kv.Key);
        for (int i = 0; i < expired.Count; i++)
            _dirtyTimestamps.Remove(expired[i]);
    }

    void OnGUI()
    {
        int mapX = Screen.width - _mapSize - 20;
        int mapY = 20;
        Rect mapRect = new Rect(mapX, mapY, _mapSize, _mapSize);

        GUI.DrawTexture(mapRect, _gridTex);

        if (_dirtyTimestamps.Count == 0) return;

        float now = Time.time;
        Color dirtyOrig = _dirtyColor;

        foreach (KeyValuePair<int, float> kv in _dirtyTimestamps)
        {
            float age = now - kv.Value;
            if (age >= _keepDuration) continue;

            float alpha = 1f - (age / _keepDuration);
            int tx = kv.Key % _tilesX;
            int ty = _tilesY - 1 - (kv.Key / _tilesX);

            Rect tr = new Rect(
                mapX + tx * _tilePixel + 1,
                mapY + ty * _tilePixel + 1,
                _tilePixel - 2,
                _tilePixel - 2
            );

            GUI.color = new Color(dirtyOrig.r, dirtyOrig.g, dirtyOrig.b, dirtyOrig.a * alpha);
            GUI.DrawTexture(tr, Texture2D.whiteTexture);
        }
        GUI.color = Color.white;
    }

    void OnDestroy()
    {
        if (_host != null) _host.OnDirtyTilesDetected -= OnDirtyTiles;
        if (_client != null) _client.OnDirtyTilesApplied -= OnDirtyTiles;
        if (_gridTex != null) Destroy(_gridTex);
    }
}
