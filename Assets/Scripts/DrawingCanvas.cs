using UnityEngine;
using UnityEngine.UI;

[RequireComponent(typeof(RawImage))]
public class DrawingCanvas : MonoBehaviour
{
    [Header("画布")]
    public int textureWidth = 512;
    public int textureHeight = 512;

    [Header("笔刷")]
    public Color brushColor = Color.black;
    [Range(0.001f, 0.1f)]
    public float brushSize = 0.01f;

    private RenderTexture canvasRT;
    private RenderTexture tempRT;
    private Material brushMat;
    private RawImage rawImage;
    private RectTransform rt;
    private Vector2? prevUV;

    public RenderTexture CanvasTexture => canvasRT;

    void Awake()
    {
        rawImage = GetComponent<RawImage>();
        rt = GetComponent<RectTransform>();
    }

    void Start()
    {
        canvasRT = new RenderTexture(textureWidth, textureHeight, 0, RenderTextureFormat.ARGB32)
        {
            filterMode = FilterMode.Bilinear
        };
        tempRT = new RenderTexture(textureWidth, textureHeight, 0, RenderTextureFormat.ARGB32)
        {
            filterMode = FilterMode.Bilinear
        };

        ClearCanvas();
        rawImage.texture = canvasRT;
        brushMat = new Material(Shader.Find("Custom/Brush"));
    }

    void Update()
    {
        if (Input.GetMouseButtonDown(0))
        {
            var uv = ScreenToUV(Input.mousePosition);
            if (uv.HasValue)
            {
                prevUV = uv.Value;
                DrawAt(uv.Value);
            }
        }
        else if (Input.GetMouseButton(0) && prevUV.HasValue)
        {
            var uv = ScreenToUV(Input.mousePosition);
            if (uv.HasValue)
            {
                float dist = Vector2.Distance(prevUV.Value, uv.Value);
                int steps = Mathf.Max(1, Mathf.CeilToInt(dist / (brushSize * 0.5f)));
                for (int i = 1; i <= steps; i++)
                    DrawAt(Vector2.Lerp(prevUV.Value, uv.Value, (float)i / steps));
                prevUV = uv.Value;
            }
        }
        else if (Input.GetMouseButtonUp(0))
        {
            prevUV = null;
        }
    }

    Vector2? ScreenToUV(Vector2 screenPos)
    {
        if (!RectTransformUtility.RectangleContainsScreenPoint(rt, screenPos))
            return null;

        RectTransformUtility.ScreenPointToLocalPointInRectangle(rt, screenPos, null, out var lp);
        var rect = rt.rect;
        return new Vector2(
            Mathf.InverseLerp(rect.xMin, rect.xMax, lp.x),
            Mathf.InverseLerp(rect.yMin, rect.yMax, lp.y)
        );
    }

    void DrawAt(Vector2 uv)
    {
        brushMat.SetVector("_BrushPos", new Vector4(uv.x, uv.y, brushSize, 0));
        brushMat.SetColor("_BrushColor", brushColor);

        Graphics.Blit(canvasRT, tempRT);
        Graphics.Blit(tempRT, canvasRT, brushMat);
    }

    public void SetRed() => brushColor = Color.red;
    public void SetGreen() => brushColor = Color.green;
    public void SetBlue() => brushColor = Color.blue;

    public void ClearCanvas()
    {
        RenderTexture.active = canvasRT;
        GL.Clear(true, true, Color.white);
        RenderTexture.active = null;
    }

    void OnDestroy()
    {
        if (canvasRT != null) canvasRT.Release();
        if (tempRT != null) tempRT.Release();
        if (brushMat != null) Destroy(brushMat);
    }
}
