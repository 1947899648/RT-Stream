using System.Collections.Generic;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.UI;
using WPZ0325.RTStream;

[System.Serializable]
public struct DisplayBinding
{
    public string texId;
    public MeshRenderer meshTarget;
    public RawImage uiTarget;
}

public class ReceiverSetup : MonoBehaviour
{
    #region 序列化字段

    [Header("核心模块")]
    [SerializeField] private MonoRTStreamReceiver _streamClient;

    [Header("显示绑定")]
    [SerializeField] private DisplayBinding[] _bindings;

    [SerializeField] private Transform _rotateTarget;
    [SerializeField] private float _rotateSpeed = 90f;

    [Header("本地模拟")]
    [SerializeField] private bool _startLocal = true;
    [Range(0.1f, 2f)]
    [SerializeField] private float _localSimInterval = 0.3f;

    #endregion

    #region 面板常量

    private const float _panelW = 250f;
    private const float _panelMarginX = 10f;
    private const float _panelMarginB = 40f;
    private const float _panelPad = 8f;
    private const float _lineH = 22f;
    private const float _ctrlH = 28f;

    #endregion

    #region 私有状态

    private bool _isLocal;
    private float _localTimer;
    private int _localPatternIndex;
    private string _ipInput;
    private string _portInput;
    private Dictionary<string, RenderTexture> _outputRTs = new Dictionary<string, RenderTexture>();
    private List<string> _texIds = new List<string>();

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
            _streamClient = FindObjectOfType<MonoRTStreamReceiver>();
            if (_streamClient == null)
            {
                Debug.LogError("ReceiverSetup: No MonoRTStreamReceiver found in scene.");
                return;
            }
        }

        _streamClient.OnRenderTextureAnnounced += OnRenderTextureAnnounced;

        _ipInput = "127.0.0.1";
        _portInput = "7777";

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
        float px = Screen.width - _panelW - _panelMarginX;
        float ph = CalcPanelHeight();
        float py = Screen.height - ph - _panelMarginB;
        Rect panelRect = new Rect(px, py, _panelW, ph);

        GUI.Box(panelRect, "");
        GUI.BeginGroup(panelRect);

        float y = _panelPad;

        y = DrawModeButtons(y);
        y += 6f;

        if (!_isLocal)
        {
            y = DrawRemoteConfig(y);
            y += 6f;
        }

        y = DrawStatus(y);
        y += 4f;
        y = DrawTextureInfo(y);
        y += 4f;
        DrawMenuButton(y);

        GUI.EndGroup();
    }

    void OnDestroy()
    {
        if (_streamClient != null)
            _streamClient.OnRenderTextureAnnounced -= OnRenderTextureAnnounced;

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

        foreach (DisplayBinding binding in _bindings)
        {
            if (string.IsNullOrEmpty(binding.texId)) continue;
            _texIds.Add(binding.texId);
            CreateOutputRT(binding.texId, 2, 2);
        }
    }

    void ConnectToRemote()
    {
        if (!int.TryParse(_portInput, out int port) || port <= 0 || port >= 65536) return;
        if (string.IsNullOrEmpty(_ipInput)) return;

        _streamClient.Connect(_ipInput, port, _texIds.ToArray());
    }

    void CreateLocalOutputRTs()
    {
        ReleaseAllRTs();
        _texIds.Clear();

        foreach (DisplayBinding binding in _bindings)
        {
            if (string.IsNullOrEmpty(binding.texId)) continue;
            _texIds.Add(binding.texId);
            CreateOutputRT(binding.texId, 512, 512);
            RenderTexture rt = _outputRTs[binding.texId];
            BindRTToDisplay(binding.texId, rt);
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

    void CreateOutputRT(string texId, int w, int h)
    {
        RenderTexture rt = new RenderTexture(w, h, 0, RenderTextureFormat.ARGB32)
        {
            enableRandomWrite = true,
            filterMode = FilterMode.Bilinear
        };
        _outputRTs[texId] = rt;
        _streamClient.BindOutputTexture(texId, rt);
    }

    void ResizeOutputRT(string texId, int w, int h)
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

    void BindRTToDisplay(string texId, RenderTexture rt)
    {
        foreach (DisplayBinding binding in _bindings)
        {
            if (binding.texId != texId) continue;
            if (binding.meshTarget != null)
                binding.meshTarget.material.mainTexture = rt;
            if (binding.uiTarget != null)
                binding.uiTarget.texture = rt;
        }
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

    void OnRenderTextureAnnounced(string texId, int w, int h)
    {
        Debug.Log($"ReceiverSetup: TextureAnnounce texId={texId} ({w}x{h})");

        ResizeOutputRT(texId, w, h);
        BindRTToDisplay(texId, _outputRTs[texId]);
    }

    #endregion

    #region GUI 面板

    float CalcPanelHeight()
    {
        float h = _panelPad;
        h += _ctrlH;
        h += 6f;

        if (!_isLocal)
        {
            h += _ctrlH;
            h += 2f;
            h += _ctrlH;
            h += 4f;
            h += _ctrlH;
            h += 6f;
        }

        h += _lineH;
        if (!_isLocal && _streamClient != null && _streamClient.IsConnected)
            h += _lineH;
        h += 4f;

        int texLines = _outputRTs.Count > 0 ? _outputRTs.Count : 1;
        h += _lineH * texLines;
        h += 4f;
        h += _ctrlH;
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

    float DrawRemoteConfig(float y)
    {
        float labelW = 36f;
        float fieldW = _panelW - _panelPad * 2 - labelW - 4f;

        GUI.Label(new Rect(_panelPad, y, labelW, _lineH), "IP:");
        _ipInput = GUI.TextField(new Rect(_panelPad + labelW, y, fieldW, _ctrlH), _ipInput);
        y += _ctrlH + 2f;

        GUI.Label(new Rect(_panelPad, y, labelW, _lineH), "端口:");
        _portInput = GUI.TextField(new Rect(_panelPad + labelW, y, fieldW, _ctrlH), _portInput);
        y += _ctrlH + 4f;

        bool isConnected = _streamClient != null && _streamClient.IsConnected;
        string btnText = isConnected ? "断开" : "连接";

        if (GUI.Button(new Rect(_panelPad, y, _panelW - _panelPad * 2, _ctrlH), btnText))
        {
            if (isConnected)
                _streamClient.Disconnect();
            else
                ConnectToRemote();
        }

        return y + _ctrlH;
    }

    float DrawStatus(float y)
    {
        string status;

        if (_isLocal)
            status = "模式: 本地模拟";
        else if (_streamClient.IsConnected)
            status = string.Format("已连接 {0}:{1}", _streamClient.RemoteHost, _streamClient.RemotePort);
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

        foreach (KeyValuePair<string, RenderTexture> kv in _outputRTs)
        {
            string texId = kv.Key;
            int w = kv.Value.width;
            int h = kv.Value.height;
            string label = string.Format("{0}: {1}\u00d7{2}", texId, w, h);
            GUI.Label(new Rect(_panelPad, y, _panelW - _panelPad * 2, _lineH), label);
            y += _lineH;
        }

        return y;
    }

    float DrawMenuButton(float y)
    {
        if (GUI.Button(new Rect(_panelPad, y, _panelW - _panelPad * 2, _ctrlH), "返回主菜单"))
            SceneManager.LoadScene("MainScene");

        return y + _ctrlH;
    }

    #endregion
}
