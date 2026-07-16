using UnityEngine;
using UnityEngine.UI;
using UnityEngine.Serialization;

public class RTStreamReceiver : MonoBehaviour
{
    public RenderTexture displayRT;
    public RTLocalSim localSim;
    public StreamClient streamClient;
    [FormerlySerializedAs("localBtn")]
    [SerializeField] private Button _localBtn;
    [FormerlySerializedAs("webBtn")]
    [SerializeField] private Button _webBtn;

    void Start()
    {
        _localBtn.onClick.AddListener(SetLocal);
        _webBtn.onClick.AddListener(SetWeb);
        streamClient.displayRT = displayRT;
        SetLocal();
    }

    private void SetLocal()
    {
        _localBtn.interactable = false;
        _webBtn.interactable = true;
        localSim.enabled = true;
        streamClient.Disconnect();
    }

    private void SetWeb()
    {
        _localBtn.interactable = true;
        _webBtn.interactable = false;
        localSim.enabled = false;
        RenderTexture.active = displayRT;
        GL.Clear(true, true, Color.black);
        RenderTexture.active = null;
        streamClient.Connect();
    }
}
