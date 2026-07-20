using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;

public class ReceiverSetup : MonoBehaviour
{
    #region 序列化字段

    [Header("核心模块")]
    [SerializeField] private StreamClient _streamClient;

    [Header("显示目标")]
    [SerializeField] private MeshRenderer _cubeRenderer;
    [SerializeField] private RawImage _displayImage;
    [SerializeField] private Transform _rotateTarget;
    [SerializeField] private float _rotateSpeed = 90f;

    [Header("订阅配置")]
    [SerializeField] private bool _subscribeTexA = true;
    [SerializeField] private bool _subscribeTexB = true;

    [Header("本地模拟")]
    [SerializeField] private bool _startLocal = true;
    [Range(0.1f, 2f)]
    [SerializeField] private float _localSimInterval = 0.3f;

    #endregion

    #region 面板常量

    private const float _panelW = 240f;
    private const float _panelMargin = 10f;
    private const float _panelPad = 8f;
    private const float _lineH = 22f;
    private const float _ctrlH = 28f;

    #endregion

    #region 私有状态

    private bool _isLocal;
    private float _localTimer;
    private int _localPatternIndex;
    private Dictionary<byte, RenderTexture> _outputRTs = new Dictionary<byte, RenderTexture>();
    private List<byte> _texIds = new List<byte>();

    #endregion

    #region 公开 API

    public bool IsLocal => _isLocal;
    public bool IsConnected => !_isLocal && _streamClient != null && _streamClient.IsConnected;

    #endregion

    #region Unity 生命周期

    void Start()
    {
        if (_streamClient == null)
        {
            _streamClient = FindObjectOfType<StreamClient>();
            if (_streamClient == null)
            {
                Debug.LogError("ReceiverSetup: No StreamClient found in scene.");
                return;
            }
        }

        _streamClient.OnTextureAnnounce += OnTextureAnnounce;

        if (_startLocal)
            SwitchToLocal();
        else
            SwitchToRemote();
    }

    void Update()
    {
        if (_rotateTarget != null)
            _rotateTarget.Rotate(Vector3.up, _rotateSpeed * Time.deltaTime);

        if (_isLocal)
            UpdateLocalSim();
    }

    void OnGUI()
    {
        float px = Screen.width - _panelW - _panelMargin;
        float py = _panelMargin;
        float ph = CalcPanelHeight();
        Rect panelRect = new Rect(px, py, _panelW, ph);

        GUI.Box(panelRect, "");
        GUI.BeginGroup(panelRect);

        float y = _panelPad;

        y = DrawModeButtons(y);
        y += 6f;
        y = DrawStatus(y);
        y += 4f;
        DrawTextureInfo(y);

        GUI.EndGroup();
    }

    void OnDestroy()
    {
        if (_streamClient != null)
            _streamClient.OnTextureAnnounce -= OnTextureAnnounce;

        ReleaseAllRTs();
    }

    #endregion

    #region 模式切换

    void SwitchToLocal()
    {
        _isLocal = true;
        _streamClient.Disconnect();
        _localTimer = 0f;

        CreateLocalOutputRTs();
    }

    void SwitchToRemote()
    {
        _isLocal = false;
        _localTimer = 0f;

        ReleaseAllRTs();
        _texIds.Clear();

        if (_subscribeTexA) _texIds.Add(0);
        if (_subscribeTexB) _texIds.Add(1);

        for (int i = 0; i < _texIds.Count; i++)
            CreateOutputRT(_texIds[i], 2, 2);

        _streamClient.Connect(_texIds.ToArray());
    }

    void CreateLocalOutputRTs()
    {
        ReleaseAllRTs();
        _texIds.Clear();

        if (_subscribeTexA) _texIds.Add(0);
        if (_subscribeTexB) _texIds.Add(1);

        for (int i = 0; i < _texIds.Count; i++)
        {
            byte texId = _texIds[i];
            CreateOutputRT(texId, 512, 512);
            RenderTexture rt = _outputRTs[texId];
            BindRTToDisplay(texId, rt);
        }
    }

    #endregion

    #region 本地模拟

    void UpdateLocalSim()
    {
        _localTimer += Time.deltaTime;
        if (_localTimer < _localSimInterval) return;
        _localTimer = 0f;

        _localPatternIndex = (_localPatternIndex + 1) % 8;
        Color color = IndexToColor(_localPatternIndex);

        foreach (RenderTexture rt in _outputRTs.Values)
        {
            RenderTexture.active = rt;
            GL.Clear(true, true, color);
        }
        RenderTexture.active = null;
    }

