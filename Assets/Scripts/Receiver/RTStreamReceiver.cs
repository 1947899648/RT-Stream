using UnityEngine;
using UnityEngine.UI;
using UnityEngine.Serialization;

public class RTStreamReceiver : MonoBehaviour
{
    public RTLocalSim localSim;
    public StreamClient streamClient;
    [FormerlySerializedAs("localBtn")]
    [SerializeField] private Button _localBtn;
    [FormerlySerializedAs("webBtn")]
    [SerializeField] private Button _webBtn;
    [SerializeField] private RawImage _displayImage;
    [SerializeField] private MeshRenderer _displayMesh;

    void Start()
    {
        int w = SceneConfig.TextureWidth;
        int h = SceneConfig.TextureHeight;
        if (w < 64) w = 64;
        if (w > 8192) w = 8192;
        if (h < 64) h = 64;
        if (h > 8192) h = 8192;

        if (SceneConfig.DisplayRT == null || SceneConfig.DisplayRT.width != w || SceneConfig.DisplayRT.height != h
            || !SceneConfig.DisplayRT.enableRandomWrite)
        {
            if (SceneConfig.DisplayRT != null) SceneConfig.DisplayRT.Release();
            SceneConfig.DisplayRT = new RenderTexture(w, h, 0, RenderTextureFormat.ARGB32)
            {
                enableRandomWrite = true
            };
        }

        _displayImage.texture = SceneConfig.DisplayRT;
        _displayMesh.material.mainTexture = SceneConfig.DisplayRT;

        _localBtn.onClick.AddListener(SetLocal);
        _webBtn.onClick.AddListener(SetWeb);
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
        RenderTexture.active = SceneConfig.DisplayRT;
        GL.Clear(true, true, Color.black);
        RenderTexture.active = null;
        streamClient.Connect();
    }
}
