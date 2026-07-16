using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.UI;

public class SceneLoader : MonoBehaviour
{
    [SerializeField] private Button senderBtn;
    [SerializeField] private Button receiverBtn;
    [SerializeField] private InputField ipInput;
    [SerializeField] private InputField portInput;

    void Start()
    {
        senderBtn.onClick.AddListener(() => SceneManager.LoadScene("Sender"));
        receiverBtn.onClick.AddListener(LoadReceiver);
    }

    void LoadReceiver()
    {
        if (!string.IsNullOrWhiteSpace(ipInput.text))
            SceneConfig.HostIP = ipInput.text.Trim();

        if (int.TryParse(portInput.text, out int p) && p > 0 && p < 65536)
            SceneConfig.Port = p;

        SceneManager.LoadScene("Receiver");
    }
}
