using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;

#region 类型定义

[System.Serializable]
public struct DrawEntry
{
    public string name;
    public RawImage rawImage;
    [Range(64, 8192)]
    public int textureWidth;
    [Range(64, 8192)]
    public int textureHeight;
}

#endregion

public class DrawController : MonoBehaviour
{
    #region 序列化字段

    [Header("纹理配置")]
    [SerializeField] private DrawEntry[] _entries;

    [Header("笔刷")]
    public Color BrushColor = Color.black;
    [Range(0.001f, 0.1f)]
    public float BrushSize = 0.01f;

    #endregion

    #region 面板常量

    private static readonly Color[] _palette = new Color[]
    {
        Color.red, Color.yellow, Color.green, Color.blue, Color.black
    };
    private const int _swatchSize = 30;
    private const int _swatchGap = 4;
    private const float _panelMargin = 10f;
    private const float _panelPad = 8f;
    private const float _lineH = 24f;
    private const float _ctrlH = 28f;

    #endregion

    #region 计算属性

    private float PanelW => _swatchSize * _palette.Length + _swatchGap * (_palette.Length - 1) + _panelPad * 2;
    private float PanelH => _lineH * 4 + _ctrlH + _panelPad * 2 + 18f;

    #endregion

    #region 私有状态

    private RenderTexture[] _canvasRTs;
    private RenderTexture[] _tempRTs;
    private Material _brushMat;
    private Vector2? _prevUV;
    private int _activeIndex = -1;
    private bool _pendingClearAll;
    private Dictionary<string, int> _nameToIndex = new Dictionary<string, int>();

    #endregion

    #region 公开 API

    public int EntryCount => _entries != null ? _entries.Length : 0;

    public string GetCanvasName(int index)
    {
        if (index >= 0 && index < _entries.Length)
            return _entries[index].name;
        return null;
    }

    public RenderTexture GetCanvasTexture(string name)
    {
        if (_nameToIndex.TryGetValue(name, out int index))
            return _canvasRTs[index];
        return null;
    }

    public void ClearCanvas(string name)
    {
        if (_nameToIndex.TryGetValue(name, out int index))
            ClearCanvasAt(index);
    }

    public void ClearAll()
    {
        for (int i = 0; i < _canvasRTs.Length; i++)
            ClearCanvasAt(i);
    }

    #endregion

    #region Unity 生命周期

    void Awake()
    {
        int count = _entries.Length;
        _canvasRTs = new RenderTexture[count];
        _tempRTs = new RenderTexture[count];

        for (int i = 0; i < count; i++)
        {
            int w = Mathf.Clamp(_entries[i].textureWidth, 64, 8192) / 64 * 64;
            int h = Mathf.Clamp(_entries[i].textureHeight, 64, 8192) / 64 * 64;

            _canvasRTs[i] = new RenderTexture(w, h, 0, RenderTextureFormat.ARGB32) { filterMode = FilterMode.Bilinear };
            _tempRTs[i] = new RenderTexture(w, h, 0, RenderTextureFormat.ARGB32) { filterMode = FilterMode.Bilinear };

            _entries[i].rawImage.texture = _canvasRTs[i];

            if (!string.IsNullOrEmpty(_entries[i].name))
                _nameToIndex[_entries[i].name] = i;
        }

        ClearAll();
        _brushMat = new Material(Shader.Find("Custom/Brush"));
    }

    void Update()
    {
        if (_pendingClearAll)
        {
            _pendingClearAll = false;
            ClearAll();
        }

        HandleMouseDraw();
    }

    void OnGUI()
    {
        float pw = PanelW;
        float ph = PanelH;
        float px = Screen.width - pw - _panelMargin;
        float py = _panelMargin;

        Rect panelRect = new Rect(px, py, pw, ph);
        GUI.Box(panelRect, "");
        GUI.BeginGroup(panelRect);

        float y = _panelPad;

        y = DrawPalette(y);
        y += 8f;
        y = DrawBrushSlider(y);
        y += 6f;
        y = DrawClearAllButton(y);
        y += 2f;
        DrawActiveInfo(y);

        GUI.EndGroup();
    }

    void OnDestroy()
    {
        if (_canvasRTs != null)
        {
            for (int i = 0; i < _canvasRTs.Length; i++)
            {
                if (_canvasRTs[i] != null) _canvasRTs[i].Release();
                if (_tempRTs[i] != null) _tempRTs[i].Release();
            }
        }
        if (_brushMat != null) Destroy(_brushMat);
    }

    #endregion

    #region GUI 面板

    float DrawPalette(float y)
    {
        float x = _panelPad;
        for (int i = 0; i < _palette.Length; i++)
        {
            Rect r = new Rect(x, y, _swatchSize, _swatchSize);

            Color prev = GUI.color;
            GUI.color = _palette[i];
            GUI.DrawTexture(r, Texture2D.whiteTexture);

            GUI.color = new Color(0f, 0f, 0f, 0.3f);
            GUI.DrawTexture(new Rect(r.x, r.y, r.width, 1f), Texture2D.whiteTexture);
            GUI.DrawTexture(new Rect(r.x, r.y, 1f, r.height), Texture2D.whiteTexture);
            GUI.DrawTexture(new Rect(r.x + r.width - 1f, r.y, 1f, r.height), Texture2D.whiteTexture);
            GUI.DrawTexture(new Rect(r.x, r.y + r.height - 1f, r.width, 1f), Texture2D.whiteTexture);

            if (ColorEquals(BrushColor, _palette[i]))
            {
                GUI.color = Color.white;
                float inset = 4f;
                GUI.DrawTexture(new Rect(r.x + inset, r.y + inset, r.width - inset * 2, r.height - inset * 2),
                    Texture2D.whiteTexture);
            }

            GUI.color = prev;

            if (Event.current.type == EventType.MouseDown && r.Contains(Event.current.mousePosition))
            {
                BrushColor = _palette[i];
                Event.current.Use();
            }

            x += _swatchSize + _swatchGap;
        }
        return y + _swatchSize;
    }

