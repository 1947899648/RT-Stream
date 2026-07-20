using UnityEngine;
using UnityEngine.SceneManagement;

public class MainMenu : MonoBehaviour
{
    private const float _btnW = 300f;
    private const float _btnH = 80f;
    private const int _historyLen = 150;
    private const float _chartW = 400f;
    private const float _chartH = 100f;

    private float _deltaTime;
    private float[] _history = new float[_historyLen];
    private int _writeIdx;
    private int _count;
    private float _sampleTimer;

    void Awake()
    {
        QualitySettings.vSyncCount = 0;
        Application.targetFrameRate = 9999;
    }

    void Update()
    {
        _deltaTime += (Time.unscaledDeltaTime - _deltaTime) * 0.1f;

        _sampleTimer += Time.unscaledDeltaTime;
        if (_sampleTimer >= 0.1f)
        {
            _sampleTimer = 0f;
            _history[_writeIdx] = 1f / _deltaTime;
            _writeIdx = (_writeIdx + 1) % _historyLen;
            if (_count < _historyLen) _count++;
        }
    }

    void OnGUI()
    {
        float cx = Screen.width * 0.5f;
        float cy = Screen.height * 0.5f;
        float chartX = cx - _chartW * 0.5f;
        float chartY = cy - _btnH * 0.5f - _chartH - 30f;

        DrawFpsChart(new Rect(chartX, chartY, _chartW, _chartH));

        int fps = Mathf.RoundToInt(1f / _deltaTime);
        GUIStyle fpsStyle = new GUIStyle
        {
            fontSize = 18,
            fontStyle = FontStyle.Bold,
            alignment = TextAnchor.MiddleCenter,
            normal = { textColor = Color.green }
        };
        GUI.Label(new Rect(cx - 200f, chartY + _chartH + 4f, 400f, 20f), $"{fps} FPS", fpsStyle);

        float senderX = cx - _btnW - 10f;
        float btnY = cy - _btnH * 0.5f;
        float receiverX = cx + 10f;

        GUIStyle btnStyle = new GUIStyle(GUI.skin.button)
        {
            fontSize = 32,
            fontStyle = FontStyle.Bold
        };

        if (GUI.Button(new Rect(senderX, btnY, _btnW, _btnH), "Sender", btnStyle))
            SceneManager.LoadScene("Sender");

        if (GUI.Button(new Rect(receiverX, btnY, _btnW, _btnH), "Receiver", btnStyle))
            SceneManager.LoadScene("Receiver");
    }

    void DrawFpsChart(Rect r)
    {
        Color prev = GUI.color;
        GUI.color = new Color(0f, 0f, 0f, 0.6f);
        GUI.DrawTexture(r, Texture2D.whiteTexture);
        GUI.color = prev;

        if (_count < 2) return;

        float maxFps = 0f;
        for (int i = 0; i < _count; i++)
        {
            float v = _history[i];
            if (v > maxFps) maxFps = v;
        }
        if (maxFps < 1f) return;
        float scaleY = (r.height - 4f) / maxFps;

        float stepX = r.width / (_count - 1);
        int readIdx = _writeIdx;

        for (int i = 1; i < _count; i++)
        {
            int prevIdx = (readIdx - i + _historyLen + 1) % _historyLen;
            int currIdx = (readIdx - i + _historyLen) % _historyLen;

            float x0 = r.x + (i - 1) * stepX;
            float y0 = r.y + r.height - 2f - _history[prevIdx] * scaleY;
            float x1 = r.x + i * stepX;
            float y1 = r.y + r.height - 2f - _history[currIdx] * scaleY;

            if (y0 < r.y) y0 = r.y;
            if (y1 < r.y) y1 = r.y;

            DrawLine(x0, y0, x1, y1, Color.green);
        }
    }

    void DrawLine(float x0, float y0, float x1, float y1, Color color)
    {
        float dx = x1 - x0;
        float dy = y1 - y0;
        float steps = Mathf.Max(Mathf.Abs(dx), Mathf.Abs(dy));

        Color prev = GUI.color;
        GUI.color = color;

        for (int i = 0; i <= steps; i++)
        {
            float t = i / steps;
            float x = x0 + dx * t;
            float y = y0 + dy * t;
            GUI.DrawTexture(new Rect(x, y, 1f, 1f), Texture2D.whiteTexture);
        }

        GUI.color = prev;
    }
}
