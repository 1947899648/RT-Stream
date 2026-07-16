using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.UI;

public class SceneLoader : MonoBehaviour
{
    [SerializeField] private Button senderBtn;
    [SerializeField] private Button receiverBtn;

    void Start()
    {
        senderBtn.onClick.AddListener(() => SceneManager.LoadScene("Sender"));
        receiverBtn.onClick.AddListener(() => SceneManager.LoadScene("Receiver"));
    }
}
