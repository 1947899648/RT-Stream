using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.UI;

public class SceneReset : MonoBehaviour
{
    [SerializeField] private Button _resetBtn;

    void Start()
    {
        if (_resetBtn == null) _resetBtn = GetComponent<Button>();
        if (_resetBtn != null)
            _resetBtn.onClick.AddListener(() => SceneManager.LoadScene("MainScene"));
    }
}
