using System.Collections.Generic;
using UnityEngine;

public class SenderSetup : MonoBehaviour
{
    [SerializeField] private DrawController _drawController;
    [SerializeField] private StreamHost _streamHost;

    private Dictionary<string, byte> _nameToTexId = new Dictionary<string, byte>();

    void Start()
    {
        if (_drawController == null)
        {
            _drawController = FindObjectOfType<DrawController>();
            if (_drawController == null)
            {
                Debug.LogError("SenderSetup: No DrawController found in scene.");
                return;
            }
        }

        if (_streamHost == null)
        {
            _streamHost = FindObjectOfType<StreamHost>();
            if (_streamHost == null)
            {
                Debug.LogError("SenderSetup: No StreamHost found in scene.");
                return;
            }
        }

        for (int i = 0; i < _drawController.EntryCount; i++)
        {
            string name = _drawController.GetCanvasName(i);
            RenderTexture rt = _drawController.GetCanvasTexture(name);
            if (rt == null) continue;

            byte texId = _streamHost.RegisterTexture(rt);
            _nameToTexId[name] = texId;
            Debug.Log($"SenderSetup: Registered \"{name}\" → texId={texId}");
        }
    }
}
