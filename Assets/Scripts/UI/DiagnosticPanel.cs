using System.Collections.Generic;
using UnityEngine;

public class DiagnosticPanel : MonoBehaviour
{
    #region 序列化字段

    [SerializeField] private int _mapWidth = 220;
    [SerializeField] private Color _gridColor = new Color(0, 1, 0, 0.25f);
    [SerializeField] private Color _dirtyColor = new Color(1, 0, 0, 0.5f);
    [SerializeField] private Color _bgColor = new Color(0, 0, 0, 0.65f);
    [SerializeField] private Color _sectionColor = new Color(1f, 0.85f, 0.4f);

    #endregion

    #region 面板常量

    private const int _tabBarH = 28;
    private const int _tabBtnW = 70;
    private const int _panelW = 420;
    private const float _panelMarginX = 12f;
    private const float _panelMarginY = 12f;
    private const int _lineH = 22;
    private const int _ctrlH = 26;
    private const float _keepDuration = 0.5f;

    #endregion

    #region 私有状态

    private string _role;
    private StreamHost _host;
    private StreamClient _client;

    private float _deltaTime;
    private int _activeTab;
    private int _activeTexSubTab;
    private List<DiagTextureInfo> _texList = new List<DiagTextureInfo>();

    private GUIStyle _styleFPS;
    private GUIStyle _styleText;

    private struct GridDim
    {
        public int TilesX, TilesY, MapH;
        public float TilePxF;
    }
    private Dictionary<byte, Dictionary<int, float>> _dirtyPerTex = new Dictionary<byte, Dictionary<int, float>>();
    private Dictionary<byte, Texture2D> _gridTexs = new Dictionary<byte, Texture2D>();
    private Dictionary<byte, GridDim> _gridDims = new Dictionary<byte, GridDim>();
    private List<int> _expiredCache = new List<int>();

    #endregion

    #region 生命周期

    void Start()
    {
        _styleFPS = new GUIStyle { fontSize = 36, fontStyle = FontStyle.Bold, alignment = TextAnchor.UpperLeft };
        _styleText = new GUIStyle { fontSize = 16, fontStyle = FontStyle.Normal, alignment = TextAnchor.UpperLeft };

        DetectRole();
    }

    void Update()
    {
        _deltaTime += (Time.unscaledDeltaTime - _deltaTime) * 0.1f;
        RefreshTexList();
        ClearExpiredTimestamps();
    }

    void OnGUI()
    {
        float ph = CalcPanelHeight();
        Rect panelRect = new Rect(_panelMarginX, _panelMarginY, _panelW, ph);

        DrawBackground(panelRect);
        GUI.BeginGroup(panelRect);

        float y = 6f;
        y = DrawHeader(y);
        y = DrawTabBar(y);
        y += 4f;
        y = DrawTabContent(y);

        GUI.EndGroup();
    }

    void OnDestroy()
    {
        if (_host != null) _host.OnDirtyTilesDetected -= OnDirtyTiles;
        if (_client != null) _client.OnDirtyTilesApplied -= OnDirtyTiles;

        foreach (Texture2D tex in _gridTexs.Values)
            if (tex != null) Destroy(tex);
        _gridTexs.Clear();
    }

    #endregion

    #region 角色检测

    void DetectRole()
    {
        _host = FindObjectOfType<StreamHost>();
        _client = FindObjectOfType<StreamClient>();

        if (_host != null)
            _role = "Sender (Host)";
        else if (_client != null)
            _role = "Receiver (Client)";
        else
            _role = "Idle";

        if (_host != null) _host.OnDirtyTilesDetected += OnDirtyTiles;
        if (_client != null) _client.OnDirtyTilesApplied += OnDirtyTiles;
    }

    void RefreshTexList()
    {
        if (_host != null)
            _texList = _host.GetDiagTextureList();
        else if (_client != null)
            _texList = _client.GetDiagTextureList();
    }

    #endregion

    #region 面板高度

    float CalcPanelHeight()
    {
        float h = 6f;
        h += 42f;
        h += 20f;
        h += 4f;
        h += _tabBarH;
        h += 6f;

        switch (_activeTab)
        {
            case 0: h += 4 * _lineH; break;
            case 1: h += _lineH * (_texList.Count > 0 ? _texList.Count + 1 : 1); break;
            case 2: h += _lineH * StreamInfoLineCount(); break;
            case 3: h += HeatmapContentHeight(); break;
        }

        h += 10f;
        return h;
    }

    int StreamInfoLineCount()
    {
        if (_host != null)
        {
            int n = 5;
            if (FrameCodec.LastEncodeComprBytes > 0) n++;
            if (!string.IsNullOrEmpty(_host.GetClientDiagnostics())) n++;
            return n;
        }
        if (_client != null && _client.IsConnected)
        {
            int n = 6;
            if (FrameCodec.LastDecodeDecompBytes > 0) n++;
            return n;
        }
        return 1;
    }

