using System.Collections.Generic;
using UnityEngine;
using UnityEngine.SceneManagement;

public class DiagnosticPanel : MonoBehaviour
{
    [SerializeField] private int _mapWidth = 220;
    [SerializeField] private int _panelWidth = 620;
    [SerializeField] private Color _gridColor = new Color(0, 1, 0, 0.25f);
    [SerializeField] private Color _dirtyColor = new Color(1, 0, 0, 0.5f);
    [SerializeField] private Color _bgColor = new Color(0, 0, 0, 0.65f);
    [SerializeField] private Color _sectionColor = new Color(1f, 0.85f, 0.4f);

    private static DiagnosticPanel _instance;
    private float _deltaTime;

    private string _role;
    private DrawableSurface _canvas;
    private StreamHost _host;
    private StreamClient _client;

    private Dictionary<int, float> _dirtyTimestamps = new Dictionary<int, float>();
    private const float _keepDuration = 0.5f;
    private Texture2D _gridTex;
    private Texture2D _whiteTex;
    private int _tilesX, _tilesY;
    private float _tilePixelF;

    private GUIStyle _styleFPS;
    private GUIStyle _styleRole;
    private GUIStyle _styleSection;
    private GUIStyle _styleText;

    private const int _lineH = 26;

    void Awake()
    {
        if (_instance != null) { Destroy(gameObject); return; }
        _instance = this;
        DontDestroyOnLoad(gameObject);

        Telepathy.Log.Info = Debug.Log;
        Telepathy.Log.Warning = Debug.LogWarning;
        Telepathy.Log.Error = Debug.LogError;

        _styleFPS = new GUIStyle { fontSize = 36, fontStyle = FontStyle.Bold, alignment = TextAnchor.UpperLeft };
        _styleRole = new GUIStyle { fontSize = 18, fontStyle = FontStyle.Normal, alignment = TextAnchor.UpperLeft };
        _styleSection = new GUIStyle { fontSize = 18, fontStyle = FontStyle.Bold, alignment = TextAnchor.UpperLeft };
        _styleText = new GUIStyle { fontSize = 18, fontStyle = FontStyle.Normal, alignment = TextAnchor.UpperLeft };

        _whiteTex = new Texture2D(1, 1, TextureFormat.RGBA32, false);
        _whiteTex.SetPixel(0, 0, Color.white);
        _whiteTex.Apply();

        SceneManager.sceneLoaded += (scene, mode) =>
        {
            DetectRole();
            _dirtyTimestamps.Clear();
        };

        QualitySettings.vSyncCount = 0;
        Application.targetFrameRate = 9999;
    }

    void Start()
    {
        DetectRole();
        BuildGridTexture();
    }

    void Update()
    {
        _deltaTime += (Time.unscaledDeltaTime - _deltaTime) * 0.1f;
        ClearExpiredTimestamps();
    }

    void OnGUI()
    {
        int x = 12;
        int y = 12;
        int bgHeight = CalcTotalHeight();

        DrawBackground(x - 6, y - 6, _panelWidth, bgHeight);

        y = DrawHeader(x, y);

        y = DrawDivider(x, y);
        y = DrawSectionTitle(x, y, "Hardware");
        y = DrawHardwareInfo(x, y);

        y = DrawDivider(x, y);
        y = DrawSectionTitle(x, y, "RT Texture");
        y = DrawRTInfo(x, y);

        y = DrawDivider(x, y);
        y = DrawSectionTitle(x, y, "Stream");
        y = DrawStreamInfo(x, y);

        if (_host != null || _client != null)
        {
            y = DrawDivider(x, y);
            y = DrawSectionTitle(x, y, "Tile Map");
            y = DrawTileMap(x, y);
        }
    }

    void OnDestroy()
    {
        if (_host != null) _host.OnDirtyTilesDetected -= OnDirtyTiles;
        if (_gridTex != null) Destroy(_gridTex);
        if (_whiteTex != null) Destroy(_whiteTex);
    }

