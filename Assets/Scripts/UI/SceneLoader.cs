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

    void Start()
    {
        _senderBtn.onClick.AddListener(() => SceneManager.LoadScene("Sender"));
        _receiverBtn.onClick.AddListener(LoadReceiver);
    }

    void LoadReceiver()
    {
        if (!string.IsNullOrWhiteSpace(_ipInput.text))
            SceneConfig.HostIP = _ipInput.text.Trim();

        if (int.TryParse(_portInput.text, out int p) && p > 0 && p < 65536)
            SceneConfig.Port = p;

        SceneManager.LoadScene("Receiver");
    }
}
