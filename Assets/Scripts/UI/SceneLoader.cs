using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.UI;
using UnityEngine.Serialization;

public class SceneLoader : MonoBehaviour
{
    [FormerlySerializedAs("senderBtn")]
    [SerializeField] private Button _senderBtn;
    [FormerlySerializedAs("receiverBtn")]
    [SerializeField] private Button _receiverBtn;
    [FormerlySerializedAs("ipInput")]
    [SerializeField] private InputField _ipInput;
    [FormerlySerializedAs("portInput")]
    [SerializeField] private InputField _portInput;
    [SerializeField] private InputField _sizeInput;

    void Start()
    {
        _senderBtn.onClick.AddListener(LoadSender);
        _receiverBtn.onClick.AddListener(LoadReceiver);
    }

    void LoadSender()
    {
        ApplyConfig();
        SceneManager.LoadScene("Sender");
    }

    void LoadReceiver()
    {
        ApplyConfig();
        SceneManager.LoadScene("Receiver");
    }

    void ApplyConfig()
    {
        if (!string.IsNullOrWhiteSpace(_ipInput.text))
            SceneConfig.HostIP = _ipInput.text.Trim();

        if (int.TryParse(_portInput.text, out int p) && p > 0 && p < 65536)
            SceneConfig.Port = p;

        if (int.TryParse(_sizeInput.text, out int s) && s >= 64 && s <= 4096)
            SceneConfig.TextureSize = s;
    }
}
