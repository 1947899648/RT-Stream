using UnityEngine;
using UnityEngine.SceneManagement;

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

        SceneManager.sceneLoaded += (scene, mode) => DetectRole();

        QualitySettings.vSyncCount = 0;
        Application.targetFrameRate = 9999;
    }

    void Start()
    {
        DetectRole();
    }

    void DetectRole()
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
            int size = SceneConfig.TextureSize;
            int rtMem = size * size * 4;
            int t = size / SceneConfig.TileSize;
            _styleSmall.normal.textColor = new Color(0.7f, 1f, 0.7f);
            GUI.Label(new Rect(0, y, Screen.width, 22),
                $"RT: {size}x{size}  RGBA32  {rtMem / 1024f / 1024f:F1} MB  Tile: {t}x{t} ({t * t})", _styleSmall);
            y += 26;
        }
        else if (_client != null && SceneConfig.DisplayRT != null)
        {
            int rtMem = SceneConfig.DisplayRT.width * SceneConfig.DisplayRT.height * 4;
            _styleSmall.normal.textColor = new Color(0.7f, 0.7f, 1f);
            GUI.Label(new Rect(0, y, Screen.width, 22),
                $"RT: {SceneConfig.DisplayRT.width}x{SceneConfig.DisplayRT.height}  {SceneConfig.DisplayRT.format}  {rtMem / 1024f / 1024f:F1} MB", _styleSmall);
            y += 26;
        }

        if (_host != null)
        {
            int kf = _host.keyFrameInterval - (_host.DiagSeq % _host.keyFrameInterval);
            _styleSmall.normal.textColor = new Color(0.7f, 1f, 0.7f);
            GUI.Label(new Rect(0, y, Screen.width, 22),
                $"Clients: {_host.ClientCount}  Port: {_host.port}  KeyFrame: {kf}/{_host.keyFrameInterval}", _styleSmall);
            y += 26;

            string diag = _host.GetClientDiagnostics();
            if (!string.IsNullOrEmpty(diag))
            {
                _styleSmall.normal.textColor = new Color(1f, 0.9f, 0.5f);
                GUI.Label(new Rect(0, y, Screen.width, 22), diag, _styleSmall);
            }
        }
        else if (_client != null)
        {
            _styleSmall.normal.textColor = _client.IsConnected ? Color.green : Color.red;
            GUI.Label(new Rect(0, y, Screen.width, 22),
                _client.IsConnected
                    ? $"Connected  {_client.hostIP}:{_client.port}  batch:{_client.LastBatchSize} skip:{_client.SkippedFrames}"
                    : "Disconnected", _styleSmall);
        }

        DrawHardwareInfo();
    }

    void DrawHardwareInfo()
    {
        int bottom = Screen.height - 10;

        string memLine = "System RAM: -- MB    VRAM: -- MB";
        if (SystemInfo.systemMemorySize > 0)
            memLine = $"System RAM: {SystemInfo.systemMemorySize} MB";
        if (SystemInfo.graphicsMemorySize > 0)
            memLine += $"    VRAM: {SystemInfo.graphicsMemorySize} MB";

        _styleSmall.normal.textColor = Color.white;
        GUI.Label(new Rect(0, bottom - 22, Screen.width, 22), memLine, _styleSmall);

        string cpuLine = string.IsNullOrEmpty(SystemInfo.processorType)
            ? "CPU: Unknown"
            : $"CPU: {SystemInfo.processorType}";
        GUI.Label(new Rect(0, bottom - 44, Screen.width, 22), cpuLine, _styleSmall);

        string gpuLine = $"GPU: {SystemInfo.graphicsDeviceName}  ({SystemInfo.graphicsDeviceType})";
        _styleSmall.normal.textColor = new Color(0.8f, 0.8f, 1f);
        GUI.Label(new Rect(0, bottom - 66, Screen.width, 22), gpuLine, _styleSmall);

        bool csSupported = SystemInfo.supportsComputeShaders;
        _styleLarge.normal.textColor = csSupported ? Color.green : Color.red;
        GUI.Label(new Rect(0, bottom - 115, Screen.width, 42),
            csSupported ? "Compute Shader: Supported" : "Compute Shader: Not Supported", _styleLarge);
    }
}
