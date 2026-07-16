using UnityEngine;
using UnityEngine.UI;

public class RTStreamReceiver : MonoBehaviour
{
    public RenderTexture displayRT;
    public RTLocalSim localSim;
    public StreamClient streamClient;
    [SerializeField] private Button localBtn;
    [SerializeField] private Button webBtn;

    void Start()
    {
        localBtn.onClick.AddListener(SetLocal);
        webBtn.onClick.AddListener(SetWeb);
        streamClient.displayRT = displayRT;
        SetLocal();
    }

    private void SetLocal()
    {
        localBtn.interactable = false;
        webBtn.interactable = true;
        localSim.enabled = true;
        streamClient.Disconnect();
    }

    private void SetWeb()
    {
        localBtn.interactable = true;
        webBtn.interactable = false;
        localSim.enabled = false;
        RenderTexture.active = displayRT;
        GL.Clear(true, true, Color.black);
        RenderTexture.active = null;
        streamClient.Connect();
    }
}