    int CalcTotalHeight()
    {
        int h = 12;

        h += 42;
        h += 28;
        h += 8;

        h += 8 + 26;
        h += _lineH * 4 + 4;
        h += 6;

        h += 8 + 26;

        if (_canvas != null)
            h += _lineH * 2 + 4;
        else if (_client != null && SceneConfig.DisplayRT != null)
            h += _lineH + 4;
        h += 6;

        h += 8 + 26;

        if (_host != null)
        {
            h += _lineH * 5;
            if (FrameCodec.LastEncodeComprBytes > 0) h += _lineH;
            if (!string.IsNullOrEmpty(_host.GetClientDiagnostics())) h += _lineH;
            h += 4;
        }
        else if (_client != null)
        {
            if (!_client.IsConnected)
                h += _lineH;
            else
            {
                h += _lineH * 6;
                if (FrameCodec.LastDecodeDecompBytes > 0) h += _lineH;
            }
            h += 4;
        }
        else
        {
            h += _lineH + 4;
        }

        h += 6;

        if (_host != null || _client != null)
        {
            h += 8 + 26;
            h += _mapWidth + 12;
        }

        h += 8;

        return h;
    }

    void DrawBackground(int x, int y, int w, int h)
    {
        Color prev = GUI.color;
        GUI.color = _bgColor;
        GUI.DrawTexture(new Rect(x, y, w, h), _whiteTex);
        GUI.color = prev;
    }

    int DrawHeader(int x, int y)
    {
        float fps = 1f / _deltaTime;
        float ms = _deltaTime * 1000f;

        _styleFPS.normal.textColor = Color.green;
        GUI.Label(new Rect(x, y, _panelWidth, 42), $"{fps:0} FPS ({ms:0.0} ms)", _styleFPS);
        y += 42;

        _styleRole.normal.textColor = Color.white;
        GUI.Label(new Rect(x, y, _panelWidth, 22), _role, _styleRole);
        y += 28;

        return y;
    }

    int DrawDivider(int x, int y)
    {
        Color prev = GUI.color;
        GUI.color = new Color(1, 1, 1, 0.15f);
        GUI.DrawTexture(new Rect(x, y, _panelWidth - 24, 1), _whiteTex);
        GUI.color = prev;
        return y + 8;
    }

    int DrawSectionTitle(int x, int y, string title)
    {
        _styleSection.normal.textColor = _sectionColor;
        GUI.Label(new Rect(x, y, _panelWidth, 22), title, _styleSection);
        return y + 26;
    }

    int DrawRTInfo(int x, int y)
    {
        _styleText.normal.textColor = new Color(0.8f, 0.8f, 1f);

        if (_canvas != null)
        {
            int size = SceneConfig.TextureSize;
            int rtMem = size * size * 4;
            int t = size / SceneConfig.TileSize;
            string fmt = _canvas.CanvasTexture != null ? _canvas.CanvasTexture.format.ToString() : "RGBA32";

            GUI.Label(new Rect(x, y, _panelWidth, 22),
                $"Render Texture: {size}×{size}  {fmt}  {rtMem / 1024f / 1024f:F1} MB", _styleText);
            y += _lineH;

            GUI.Label(new Rect(x, y, _panelWidth, 22),
                $"Tile Grid: {t}×{t} ({t * t} tiles)  {SceneConfig.TileSize} px", _styleText);
            y += _lineH + 4;
        }
        else if (_client != null && SceneConfig.DisplayRT != null)
        {
            int rtMem = SceneConfig.DisplayRT.width * SceneConfig.DisplayRT.height * 4;
            GUI.Label(new Rect(x, y, _panelWidth, 22),
                $"Render Texture: {SceneConfig.DisplayRT.width}×{SceneConfig.DisplayRT.height}  {SceneConfig.DisplayRT.format}  {rtMem / 1024f / 1024f:F1} MB", _styleText);
            y += _lineH + 4;
        }

        return y + 6;
    }

