using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;
using UnityEngine.Serialization;

public class DrawableSurface : MonoBehaviour
{
    [Header("笔刷")]
    public Color brushColor = Color.black;
    [Range(0.001f, 0.1f)]
    public float brushSize = 0.01f;

    [Header("3D")]
    [SerializeField] private bool _is3D;
    [SerializeField] private Camera _camera3D;
    [SerializeField] private Collider _collider3D;

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

    private bool _externalRT;
    private bool _isAuthoritative = true;
    private List<Vector2> _pendingPoints = new List<Vector2>();

    public RenderTexture CanvasTexture => _canvasRT;

    public bool IsAuthoritative
    {
        get => _isAuthoritative;
        set => _isAuthoritative = value;
    }

    public event System.Action<Color32, float, Vector2[]> OnUserDraw;

    void Awake()
    {
        _rawImage = GetComponent<RawImage>();
        _rt = GetComponent<RectTransform>();
    }

    void Start()
    {
        if (!_externalRT)
        {
            int size = SceneConfig.TextureSize;
            if (size < 64) size = 64;
            if (size > 8192) size = 8192;

            _canvasRT = new RenderTexture(size, size, 0, RenderTextureFormat.ARGB32)
            {
                filterMode = FilterMode.Bilinear
            };
        }

        _tempRT = new RenderTexture(_canvasRT.width, _canvasRT.height, 0, RenderTextureFormat.ARGB32)
        {
            filterMode = FilterMode.Bilinear
        };

        ClearCanvas();
        if (_rawImage != null) _rawImage.texture = _canvasRT;
        _brushMat = new Material(Shader.Find("Custom/Brush"));

        if (_redBtn != null) _redBtn.onClick.AddListener(SetRed);
        if (_greenBtn != null) _greenBtn.onClick.AddListener(SetGreen);
        if (_blueBtn != null) _blueBtn.onClick.AddListener(SetBlue);
        if (_clearBtn != null) _clearBtn.onClick.AddListener(ClearCanvas);
    }

    public void Initialize(RenderTexture targetRT)
    {
        _canvasRT = targetRT;
        _externalRT = true;
    }

    public void ApplyRemoteCommand(Color32 color, float size, float[] points)
    {
        _brushMat.SetColor("_BrushColor", color);
        for (int i = 0; i < points.Length; i += 2)
        {
            _brushMat.SetVector("_BrushPos", new Vector4(points[i], points[i + 1], size, 0));
            Graphics.Blit(_canvasRT, _tempRT);
            Graphics.Blit(_tempRT, _canvasRT, _brushMat);
        }
    }

    void Update()
    {
        if (Input.GetMouseButtonDown(0))
        {
            Vector2? uv = ScreenToUV(Input.mousePosition);
            if (uv.HasValue)
            {
                _prevUV = uv.Value;
                if (_isAuthoritative) DrawAt(uv.Value);
                else _pendingPoints.Add(uv.Value);
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
                {
                    Vector2 lerped = Vector2.Lerp(_prevUV.Value, uv.Value, (float)i / steps);
                    if (_isAuthoritative) DrawAt(lerped);
                    else _pendingPoints.Add(lerped);
                }
                _prevUV = uv.Value;
            }
        }
        else if (Input.GetMouseButtonUp(0))
        {
            if (!_isAuthoritative && _pendingPoints.Count > 0)
            {
                Color32 c32 = new Color32(
                    (byte)(brushColor.r * 255), (byte)(brushColor.g * 255),
                    (byte)(brushColor.b * 255), (byte)(brushColor.a * 255));
                OnUserDraw?.Invoke(c32, brushSize, _pendingPoints.ToArray());
                _pendingPoints.Clear();
            }
            _prevUV = null;
        }
    }

    Vector2? ScreenToUV(Vector2 screenPos)
    {
        if (_is3D)
        {
            Camera cam = _camera3D != null ? _camera3D : Camera.main;
            if (cam == null) return null;

            Ray ray = cam.ScreenPointToRay(screenPos);
            if (_collider3D != null && _collider3D.Raycast(ray, out RaycastHit hit, 1000f))
                return hit.textureCoord;

            return null;
        }

        if (_rt == null) return null;

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
        if (!_externalRT && _canvasRT != null) _canvasRT.Release();
        if (_tempRT != null) _tempRT.Release();
        if (_brushMat != null) Destroy(_brushMat);
    }
}
