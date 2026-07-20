using System.Collections.Generic;
using UnityEngine;
using UnityEngine.SceneManagement;

public class SenderSetup : MonoBehaviour
{
    #region 序列化字段

    [SerializeField] private DrawController _drawController;
    [SerializeField] private StreamHost _streamHost;

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

    private Dictionary<string, byte> _nameToTexId = new Dictionary<string, byte>();
    private string _portInput;
    private string _ipInput;

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
            _streamHost = FindObjectOfType<StreamHost>();
            if (_streamHost == null)
            {
                Debug.LogError("SenderSetup: No StreamHost found in scene.");
                return;
            }
        }

        _ipInput = "0.0.0.0";
        _portInput = SceneConfig.Port.ToString();

        for (int i = 0; i < _drawController.EntryCount; i++)
        {
            string name = _drawController.GetCanvasName(i);
            RenderTexture rt = _drawController.GetCanvasTexture(name);
            if (rt == null) continue;

            byte texId = _streamHost.RegisterTexture(rt);
            _nameToTexId[name] = texId;
            Debug.Log($"SenderSetup: Registered \"{name}\" → texId={texId}");
        }
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
        DrawMenuButton(y);

        GUI.EndGroup();
    }

    #endregion

    #region GUI 面板

    float CalcPanelHeight()
    {
        return _panelPad + _ctrlH + 2f + _ctrlH + 4f + _ctrlH + 4f + _lineH + 4f + _ctrlH + _panelPad;
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
        bool running = _streamHost.IsRunning;

        if (running)
        {
            if (GUI.Button(new Rect(_panelPad, y, _panelW - _panelPad * 2, _ctrlH), "停止监听"))
                _streamHost.StopHost();
        }
        else
        {
            if (GUI.Button(new Rect(_panelPad, y, _panelW - _panelPad * 2, _ctrlH), "启动监听"))
            {
                if (int.TryParse(_portInput, out int port) && port > 0 && port < 65536)
                    _streamHost.StartHost(port);
                else
                    _portInput = SceneConfig.Port.ToString();
            }
        }

        return y + _ctrlH;
    }

    float DrawClientCount(float y)
    {
        int count = _streamHost.IsRunning ? _streamHost.ClientCount : 0;
        GUI.Label(new Rect(_panelPad, y, _panelW - _panelPad * 2, _lineH),
            string.Format("客户端: {0}", count));

        return y + _lineH;
    }

    float DrawMenuButton(float y)
    {
        if (GUI.Button(new Rect(_panelPad, y, _panelW - _panelPad * 2, _ctrlH), "返回主菜单"))
            SceneManager.LoadScene("MainScene");

        return y + _ctrlH;
    }

    #endregion
}
