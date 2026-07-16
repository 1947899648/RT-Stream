using UnityEngine.SceneManagement;
using UnityEngine;

public class SceneLoader : MonoBehaviour
{
    public void LoadSender()
    {
        SceneManager.LoadScene("Sender");
    }

    public void LoadReceiver()
    {
        SceneManager.LoadScene("Receiver");
    }
}
