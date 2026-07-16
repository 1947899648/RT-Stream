using UnityEngine;

public class FpsDisplay : MonoBehaviour
{
    private static FpsDisplay _instance;
    private float _deltaTime;

    void Awake()
    {
        if (_instance != null)
        {
            Destroy(gameObject);
            return;
        }
        _instance = this;
        DontDestroyOnLoad(gameObject);
    }

    void Update()
    {
        _deltaTime += (Time.unscaledDeltaTime - _deltaTime) * 0.1f;
    }

    void OnGUI()
    {
        float fps = 1.0f / _deltaTime;
        float ms = _deltaTime * 1000f;

        GUIStyle style = new GUIStyle();
        style.fontSize = 40;
        style.normal.textColor = Color.green;
        style.fontStyle = FontStyle.Bold;
        style.alignment = TextAnchor.UpperCenter;

        GUI.Label(new Rect(0, 20, Screen.width, 50), $"{fps:0.} FPS ({ms:0.0} ms)", style);
    }
}