    float DrawBrushSlider(float y)
    {
        GUI.Label(new Rect(_panelPad, y, PanelW - _panelPad * 2, _lineH), "笔刷大小");
        y += _lineH;

        float sliderW = PanelW - _panelPad * 2 - 60f;
        float newSize = GUI.HorizontalSlider(new Rect(_panelPad, y, sliderW, _lineH), BrushSize, 0.001f, 0.1f);
        BrushSize = Mathf.Round(newSize * 1000f) / 1000f;

        GUI.Label(new Rect(_panelPad + sliderW + 6f, y, 55f, _lineH), BrushSize.ToString("F3"));
        y += _lineH;

        return y;
    }

    float DrawClearAllButton(float y)
    {
        if (GUI.Button(new Rect(_panelPad, y, PanelW - _panelPad * 2, _ctrlH), "清空全部画板"))
            _pendingClearAll = true;

        return y + _ctrlH;
    }

    float DrawActiveInfo(float y)
    {
        string info;
        if (_activeIndex >= 0 && _activeIndex < _entries.Length)
        {
            int w = _canvasRTs[_activeIndex].width;
            int h = _canvasRTs[_activeIndex].height;
            info = string.Format("绘制: {0} ({1}\u00d7{2})", _entries[_activeIndex].name, w, h);
        }
        else
        {
            info = "\u2014";
        }

        GUI.Label(new Rect(_panelPad, y, PanelW - _panelPad * 2, _lineH), info);
        return y + _lineH;
    }

    #endregion

    #region 鼠标绘制

    void HandleMouseDraw()
    {
        if (Input.GetMouseButtonDown(0))
        {
            if (IsMouseOverUI()) return;

            int hit = HitTest(Input.mousePosition);
            if (hit >= 0)
            {
                _activeIndex = hit;
                Vector2 uv = ScreenToUV(_entries[hit].rawImage, Input.mousePosition);
                _prevUV = uv;
                DrawAt(hit, uv);
            }
        }
        else if (Input.GetMouseButton(0) && _prevUV.HasValue && _activeIndex >= 0)
        {
            Vector2 uv = ScreenToUV(_entries[_activeIndex].rawImage, Input.mousePosition);
            float dist = Vector2.Distance(_prevUV.Value, uv);
            int steps = Mathf.Max(1, Mathf.CeilToInt(dist / (BrushSize * 0.5f)));
            for (int i = 1; i <= steps; i++)
                DrawAt(_activeIndex, Vector2.Lerp(_prevUV.Value, uv, (float)i / steps));
            _prevUV = uv;
        }
        else if (Input.GetMouseButtonUp(0))
        {
            _prevUV = null;
            _activeIndex = -1;
        }
    }

    int HitTest(Vector2 screenPos)
    {
        for (int i = 0; i < _entries.Length; i++)
        {
            RectTransform rt = _entries[i].rawImage.GetComponent<RectTransform>();
            if (rt != null && RectTransformUtility.RectangleContainsScreenPoint(rt, screenPos))
                return i;
        }
        return -1;
    }

    Vector2 ScreenToUV(RawImage rawImage, Vector2 screenPos)
    {
        RectTransform rt = rawImage.GetComponent<RectTransform>();
        RectTransformUtility.ScreenPointToLocalPointInRectangle(rt, screenPos, null, out Vector2 lp);
        Rect rect = rt.rect;
        return new Vector2(
            Mathf.InverseLerp(rect.xMin, rect.xMax, lp.x),
            Mathf.InverseLerp(rect.yMin, rect.yMax, lp.y)
        );
    }

    #endregion

    #region 核心操作

    void DrawAt(int index, Vector2 uv)
    {
        _brushMat.SetVector("_BrushPos", new Vector4(uv.x, uv.y, BrushSize, 0));
        _brushMat.SetColor("_BrushColor", BrushColor);
        Graphics.Blit(_canvasRTs[index], _tempRTs[index]);
        Graphics.Blit(_tempRTs[index], _canvasRTs[index], _brushMat);
    }

    void ClearCanvasAt(int index)
    {
        RenderTexture.active = _canvasRTs[index];
        GL.Clear(true, true, Color.white);
        RenderTexture.active = null;
    }

    #endregion

    #region 工具方法

    bool IsMouseOverUI()
    {
        float px = Screen.width - PanelW - _panelMargin;
        Vector2 m = new Vector2(Input.mousePosition.x, Screen.height - Input.mousePosition.y);
        return m.x >= px && m.x <= px + PanelW && m.y >= _panelMargin && m.y <= _panelMargin + PanelH;
    }

    static bool ColorEquals(Color a, Color b)
    {
        return Mathf.Abs(a.r - b.r) < 0.01f
            && Mathf.Abs(a.g - b.g) < 0.01f
            && Mathf.Abs(a.b - b.b) < 0.01f;
    }

    #endregion
}