    float HeatmapContentHeight()
    {
        float h = 0;
        if (_texList.Count > 1) h += _ctrlH + 2f;
        if (_texList.Count > 0)
        {
            byte texId = GetActiveHeatmapTexId();
            if (_gridDims.TryGetValue(texId, out GridDim dim))
                h += dim.MapH + 4f;
        }
        if (h == 0) h = _lineH;
        return h;
    }

    #endregion

    #region 头部

    float DrawBackground(Rect r) { Color p = GUI.color; GUI.color = _bgColor; GUI.DrawTexture(r, Texture2D.whiteTexture); GUI.color = p; return 0; }

    float DrawHeader(float y)
    {
        float fps = 1f / _deltaTime;
        _styleFPS.normal.textColor = Color.green;
        GUI.Label(new Rect(8, y, _panelW, 42), $"{fps:0} FPS ({(float)(_deltaTime * 1000f):0.0} ms)", _styleFPS);
        y += 42;

        _styleText.normal.textColor = Color.white;
        GUI.Label(new Rect(8, y, _panelW, 20), _role, _styleText);
        return y + 20;
    }

    #endregion

    #region Tab 栏

    float DrawTabBar(float y)
    {
        string[] tabs = { "系统", "纹理", "流量", "热力图" };
        float x = 8f;
        Color prev = GUI.backgroundColor;

        for (int i = 0; i < tabs.Length; i++)
        {
            GUI.backgroundColor = i == _activeTab ? new Color(0.3f, 0.7f, 0.3f) : Color.gray;
            if (GUI.Button(new Rect(x, y, _tabBtnW, _tabBarH), tabs[i]))
                _activeTab = i;
            x += _tabBtnW + 2f;
        }

        GUI.backgroundColor = prev;
        return y + _tabBarH;
    }

    #endregion

    #region Tab 内容

    float DrawTabContent(float y)
    {
        _styleText.normal.textColor = new Color(0.8f, 0.8f, 1f);
        float x = 10f;

        switch (_activeTab)
        {
            case 0: return DrawSystemTab(x, y);
            case 1: return DrawTexturesTab(x, y);
            case 2: return DrawStreamTab(x, y);
            case 3: return DrawHeatmapTab(x, y);
        }
        return y;
    }

    #endregion

    #region Tab: 系统

    float DrawSystemTab(float x, float y)
    {
        GUI.Label(Line(x, y), string.IsNullOrEmpty(SystemInfo.processorType) ? "Processor: Unknown" : $"Processor: {SystemInfo.processorType}");
        y += _lineH;

        GUI.Label(Line(x, y), $"GPU Device: {SystemInfo.graphicsDeviceName} ({SystemInfo.graphicsDeviceType})");
        y += _lineH;

        GUI.Label(Line(x, y), FormatMemLine());
        y += _lineH;

        bool cs = SystemInfo.supportsComputeShaders;
        _styleText.normal.textColor = cs ? Color.green : Color.red;
        GUI.Label(Line(x, y), cs ? "Compute Shader: Supported" : "Compute Shader: Not Supported");
        y += _lineH;

        return y;
    }

    string FormatMemLine()
    {
        string ram = SystemInfo.systemMemorySize > 0 ? $"{SystemInfo.systemMemorySize} MB" : "--";
        string vram = SystemInfo.graphicsMemorySize > 0 ? $"{SystemInfo.graphicsMemorySize} MB" : "--";
        return $"System RAM: {ram}  VRAM: {vram}";
    }

    #endregion

    #region Tab: 纹理

    float DrawTexturesTab(float x, float y)
    {
        if (_texList.Count == 0)
        {
            GUI.Label(Line(x, y), "—");
            return y + _lineH;
        }

        GUI.Label(Line(x, y), $"Registered: {_texList.Count}");
        y += _lineH;

        foreach (DiagTextureInfo info in _texList)
        {
            int tilesX = info.Width / FrameCodec.TileSize;
            int tilesY = info.Height / FrameCodec.TileSize;
            float mb = info.Width * info.Height * 4f / 1024f / 1024f;
            GUI.Label(Line(x + 10f, y),
                $"texId={info.TexId}: {info.Width}\u00d7{info.Height}  Tile: {tilesX}\u00d7{tilesY} ({tilesX * tilesY})  {mb:F1} MB");
            y += _lineH;
        }

        return y;
    }

    #endregion

    #region Tab: 流量

    float DrawStreamTab(float x, float y)
    {
        if (_host != null) return DrawSenderBlock(x, y);
        if (_client != null) return DrawReceiverBlock(x, y);

        GUI.Label(Line(x, y), "Idle");
        return y + _lineH;
    }

