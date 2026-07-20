using UnityEngine;
using UnityEngine.SceneManagement;

public class MainMenu : MonoBehaviour
{
    private const float _btnW = 300f;
    private const float _btnH = 80f;

    void OnGUI()
    {
        float cx = Screen.width * 0.5f;
        float cy = Screen.height * 0.5f;

        float senderX = cx - _btnW - 10f;
        float senderY = cy - _btnH * 0.5f;
        float receiverX = cx + 10f;
        float receiverY = senderY;

        GUIStyle style = new GUIStyle(GUI.skin.button)
        {
            fontSize = 32,
            fontStyle = FontStyle.Bold
        };

        if (GUI.Button(new Rect(senderX, senderY, _btnW, _btnH), "Sender", style))
            SceneManager.LoadScene("Sender");

        if (GUI.Button(new Rect(receiverX, receiverY, _btnW, _btnH), "Receiver", style))
            SceneManager.LoadScene("Receiver");
    }
}
