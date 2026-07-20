using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;

public class ReceiverSetup : MonoBehaviour
{
    [SerializeField] private StreamClient _streamClient;

    [Header("Display Targets")]
    [SerializeField] private MeshRenderer _cubeRenderer;
    [SerializeField] private RawImage _displayImage;

    [Header("Subscription")]
    [SerializeField] private bool _subscribeTexA = true;
    [SerializeField] private bool _subscribeTexB = true;

    private Dictionary<byte, RenderTexture> _outputRTs = new Dictionary<byte, RenderTexture>();
    private List<byte> _texIds = new List<byte>();

    void Start()
    {
        if (_streamClient == null)
        {
            _streamClient = FindObjectOfType<StreamClient>();
            if (_streamClient == null)
            {
                Debug.LogError("ReceiverSetup: No StreamClient found in scene.");
                return;
            }
        }

        _streamClient.OnTextureAnnounce += OnTextureAnnounce;

        if (_subscribeTexA)
        {
            byte id = 0;
            _texIds.Add(id);
            CreateOutputRT(id, 2, 2);
        }
        if (_subscribeTexB)
        {
            byte id = 1;
            _texIds.Add(id);
            CreateOutputRT(id, 2, 2);
        }

        _streamClient.Connect(_texIds.ToArray());
    }

    void CreateOutputRT(byte texId, int w, int h)
    {
        RenderTexture rt = new RenderTexture(w, h, 0, RenderTextureFormat.ARGB32)
        {
            enableRandomWrite = true,
            filterMode = FilterMode.Bilinear
        };
        _outputRTs[texId] = rt;
        _streamClient.BindOutputTexture(texId, rt);
    }

    void ResizeOutputRT(byte texId, int w, int h)
    {
        if (_outputRTs.TryGetValue(texId, out RenderTexture existing))
        {
            if (existing.width == w && existing.height == h) return;
            existing.Release();
            existing.width = w;
            existing.height = h;
            existing.Create();
        }
        else
        {
            CreateOutputRT(texId, w, h);
        }
    }

    void OnTextureAnnounce(byte texId, int w, int h)
    {
        Debug.Log($"ReceiverSetup: TextureAnnounce texId={texId} ({w}x{h})");

        ResizeOutputRT(texId, w, h);

        if (texId == 0 && _cubeRenderer != null)
            _cubeRenderer.material.mainTexture = _outputRTs[texId];
        if (texId == 1 && _displayImage != null)
            _displayImage.texture = _outputRTs[texId];
    }

    void OnDestroy()
    {
        if (_streamClient != null)
            _streamClient.OnTextureAnnounce -= OnTextureAnnounce;

        foreach (RenderTexture rt in _outputRTs.Values)
        {
            if (rt != null) rt.Release();
        }
        _outputRTs.Clear();
    }
}