    float DrawSenderBlock(float x, float y)
    {
        GUI.Label(Line(x, y), $"Listen Port: {_host.ListenPort}    Clients: {_host.ClientCount}");
        y += _lineH;

        GUI.Label(Line(x, y), $"Dirty Tiles: {_host.DiagDirtyTiles}    Readback: {_host.DiagReadbackBytes / 1024f:F1} KB");
        y += _lineH;

        GUI.Label(Line(x, y),
            $"Bandwidth: Dirty {_host.RawDirtyMBps:F3}  Enc {_host.UpEncMBps:F3}  Send {_host.UpSendMBps:F3} MB/s");
        y += _lineH;

        y = DrawLZ4EncodeLine(x, y);

        string diag = _host.GetClientDiagnostics();
        if (!string.IsNullOrEmpty(diag))
            GUI.Label(Line(x, y), diag);
        y += _lineH;

        return y;
    }

    float DrawReceiverBlock(float x, float y)
    {
        if (!_client.IsConnected)
        {
            _styleText.normal.textColor = Color.red;
            GUI.Label(Line(x, y), "Disconnected");
            return y + _lineH;
        }

        _styleText.normal.textColor = new Color(0.8f, 0.8f, 1f);
        GUI.Label(Line(x, y), $"Connection: {_client.RemoteHost}:{_client.RemotePort}");
        y += _lineH;

        GUI.Label(Line(x, y), $"Tiles: {_client.DirtyTilesReceived}  Batch: {_client.LastBatchSize}");
        y += _lineH;

        GUI.Label(Line(x, y), $"Bandwidth: Recv {_client.DownRecvMBps:F3}  Proc {_client.DownProcMBps:F3} MB/s");
        y += _lineH;

        GUI.Label(Line(x, y), $"Latency: Net {_client.NetLagMs:F0}ms  Local {_client.LocalLagMs:F0}ms  Silence {_client.SilenceMs:F0}ms");
        y += _lineH;

        y = DrawLZ4DecodeLine(x, y);

        return y;
    }

    float DrawLZ4EncodeLine(float x, float y)
    {
        if (FrameCodec.LastEncodeComprBytes <= 0) return y;
        float r = FrameCodec.LastEncodeComprBytes * 100f / FrameCodec.LastEncodeOrigBytes;
        GUI.Label(Line(x, y),
            $"LZ4: {FrameCodec.LastEncodeOrigBytes / 1024f:F1}\u2192{FrameCodec.LastEncodeComprBytes / 1024f:F1} KB ({r:F0}%)");
        return y + _lineH;
    }

    float DrawLZ4DecodeLine(float x, float y)
    {
        if (FrameCodec.LastDecodeDecompBytes <= 0) return y;
        float r = (float)FrameCodec.LastDecodeDecompBytes / FrameCodec.LastDecodeComprBytes;
        GUI.Label(Line(x, y),
            $"LZ4: {FrameCodec.LastDecodeComprBytes / 1024f:F1}\u2192{FrameCodec.LastDecodeDecompBytes / 1024f:F1} KB (\u00d7{r:F1})");
        return y + _lineH;
    }

    #endregion

    #region Tab: 热力图

    byte GetActiveHeatmapTexId()
    {
        if (_texList.Count == 0) return 255;
        if (_activeTexSubTab >= _texList.Count) _activeTexSubTab = 0;
        return _texList[_activeTexSubTab].TexId;
    }

    float DrawHeatmapTab(float x, float y)
    {
        if (_texList.Count == 0)
        {
            GUI.Label(Line(x, y), "—");
            return y + _lineH;
        }

        if (_texList.Count > 1)
        {
            y = DrawHeatmapSubTabs(x, y);
            y += 2f;
        }

        byte texId = GetActiveHeatmapTexId();
        EnsureGridTexture(texId);

        if (_gridTexs.TryGetValue(texId, out Texture2D gridTex) && gridTex != null
            && _gridDims.TryGetValue(texId, out GridDim dim))
        {
            Rect mapRect = new Rect(x, y, _mapWidth, dim.MapH);
            GUI.DrawTexture(mapRect, gridTex);

            Color prev = GUI.color;
            float now = Time.time;

            if (_dirtyPerTex.TryGetValue(texId, out Dictionary<int, float> dirtyDict))
            {
                foreach (KeyValuePair<int, float> kv in dirtyDict)
                {
                    float age = now - kv.Value;
                    if (age >= _keepDuration) continue;

                    float alpha = 1f - (age / _keepDuration);
                    int tx = kv.Key % dim.TilesX;
                    int ty = dim.TilesY - 1 - (kv.Key / dim.TilesX);

                    int tileLeft = Mathf.RoundToInt(x + tx * dim.TilePxF) + 1;
                    int tileTop = Mathf.RoundToInt(y + ty * dim.TilePxF) + 1;
                    int tileRight = Mathf.RoundToInt(x + (tx + 1) * dim.TilePxF) - 1;
                    int tileBottom = Mathf.RoundToInt(y + (ty + 1) * dim.TilePxF) - 1;
                    int tileW = Mathf.Max(1, tileRight - tileLeft);
                    int tileH = Mathf.Max(1, tileBottom - tileTop);

                    float finalAlpha = Mathf.Min(_dirtyColor.a * Mathf.Sqrt(dim.TilesX / 8f) * alpha, 1f);
                    GUI.color = new Color(_dirtyColor.r, _dirtyColor.g, _dirtyColor.b, finalAlpha);
                    GUI.DrawTexture(new Rect(tileLeft, tileTop, tileW, tileH), Texture2D.whiteTexture);
                }
            }

            GUI.color = prev;
        }

        return y + _lineH;
    }

