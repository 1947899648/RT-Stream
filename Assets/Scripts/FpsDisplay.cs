using UnityEngine;

public class FpsDisplay : MonoBehaviour
{
    private static FpsDisplay instance;
    private float deltaTime;

    void Awake()
    {
        if (instance != null)
        {
            Destroy(gameObject);
            return;
        }
        instance = this;
        DontDestroyOnLoad(gameObject);
    }

    void Update()
    {
        deltaTime += (Time.unscaledDeltaTime - deltaTime) * 0.1f;
    }

    void OnGUI()
    {
        float fps = 1.0f / deltaTime;
        float ms = deltaTime * 1000f;

        GUIStyle style = new GUIStyle();
        style.fontSize = 40;
        style.normal.textColor = Color.green;
        style.fontStyle = FontStyle.Bold;
        style.alignment = TextAnchor.UpperCenter;

        GUI.Label(new Rect(0, 20, Screen.width, 50), $"{fps:0.} FPS ({ms:0.0} ms)", style);
    }
}
