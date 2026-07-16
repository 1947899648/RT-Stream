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
        style.fontSize = 20;
        style.normal.textColor = Color.green;
        style.fontStyle = FontStyle.Bold;

        GUI.Label(new Rect(10, 10, 200, 30), $"{fps:0.} FPS ({ms:0.0} ms)", style);
    }
}
