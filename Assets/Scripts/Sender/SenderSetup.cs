using UnityEngine;

public class SenderSetup : MonoBehaviour
{
    [SerializeField] private DrawingCanvas _canvasA;
    [SerializeField] private DrawingCanvas _canvasB;
    [SerializeField] private StreamHost _streamHost;

    void Start()
    {
        if (_streamHost == null)
        {
            _streamHost = FindObjectOfType<StreamHost>();
            if (_streamHost == null)
            {
                Debug.LogError("SenderSetup: No StreamHost found in scene.");
                return;
            }
        }

        if (_canvasA != null && _canvasA.CanvasTexture != null)
        {
            byte idA = _streamHost.RegisterTexture(_canvasA.CanvasTexture);
            Debug.Log($"SenderSetup: Registered canvasA → texId={idA}");
        }

        if (_canvasB != null && _canvasB.CanvasTexture != null)
        {
            byte idB = _streamHost.RegisterTexture(_canvasB.CanvasTexture);
            Debug.Log($"SenderSetup: Registered canvasB → texId={idB}");
        }
    }
}