    float DrawHeatmapSubTabs(float x, float y)
    {
        Color prev = GUI.backgroundColor;
        for (int i = 0; i < _texList.Count; i++)
        {
            GUI.backgroundColor = i == _activeTexSubTab ? new Color(0.3f, 0.7f, 0.3f) : Color.gray;
            string label = "texId=" + _texList[i].TexId;
            if (GUI.Button(new Rect(x, y, 64f, _ctrlH), label))
                _activeTexSubTab = i;
            x += 66f;
        }
        GUI.backgroundColor = prev;
        return y + _ctrlH;
    }

    void EnsureGridTexture(byte texId)
    {
        DiagTextureInfo info = default;
        bool found = false;
        foreach (DiagTextureInfo t in _texList)
        {
            if (t.TexId == texId) { info = t; found = true; break; }
        }
        if (!found) return;

        int tilesX = info.Width / FrameCodec.TileSize;
        int tilesY = info.Height / FrameCodec.TileSize;
        if (tilesX <= 0) return;

        if (_gridDims.TryGetValue(texId, out GridDim existing) && existing.TilesX == tilesX && _gridTexs.ContainsKey(texId))
            return;

        float tilePxF = (float)_mapWidth / tilesX;
        int mapH = Mathf.RoundToInt(tilePxF * tilesY);

        if (_gridTexs.TryGetValue(texId, out Texture2D oldTex) && oldTex != null)
            Destroy(oldTex);

        Texture2D tex = new Texture2D(_mapWidth, mapH, TextureFormat.RGBA32, false);
        Color[] pixels = new Color[_mapWidth * mapH];
        Color bgColor = new Color(0.1f, 0.1f, 0.1f, 0.6f);

        float gridScale = Mathf.Sqrt(8f / tilesX);
        Color gc = _gridColor;
        gc.a *= gridScale;

        for (int py = 0; py < mapH; py++)
        {
            for (int px = 0; px < _mapWidth; px++)
            {
                int tileX = Mathf.RoundToInt(px / tilePxF);
                int tileY = Mathf.RoundToInt(py / tilePxF);
                float fx = Mathf.Abs(px - tileX * tilePxF);
                float fy = Mathf.Abs(py - tileY * tilePxF);
                bool onGrid = (fx < 0.6f || fy < 0.6f) && px > 0 && py > 0;
                pixels[py * _mapWidth + px] = onGrid ? gc : bgColor;
            }
        }
        tex.SetPixels(pixels);
        tex.Apply();

        _gridTexs[texId] = tex;
        _gridDims[texId] = new GridDim { TilesX = tilesX, TilesY = tilesY, MapH = mapH, TilePxF = tilePxF };
    }

    #endregion

    #region 脏污追踪

    void OnDirtyTiles(byte texId, int[] indices)
    {
        if (!_dirtyPerTex.TryGetValue(texId, out Dictionary<int, float> dict))
        {
            dict = new Dictionary<int, float>();
            _dirtyPerTex[texId] = dict;
        }

        float now = Time.time;
        if (indices == null)
        {
            if (_gridDims.TryGetValue(texId, out GridDim dim))
            {
                int total = dim.TilesX * dim.TilesY;
                for (int i = 0; i < total; i++)
                    dict[i] = now;
            }
        }
        else
        {
            for (int i = 0; i < indices.Length; i++)
                dict[indices[i]] = now;
        }
    }

    void ClearExpiredTimestamps()
    {
        float threshold = Time.time - _keepDuration;
        foreach (Dictionary<int, float> dict in _dirtyPerTex.Values)
        {
            _expiredCache.Clear();
            foreach (KeyValuePair<int, float> kv in dict)
                if (kv.Value < threshold)
                    _expiredCache.Add(kv.Key);
            for (int i = 0; i < _expiredCache.Count; i++)
                dict.Remove(_expiredCache[i]);
        }
    }

    #endregion

    #region 工具

    static Rect Line(float x, float y)
    {
        return new Rect(x, y, 400f, _lineH);
    }

    #endregion
}
