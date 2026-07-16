using UnityEngine;
using UnityEngine.UI;
using UnityEngine.Serialization;

[RequireComponent(typeof(RawImage))]
public class DrawingCanvas : MonoBehaviour
{
    [Header("笔刷")]
    public Color brushColor = Color.black;
    [Range(0.001f, 0.1f)]
    public float brushSize = 0.01f;

    [Header("按钮")]
    [FormerlySerializedAs("redBtn")]
    [SerializeField] private Button _redBtn;
    [FormerlySerializedAs("greenBtn")]
    [SerializeField] private Button _greenBtn;
    [FormerlySerializedAs("blueBtn")]
    [SerializeField] private Button _blueBtn;
    [FormerlySerializedAs("clearBtn")]
    [SerializeField] private Button _clearBtn;

    private RenderTexture _canvasRT;
    private RenderTexture _tempRT;
    private Material _brushMat;
    private RawImage _rawImage;
    private RectTransform _rt;
    private Vector2? _prevUV;

    public RenderTexture CanvasTexture => _canvasRT;

    void Awake()
    {
        _rawImage = GetComponent<RawImage>();
        _rt = GetComponent<RectTransform>();
    }

    void Start()
    {
        int size = SceneConfig.TextureSize;
        if (size < 64) size = 64;
        if (size > 4096) size = 4096;

        _canvasRT = new RenderTexture(size, size, 0, RenderTextureFormat.ARGB32)
        {
            filterMode = FilterMode.Bilinear
        };
        _tempRT = new RenderTexture(size, size, 0, RenderTextureFormat.ARGB32)
        {
            filterMode = FilterMode.Bilinear
        };

        ClearCanvas();
        _rawImage.texture = _canvasRT;
        _brushMat = new Material(Shader.Find("Custom/Brush"));

        _redBtn.onClick.AddListener(SetRed);
        _greenBtn.onClick.AddListener(SetGreen);
        _blueBtn.onClick.AddListener(SetBlue);
        _clearBtn.onClick.AddListener(ClearCanvas);
    }

    void Update()
    {
        if (Input.GetMouseButtonDown(0))
        {
            Vector2? uv = ScreenToUV(Input.mousePosition);
            if (uv.HasValue)
            {
                _prevUV = uv.Value;
                DrawAt(uv.Value);
            }
        }
        else if (Input.GetMouseButton(0) && _prevUV.HasValue)
        {
            Vector2? uv = ScreenToUV(Input.mousePosition);
            if (uv.HasValue)
            {
                float dist = Vector2.Distance(_prevUV.Value, uv.Value);
                int steps = Mathf.Max(1, Mathf.CeilToInt(dist / (brushSize * 0.5f)));
                for (int i = 1; i <= steps; i++)
                    DrawAt(Vector2.Lerp(_prevUV.Value, uv.Value, (float)i / steps));
                _prevUV = uv.Value;
            }
        }
        else if (Input.GetMouseButtonUp(0))
        {
            _prevUV = null;
        }
    }

    Vector2? ScreenToUV(Vector2 screenPos)
    {
        if (!RectTransformUtility.RectangleContainsScreenPoint(_rt, screenPos))
            return null;

        RectTransformUtility.ScreenPointToLocalPointInRectangle(_rt, screenPos, null, out Vector2 lp);
        Rect rect = _rt.rect;
        return new Vector2(
            Mathf.InverseLerp(rect.xMin, rect.xMax, lp.x),
            Mathf.InverseLerp(rect.yMin, rect.yMax, lp.y)
        );
    }

    void DrawAt(Vector2 uv)
    {
        _brushMat.SetVector("_BrushPos", new Vector4(uv.x, uv.y, brushSize, 0));
        _brushMat.SetColor("_BrushColor", brushColor);

        Graphics.Blit(_canvasRT, _tempRT);
        Graphics.Blit(_tempRT, _canvasRT, _brushMat);
    }

    private void SetRed() => brushColor = Color.red;
    private void SetGreen() => brushColor = Color.green;
    private void SetBlue() => brushColor = Color.blue;

    public void ClearCanvas()
    {
        RenderTexture.active = _canvasRT;
        GL.Clear(true, true, Color.white);
        RenderTexture.active = null;
    }

    void OnDestroy()
    {
        if (_canvasRT != null) _canvasRT.Release();
        if (_tempRT != null) _tempRT.Release();
        if (_brushMat != null) Destroy(_brushMat);
    }
}