    int DrawStreamInfo(int x, int y)
    {
        if (_host != null)
        {
            y = DrawSenderBlock(x, y);
        }
        else if (_client != null)
        {
            y = DrawReceiverBlock(x, y);
        }
        else
        {
            _styleText.normal.textColor = Color.white;
            GUI.Label(new Rect(x, y, _panelWidth, 22), "Idle", _styleText);
            y += _lineH + 4;
        }

        return y + 6;
    }

    int DrawSenderBlock(int x, int y)
    {
        float rbkB = _host.DiagReadbackBytes / 1024f;

        _styleText.normal.textColor = new Color(0.8f, 0.8f, 1f);

        GUI.Label(new Rect(x, y, _panelWidth, 22),
            $"Connected Clients: {_host.ClientCount}    Listen Port: {SceneConfig.Port}", _styleText);
        y += _lineH;

        GUI.Label(new Rect(x, y, _panelWidth, 22),
            $"Dirty Tiles Detected: {_host.DiagDirtyTiles}    GPU Readback: {rbkB:F1} KB", _styleText);
        y += _lineH;

        GUI.Label(new Rect(x, y, _panelWidth, 22),
            $"Raw Dirty Bandwidth: {_host.RawDirtyMBps:F4} MB/s", _styleText);
        y += _lineH;

        GUI.Label(new Rect(x, y, _panelWidth, 22),
            $"Encode Bandwidth: {_host.UpEncMBps:F4} MB/s", _styleText);
        y += _lineH;

        GUI.Label(new Rect(x, y, _panelWidth, 22),
            $"Send Bandwidth: {_host.UpSendMBps:F4} MB/s", _styleText);
        y += _lineH;

        y = DrawLZ4EncodeLine(x, y);

        string diag = _host.GetClientDiagnostics();
        if (!string.IsNullOrEmpty(diag))
        {
            _styleText.normal.textColor = new Color(1f, 0.9f, 0.5f);
            GUI.Label(new Rect(x, y, _panelWidth, 22), diag, _styleText);
            y += _lineH;
        }

        return y + 4;
    }

    int DrawReceiverBlock(int x, int y)
    {
        if (!_client.IsConnected)
        {
            _styleText.normal.textColor = Color.red;
            GUI.Label(new Rect(x, y, _panelWidth, 22), "Disconnected", _styleText);
            return y + _lineH + 4;
        }

        _styleText.normal.textColor = new Color(0.8f, 0.8f, 1f);
        GUI.Label(new Rect(x, y, _panelWidth, 22),
            $"Connection: {SceneConfig.HostIP}:{SceneConfig.Port}", _styleText);
        y += _lineH;

        GUI.Label(new Rect(x, y, _panelWidth, 22),
            $"Frame Batch Size: {_client.LastBatchSize}    Skipped Frames: {_client.SkippedFrames}", _styleText);
        y += _lineH;

        GUI.Label(new Rect(x, y, _panelWidth, 22),
            $"Dirty Tiles Received: {_client.DirtyTilesReceived}", _styleText);
        y += _lineH;

        GUI.Label(new Rect(x, y, _panelWidth, 22),
            $"Receive Bandwidth: {_client.DownRecvMBps:F4} MB/s", _styleText);
        y += _lineH;

        GUI.Label(new Rect(x, y, _panelWidth, 22),
            $"Process Bandwidth: {_client.DownProcMBps:F4} MB/s", _styleText);
        y += _lineH;

        GUI.Label(new Rect(x, y, _panelWidth, 22),
            $"Network Latency: {_client.NetLagMs:F0} ms    Local Latency: {_client.LocalLagMs:F0} ms    Silence: {_client.SilenceMs:F0} ms", _styleText);
        y += _lineH;

        y = DrawLZ4DecodeLine(x, y);

        return y + 4;
    }

