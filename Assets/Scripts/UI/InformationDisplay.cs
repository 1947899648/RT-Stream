using System.Text;
using UnityEngine;

public class InformationDisplay : MonoBehaviour
{
    private static InformationDisplay _instance;
    private float _deltaTime;
    private DrawingCanvas _canvas;
    private StreamHost _host;
    private StreamClient _client;
    private string _role;
    private GUIStyle _styleLarge;
    private GUIStyle _styleSmall;
    private GUIStyle _styleBottom;
    private StringBuilder _sb = new StringBuilder(256);

    void Awake()
    {
        if (_instance != null) { Destroy(gameObject); return; }
        _instance = this;
        DontDestroyOnLoad(gameObject);

        _styleLarge = new GUIStyle { fontSize = 36, fontStyle = FontStyle.Bold, alignment = TextAnchor.UpperCenter };
        _styleSmall = new GUIStyle { fontSize = 18, fontStyle = FontStyle.Normal, alignment = TextAnchor.UpperCenter };
        _styleBottom = new GUIStyle { fontSize = 16, fontStyle = FontStyle.Normal, alignment = TextAnchor.LowerCenter };
    }

    void Start()
    {
        _canvas = FindObjectOfType<DrawingCanvas>();
        _host = FindObjectOfType<StreamHost>();
        _client = FindObjectOfType<StreamClient>();

        if (_host != null || _canvas != null)
            _role = "Sender (Host)";
        else if (_client != null)
            _role = "Receiver (Client)";
        else
            _role = "Idle";
    }

    void Update()
    {
        _deltaTime += (Time.unscaledDeltaTime - _deltaTime) * 0.1f;
    }

    void OnGUI()
    {
        float fps = 1f / _deltaTime;
        float ms = _deltaTime * 1000f;
        int y = 20;

        _styleLarge.normal.textColor = Color.green;
        _sb.Clear();
        _sb.AppendFormat("{0:0} FPS ({1:0.0} ms)", fps, ms);
        GUI.Label(new Rect(0, y, Screen.width, 42), _sb.ToString(), _styleLarge);
        y += 46;

        _styleSmall.normal.textColor = Color.white;
        GUI.Label(new Rect(0, y, Screen.width, 22), _role, _styleSmall);
        y += 26;

        if (_canvas != null)
        {
            int size = SceneConfig.TextureSize;
            int rtMem = size * size * 4;
            int t = size / 16;
            _styleSmall.normal.textColor = new Color(0.7f, 1f, 0.7f);
            _sb.Clear();
            _sb.AppendFormat("RT: {0}x{0}  RGBA32  {1:F1} MB  Tile: {2}x{2} ({3})",
                size, rtMem / 1024f / 1024f, t, t * t);
            GUI.Label(new Rect(0, y, Screen.width, 22), _sb.ToString(), _styleSmall);
            y += 26;
        }
        else if (_client != null && SceneConfig.DisplayRT != null)
        {
            int rtMem = SceneConfig.DisplayRT.width * SceneConfig.DisplayRT.height * 4;
            _styleSmall.normal.textColor = new Color(0.7f, 0.7f, 1f);
            _sb.Clear();
            _sb.AppendFormat("RT: {0}x{1}  {2}  {3:F1} MB",
                SceneConfig.DisplayRT.width, SceneConfig.DisplayRT.height,
                SceneConfig.DisplayRT.format, rtMem / 1024f / 1024f);
            GUI.Label(new Rect(0, y, Screen.width, 22), _sb.ToString(), _styleSmall);
            y += 26;
        }

        if (_host != null)
        {
            _styleSmall.normal.textColor = new Color(0.7f, 1f, 0.7f);
            _sb.Clear();
            _sb.AppendFormat("Clients: {0}  Port: {1}", _host.ClientCount, _host.port);
            GUI.Label(new Rect(0, y, Screen.width, 22), _sb.ToString(), _styleSmall);
        }
        else if (_client != null)
        {
            _styleSmall.normal.textColor = _client.IsConnected ? Color.green : Color.red;
            _sb.Clear();
            _sb.AppendFormat(_client.IsConnected ? "Connected  {0}:{1}" : "Disconnected",
                _client.hostIP, _client.port);
            GUI.Label(new Rect(0, y, Screen.width, 22), _sb.ToString(), _styleSmall);
        }

        bool csSupported = SystemInfo.supportsComputeShaders;
        _sb.Clear();
        _sb.Append(csSupported ? "ComputeShader: supported" : "ComputeShader: not supported");
        _styleBottom.normal.textColor = csSupported ? Color.green : Color.red;
        GUI.Label(new Rect(0, Screen.height - 30, Screen.width, 22), _sb.ToString(), _styleBottom);
    }
}
