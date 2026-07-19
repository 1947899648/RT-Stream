using UnityEngine;
using UnityEngine.UI;

public class RTStreamReceiver : MonoBehaviour
{
    [SerializeField] private StreamClient _streamClient;
    [SerializeField] private RawImage _displayImage;
    [SerializeField] private MeshRenderer _displayMesh;

    void Start()
    {
        if (_streamClient == null)
        {
            _streamClient = GetComponent<StreamClient>();
            if (_streamClient == null) return;
        }

        if (_displayImage != null)
            _displayImage.texture = _streamClient.DisplayRT_UI;
        if (_displayMesh != null)
            _displayMesh.material.mainTexture = _streamClient.DisplayRT_3D;
    }
}