    int DrawLZ4EncodeLine(int x, int y)
    {
        if (FrameCodec.LastEncodeComprBytes <= 0) return y;

        float ratio = FrameCodec.LastEncodeComprBytes * 100f / FrameCodec.LastEncodeOrigBytes;
        _styleText.normal.textColor = new Color(0.8f, 0.8f, 1f);
        GUI.Label(new Rect(x, y, _panelWidth, 22),
            $"LZ4 Compress: {FrameCodec.LastEncodeOrigBytes / 1024f:F1} KB → {FrameCodec.LastEncodeComprBytes / 1024f:F1} KB    Ratio: {ratio:F0}%", _styleText);
        return y + _lineH;
    }

    int DrawLZ4DecodeLine(int x, int y)
    {
        if (FrameCodec.LastDecodeDecompBytes <= 0) return y;

        float ratio = (float)FrameCodec.LastDecodeDecompBytes / FrameCodec.LastDecodeComprBytes;
        _styleText.normal.textColor = new Color(0.8f, 0.8f, 1f);
        GUI.Label(new Rect(x, y, _panelWidth, 22),
            $"LZ4 Decompress: {FrameCodec.LastDecodeComprBytes / 1024f:F1} KB → {FrameCodec.LastDecodeDecompBytes / 1024f:F1} KB    Ratio: ×{ratio:F1}", _styleText);
        return y + _lineH;
    }

    int DrawHardwareInfo(int x, int y)
    {
        _styleText.normal.textColor = new Color(0.8f, 0.8f, 1f);

        string cpuLine = string.IsNullOrEmpty(SystemInfo.processorType)
            ? "Processor: Unknown"
            : $"Processor: {SystemInfo.processorType}";
        GUI.Label(new Rect(x, y, _panelWidth, 22), cpuLine, _styleText);
        y += _lineH;

        GUI.Label(new Rect(x, y, _panelWidth, 22),
            $"GPU Device: {SystemInfo.graphicsDeviceName} ({SystemInfo.graphicsDeviceType})", _styleText);
        y += _lineH;

        string memLine;
        if (SystemInfo.systemMemorySize > 0 && SystemInfo.graphicsMemorySize > 0)
            memLine = $"System RAM: {SystemInfo.systemMemorySize} MB    VRAM: {SystemInfo.graphicsMemorySize} MB";
        else if (SystemInfo.systemMemorySize > 0)
            memLine = $"System RAM: {SystemInfo.systemMemorySize} MB    VRAM: -- MB";
        else if (SystemInfo.graphicsMemorySize > 0)
            memLine = $"System RAM: -- MB    VRAM: {SystemInfo.graphicsMemorySize} MB";
        else
            memLine = "System RAM: -- MB    VRAM: -- MB";

        GUI.Label(new Rect(x, y, _panelWidth, 22), memLine, _styleText);
        y += _lineH;

        bool csSupported = SystemInfo.supportsComputeShaders;
        _styleText.normal.textColor = csSupported ? Color.green : Color.red;
        GUI.Label(new Rect(x, y, _panelWidth, 22),
            csSupported ? "Compute Shader Support: Yes" : "Compute Shader Support: No", _styleText);
        y += _lineH + 4;

        return y + 6;
    }

