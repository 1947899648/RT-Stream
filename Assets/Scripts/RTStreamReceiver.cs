using UnityEngine;
using UnityEngine.UI;

public class RTStreamReceiver : MonoBehaviour
{
    public RenderTexture displayRT;
    public RTLocalSim localSim;
    public Button localBtn;
    public Button webBtn;

    void Start()
    {
        SetLocal();
    }

    public void SetLocal()
    {
        localBtn.interactable = false;
        webBtn.interactable = true;
        localSim.enabled = true;
    }

    public void SetWeb()
    {
        localBtn.interactable = true;
        webBtn.interactable = false;
        localSim.enabled = false;
        RenderTexture.active = displayRT;
        GL.Clear(true, true, Color.black);
        RenderTexture.active = null;
    }
}
