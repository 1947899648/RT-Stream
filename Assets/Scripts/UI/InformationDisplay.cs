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

    void Awake()
    {
        if (_instance != null) { Destroy(gameObject); return; }
        _instance = this;
        DontDestroyOnLoad(gameObject);

        _styleLarge = new GUIStyle { fontSize = 36, fontStyle = FontStyle.Bold, alignment = TextAnchor.UpperCenter };
        _styleSmall = new GUIStyle { fontSize = 18, fontStyle = FontStyle.Normal, alignment = TextAnchor.UpperCenter };
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
        GUI.Label(new Rect(0, y, Screen.width, 42), $"{fps:0} FPS ({ms:0.0} ms)", _styleLarge);
        y += 46;

        _styleSmall.normal.textColor = Color.white;
        GUI.Label(new Rect(0, y, Screen.width, 22), _role, _styleSmall);
        y += 26;

        if (_canvas != null)
        {
            int rtMem = _canvas.textureWidth * _canvas.textureHeight * 4;
            int tx = _canvas.textureWidth / 16;
            int ty = _canvas.textureHeight / 16;
            _styleSmall.normal.textColor = new Color(0.7f, 1f, 0.7f);
            GUI.Label(new Rect(0, y, Screen.width, 22),
                $"RT: {_canvas.textureWidth}x{_canvas.textureHeight}  RGBA32  {rtMem / 1024f / 1024f:F1} MB  Tile: {tx}x{ty} ({tx * ty})", _styleSmall);
            y += 26;
        }
        else if (_client != null && _client.displayRT != null)
        {
            int rtMem = _client.displayRT.width * _client.displayRT.height * 4;
            _styleSmall.normal.textColor = new Color(0.7f, 0.7f, 1f);
            GUI.Label(new Rect(0, y, Screen.width, 22),
                $"RT: {_client.displayRT.width}x{_client.displayRT.height}  {_client.displayRT.format}  {rtMem / 1024f / 1024f:F1} MB", _styleSmall);
            y += 26;
        }

        if (_host != null)
        {
            int kf = _host.keyFrameInterval - (_host.DiagSeq % _host.keyFrameInterval);
            _styleSmall.normal.textColor = new Color(0.7f, 1f, 0.7f);
            GUI.Label(new Rect(0, y, Screen.width, 22),
                $"Clients: {_host.ClientCount}  Port: {_host.port}  Target: {_host.targetFps} FPS  KeyFrame: {kf}/{_host.keyFrameInterval}", _styleSmall);
        }
        else if (_client != null)
        {
            _styleSmall.normal.textColor = _client.IsConnected ? Color.green : Color.red;
            GUI.Label(new Rect(0, y, Screen.width, 22),
                _client.IsConnected ? $"Connected  {_client.hostIP}:{_client.port}" : "Disconnected", _styleSmall);
        }
    }
}