    int DrawTileMap(int x, int y)
    {
        EnsureGridTexture();
        if (_gridTex == null) return y;

        int mapX = x;
        int mapY = y;

        Rect mapRect = new Rect(mapX, mapY, _mapWidth, _mapWidth);
        GUI.DrawTexture(mapRect, _gridTex);

        Color prev = GUI.color;
        Color dirtyOrig = _dirtyColor;
        float dirtyScale = Mathf.Sqrt(_tilesX / 8f);
        float now = Time.time;

        foreach (KeyValuePair<int, float> kv in _dirtyTimestamps)
        {
            float age = now - kv.Value;
            if (age >= _keepDuration) continue;

            float alpha = 1f - (age / _keepDuration);
            int tx = kv.Key % _tilesX;
            int ty = _tilesY - 1 - (kv.Key / _tilesX);

            int tileLeft = Mathf.RoundToInt(mapX + tx * _tilePixelF) + 1;
            int tileTop = Mathf.RoundToInt(mapY + ty * _tilePixelF) + 1;
            int tileRight = Mathf.RoundToInt(mapX + (tx + 1) * _tilePixelF) - 1;
            int tileBottom = Mathf.RoundToInt(mapY + (ty + 1) * _tilePixelF) - 1;
            int tileW = tileRight - tileLeft;
            int tileH = tileBottom - tileTop;
            if (tileW < 1) tileW = 1;
            if (tileH < 1) tileH = 1;

            Rect tr = new Rect(tileLeft, tileTop, tileW, tileH);

            float finalAlpha = Mathf.Min(dirtyOrig.a * dirtyScale * alpha, 1f);
            GUI.color = new Color(dirtyOrig.r, dirtyOrig.g, dirtyOrig.b, finalAlpha);
            GUI.DrawTexture(tr, _whiteTex);
        }

        GUI.color = prev;
        return y + _mapWidth + 12 + 8;
    }

    void DetectRole()
    {
        if (_host != null) _host.OnDirtyTilesDetected -= OnDirtyTiles;

        _canvas = FindObjectOfType<DrawableSurface>();
        _host = FindObjectOfType<StreamHost>();
        _client = FindObjectOfType<StreamClient>();

        if (_host != null || _canvas != null)
            _role = "Sender (Host)";
        else if (_client != null)
            _role = "Receiver (Client)";
        else
            _role = "Idle";

        if (_host != null) _host.OnDirtyTilesDetected += OnDirtyTiles;
    }

    void EnsureGridTexture()
    {
        int tileSize = SceneConfig.TileSize;
        int textureSize = SceneConfig.TextureSize;
        if (_host == null && _client != null && SceneConfig.DisplayRT != null)
            textureSize = SceneConfig.DisplayRT.width;

        if (textureSize <= 0) return;
        int expectedTilesX = textureSize / tileSize;
        if (expectedTilesX <= 0) return;
        if (_tilesX == expectedTilesX && _gridTex != null) return;

        BuildGridTexture();
    }

    void BuildGridTexture()
    {
        if (_gridTex != null) { Destroy(_gridTex); _gridTex = null; }
        _dirtyTimestamps.Clear();

        int tileSize = SceneConfig.TileSize;
        int textureSize = SceneConfig.TextureSize;
        if (_host == null && _client != null && SceneConfig.DisplayRT != null)
            textureSize = SceneConfig.DisplayRT.width;

        if (textureSize <= 0) return;
        _tilesX = textureSize / tileSize;
        if (_tilesX <= 0) return;
        _tilesY = _tilesX;
        _tilePixelF = (float)_mapWidth / _tilesX;

        _gridTex = new Texture2D(_mapWidth, _mapWidth, TextureFormat.RGBA32, false);
        Color[] pixels = new Color[_mapWidth * _mapWidth];
        Color bgColor = new Color(0.1f, 0.1f, 0.1f, 0.6f);

        float gridScale = Mathf.Sqrt(8f / _tilesX);
        Color gridCol = _gridColor;
        gridCol.a *= gridScale;

        for (int y = 0; y < _mapWidth; y++)
        {
            for (int x = 0; x < _mapWidth; x++)
            {
                int px = Mathf.RoundToInt(x / _tilePixelF);
                int py = Mathf.RoundToInt(y / _tilePixelF);
                float fx = Mathf.Abs(x - px * _tilePixelF);
                float fy = Mathf.Abs(y - py * _tilePixelF);
                bool onGrid = (fx < 0.6f || fy < 0.6f) && x > 0 && y > 0;
                pixels[y * _mapWidth + x] = onGrid ? gridCol : bgColor;
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

    void ClearExpiredTimestamps()
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
}
