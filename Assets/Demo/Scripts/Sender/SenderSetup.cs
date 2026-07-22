using System.Collections.Generic;
using UnityEngine;
using UnityEngine.SceneManagement;
using WPZ0325.RTStream;

public class SenderSetup : MonoBehaviour
{
    #region 序列化字段

    [SerializeField] private DrawController _drawController;
    [SerializeField] private MonoRTStreamSender _streamHost;

    #endregion

    #region 面板常量

    private const float _panelW = 220f;
    private const float _panelMarginX = 10f;
    private const float _panelMarginB = 40f;
    private const float _panelPad = 8f;
    private const float _lineH = 22f;
    private const float _ctrlH = 28f;

    #endregion

    #region 私有状态

    private string _portInput;
    private string _ipInput;
    private bool _isRunning;
    private int _clientCount;
    private HashSet<string> _registeredTexIds = new HashSet<string>();
    private HashSet<string> _pausedTexIds = new HashSet<string>();

    #endregion

    #region Unity 生命周期

    void Start()
    {
        if (_drawController == null)
        {
            _drawController = FindObjectOfType<DrawController>();
            if (_drawController == null)
            {
                Debug.LogError("SenderSetup: No DrawController found in scene.");
                return;
            }
        }

        if (_streamHost == null)
        {
            _streamHost = FindObjectOfType<MonoRTStreamSender>();
            if (_streamHost == null)
            {
                Debug.LogError("SenderSetup: No MonoRTStreamSender found in scene.");
                return;
            }
        }

        _streamHost.OnHostStarted += OnHostStarted;
        _streamHost.OnHostStopped += OnHostStopped;
        _streamHost.OnClientConnected += OnClientConnected;
        _streamHost.OnClientDisconnected += OnClientDisconnected;
        _streamHost.OnRenderTextureUnregistered += OnRenderTextureUnregistered;

        _ipInput = "0.0.0.0";
        _portInput = "9500";
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

        y = DrawNetworkConfig(y);
        y += 4f;
        y = DrawHostControl(y);
        y += 4f;
        y = DrawClientCount(y);
        y += 4f;
        y = DrawTextureControls(y);
        y += 4f;
        DrawMenuButton(y);

        GUI.EndGroup();
    }

    void OnDestroy()
    {
        if (_streamHost != null)
        {
            _streamHost.OnHostStarted -= OnHostStarted;
            _streamHost.OnHostStopped -= OnHostStopped;
            _streamHost.OnClientConnected -= OnClientConnected;
            _streamHost.OnClientDisconnected -= OnClientDisconnected;
            _streamHost.OnRenderTextureUnregistered -= OnRenderTextureUnregistered;
        }
    }

    #endregion

    #region 事件处理

    void OnHostStarted()
    {
        _isRunning = true;
    }

    void OnHostStopped()
    {
        _isRunning = false;
        _clientCount = 0;
        _registeredTexIds.Clear();
        _pausedTexIds.Clear();
    }

    void OnClientConnected(int totalClients)
    {
        _clientCount = totalClients;
    }

    void OnClientDisconnected(int totalClients)
    {
        _clientCount = totalClients;
    }

    void OnRenderTextureUnregistered(string texId)
    {
        _registeredTexIds.Remove(texId);
        _pausedTexIds.Remove(texId);
    }

    #endregion

    #region GUI 面板

    float CalcPanelHeight()
    {
        int texLines = _drawController != null ? _drawController.EntryCount : 0;
        return _panelPad + _ctrlH + 2f + _ctrlH + 4f + _ctrlH + 4f + _lineH + 4f + _lineH * texLines + 4f + _ctrlH + _panelPad;
    }

    float DrawNetworkConfig(float y)
    {
        float labelW = 36f;
        float fieldW = _panelW - _panelPad * 2 - labelW - 4f;

        GUI.Label(new Rect(_panelPad, y, labelW, _lineH), "IP:");
        _ipInput = GUI.TextField(new Rect(_panelPad + labelW, y, fieldW, _ctrlH), _ipInput);
        y += _ctrlH + 2f;

        GUI.Label(new Rect(_panelPad, y, labelW, _lineH), "端口:");
        _portInput = GUI.TextField(new Rect(_panelPad + labelW, y, fieldW, _ctrlH), _portInput);
        y += _ctrlH;

        return y;
    }

    float DrawHostControl(float y)
    {
        if (_isRunning)
        {
            if (GUI.Button(new Rect(_panelPad, y, _panelW - _panelPad * 2, _ctrlH), "停止监听"))
                _streamHost.StopHost();
        }
        else
        {
            if (GUI.Button(new Rect(_panelPad, y, _panelW - _panelPad * 2, _ctrlH), "启动监听"))
            {
                if (int.TryParse(_portInput, out int port) && port > 0 && port < 65536)
                    _streamHost.StartHost(_ipInput, port);
                else
                    _portInput = _streamHost.ListenPort.ToString();
            }
        }

        return y + _ctrlH;
    }

    float DrawClientCount(float y)
    {
        GUI.Label(new Rect(_panelPad, y, _panelW - _panelPad * 2, _lineH),
            string.Format("客户端: {0}", _clientCount));

        return y + _lineH;
    }

    float DrawTextureControls(float y)
    {
        if (_drawController == null) return y;

        bool hostRunning = _isRunning;

        for (int i = 0; i < _drawController.EntryCount; i++)
        {
            string texId = _drawController.GetCanvasName(i);
            if (string.IsNullOrEmpty(texId)) continue;

            bool isRegistered = _registeredTexIds.Contains(texId);
            bool isPaused = _pausedTexIds.Contains(texId);

            float toggleW = _panelW - _panelPad * 2 - 50f;
            string label = isPaused ? texId + " (已暂停)" : texId;

            GUI.enabled = hostRunning;
            bool toggled = GUI.Toggle(new Rect(_panelPad, y, toggleW, _lineH), isRegistered, label);
            GUI.enabled = true;

            if (hostRunning && toggled != isRegistered)
            {
                if (toggled)
                {
                    _streamHost.RegisterTexture(texId, _drawController.GetCanvasTexture(texId));
                    _registeredTexIds.Add(texId);
                }
                else
                {
                    _streamHost.UnregisterTexture(texId);
                    _registeredTexIds.Remove(texId);
                    _pausedTexIds.Remove(texId);
                }
            }

            if (isRegistered)
            {
                GUI.enabled = hostRunning;
                string btnText = isPaused ? "恢复" : "暂停";
                if (GUI.Button(new Rect(_panelPad + toggleW + 2f, y, 48f, _lineH), btnText))
                {
                    bool enable = isPaused;
                    _streamHost.SetTextureEnabled(texId, enable);
                    if (enable)
                        _pausedTexIds.Remove(texId);
                    else
                        _pausedTexIds.Add(texId);
                }
                GUI.enabled = true;
            }

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