    static Color IndexToColor(int index)
    {
        switch (index)
        {
            case 0: return Color.red;
            case 1: return Color.green;
            case 2: return Color.blue;
            case 3: return Color.yellow;
            case 4: return Color.cyan;
            case 5: return Color.magenta;
            case 6: return Color.white;
            default: return Color.gray;
        }
    }

    #endregion

    #region 纹理管理

    void CreateOutputRT(byte texId, int w, int h)
    {
        RenderTexture rt = new RenderTexture(w, h, 0, RenderTextureFormat.ARGB32)
        {
            enableRandomWrite = true,
            filterMode = FilterMode.Bilinear
        };
        _outputRTs[texId] = rt;
        _streamClient.BindOutputTexture(texId, rt);
    }

    void ResizeOutputRT(byte texId, int w, int h)
    {
        if (_outputRTs.TryGetValue(texId, out RenderTexture existing))
        {
            if (existing.width == w && existing.height == h) return;
            existing.Release();
            existing.width = w;
            existing.height = h;
            existing.Create();
        }
        else
        {
            CreateOutputRT(texId, w, h);
        }
    }

    void BindRTToDisplay(byte texId, RenderTexture rt)
    {
        if (texId == 0 && _cubeRenderer != null)
            _cubeRenderer.material.mainTexture = rt;
        if (texId == 1 && _displayImage != null)
            _displayImage.texture = rt;
    }

    void ReleaseAllRTs()
    {
        foreach (RenderTexture rt in _outputRTs.Values)
        {
            if (rt != null) rt.Release();
        }
        _outputRTs.Clear();
    }

    #endregion

    #region 事件处理

    void OnTextureAnnounce(byte texId, int w, int h)
    {
        Debug.Log($"ReceiverSetup: TextureAnnounce texId={texId} ({w}x{h})");

        ResizeOutputRT(texId, w, h);
        BindRTToDisplay(texId, _outputRTs[texId]);
    }

    #endregion

    #region GUI 面板

    float CalcPanelHeight()
    {
        int texLines = _outputRTs.Count > 0 ? _outputRTs.Count : 1;
        float h = _panelPad;
        h += _ctrlH;
        h += 6f;
        h += _lineH;
        if (!_isLocal && _streamClient != null && _streamClient.IsConnected)
            h += _lineH;
        h += 4f;
        h += _lineH * texLines;
        h += _panelPad;
        return h;
    }

    float DrawModeButtons(float y)
    {
        float halfW = (_panelW - _panelPad * 2 - 4f) * 0.5f;
        Color prev = GUI.backgroundColor;

        GUI.backgroundColor = _isLocal ? new Color(0.3f, 0.7f, 0.3f) : Color.gray;
        if (GUI.Button(new Rect(_panelPad, y, halfW, _ctrlH), "本地"))
        {
            if (!_isLocal) SwitchToLocal();
        }

        GUI.backgroundColor = !_isLocal ? new Color(0.3f, 0.7f, 0.3f) : Color.gray;
        if (GUI.Button(new Rect(_panelPad + halfW + 4f, y, halfW, _ctrlH), "远程"))
        {
            if (_isLocal) SwitchToRemote();
        }

        GUI.backgroundColor = prev;
        return y + _ctrlH;
    }

    float DrawStatus(float y)
    {
        string status;

        if (_isLocal)
            status = "模式: 本地模拟";
        else if (_streamClient.IsConnected)
            status = string.Format("已连接 {0}:{1}", SceneConfig.HostIP, SceneConfig.Port);
        else
            status = "未连接";

        GUI.Label(new Rect(_panelPad, y, _panelW - _panelPad * 2, _lineH), status);

        if (!_isLocal && _streamClient.IsConnected)
        {
            y += _lineH;
            GUI.Label(new Rect(_panelPad, y, _panelW - _panelPad * 2, _lineH),
                string.Format("延迟: {0:F0}ms  收到: {1} tiles", _streamClient.NetLagMs, _streamClient.DirtyTilesReceived));
        }

        return y + _lineH;
    }

    float DrawTextureInfo(float y)
    {
        if (_outputRTs.Count == 0)
        {
            GUI.Label(new Rect(_panelPad, y, _panelW - _panelPad * 2, _lineH), "\u2014");
            return y + _lineH;
        }

        foreach (KeyValuePair<byte, RenderTexture> kv in _outputRTs)
        {
            byte texId = kv.Key;
            int w = kv.Value.width;
            int h = kv.Value.height;
            string label = _isLocal
                ? string.Format("texId={0}: {1}\u00d7{2}", texId, w, h)
                : string.Format("texId={0}: {1}\u00d7{2}", texId, w, h);
            GUI.Label(new Rect(_panelPad, y, _panelW - _panelPad * 2, _lineH), label);
            y += _lineH;
        }

        return y;
    }

    #endregion
}
