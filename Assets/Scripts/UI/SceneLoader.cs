using System.Collections.Generic;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.UI;
using UnityEngine.Serialization;

public class SceneLoader : MonoBehaviour
{
    [FormerlySerializedAs("senderBtn")]
    [SerializeField] private Button _senderBtn;
    [FormerlySerializedAs("receiverBtn")]
    [SerializeField] private Button _receiverBtn;
    [FormerlySerializedAs("ipInput")]
    [SerializeField] private Dropdown _ipDropdown;
    [FormerlySerializedAs("portInput")]
    [SerializeField] private InputField _portInput;
    [FormerlySerializedAs("_sizeInput")]
    [SerializeField] private Dropdown _sizeDropdown;

    private NetworkDiscovery _discovery;
    private InputField _ipInputField;
    private bool _isManualInput;
    private const string ManualEntryLabel = "\u624b\u52a8\u8f93\u5165...";

    void Start()
    {
        _senderBtn.onClick.AddListener(LoadSender);
        _receiverBtn.onClick.AddListener(LoadReceiver);

        _discovery = GetComponent<NetworkDiscovery>();
        InitIPDropdown();
        _ipDropdown.options.Add(new Dropdown.OptionData(ManualEntryLabel));
        _ipDropdown.onValueChanged.AddListener(OnIPDropdownChanged);
        CreateIPInputField();

        if (_sizeDropdown != null)
        {
            _sizeDropdown.options.Clear();
            _sizeDropdown.options.Add(new Dropdown.OptionData("512"));
            _sizeDropdown.options.Add(new Dropdown.OptionData("1024"));
            _sizeDropdown.options.Add(new Dropdown.OptionData("2048"));
            _sizeDropdown.options.Add(new Dropdown.OptionData("4096"));
            _sizeDropdown.options.Add(new Dropdown.OptionData("8192"));
            _sizeDropdown.value = 0;
        }
    }

    void Update()
    {
        if (_discovery == null) return;

        while (_discovery.TryGetDiscoveredIP(out string ip))
        {
            int insertIdx = _ipDropdown.options.Count - 1;
            if (insertIdx < 0) insertIdx = 0;
            _ipDropdown.options.Insert(insertIdx, new Dropdown.OptionData(ip));
        }
    }

    void LoadSender()
    {
        ApplyConfig();
        SceneManager.LoadScene("Sender");
    }

    void LoadReceiver()
    {
        ApplyConfig();
        SceneManager.LoadScene("Receiver");
    }

    void ApplyConfig()
    {
        if (_isManualInput && _ipInputField != null)
        {
            string text = _ipInputField.text.Trim();
            if (!string.IsNullOrEmpty(text))
                SceneConfig.HostIP = text;
        }
        else if (_ipDropdown != null && _ipDropdown.options.Count > 0)
        {
            int idx = _ipDropdown.value;
            if (idx >= 0 && idx < _ipDropdown.options.Count)
                SceneConfig.HostIP = _ipDropdown.options[idx].text;
        }

        if (int.TryParse(_portInput.text, out int p) && p > 0 && p < 65536)
            SceneConfig.Port = p;

        if (_sizeDropdown != null && _sizeDropdown.options.Count > 0)
        {
            string sizeText = _sizeDropdown.options[_sizeDropdown.value].text;
            if (int.TryParse(sizeText, out int s) && s >= 64 && s <= 8192)
            {
                SceneConfig.TextureWidth = s;
                SceneConfig.TextureHeight = s;
            }
        }
    }

    void CreateIPInputField()
    {
        if (_ipDropdown == null) return;

        RectTransform dropRT = _ipDropdown.GetComponent<RectTransform>();
        Transform parent = dropRT.parent;

        GameObject go = new GameObject("IP InputField");
        go.transform.SetParent(parent, false);

        RectTransform rt = go.AddComponent<RectTransform>();
        rt.anchorMin = dropRT.anchorMin;
        rt.anchorMax = dropRT.anchorMax;
        rt.anchoredPosition = dropRT.anchoredPosition;
        rt.sizeDelta = dropRT.sizeDelta;
        rt.localScale = dropRT.localScale;
        rt.pivot = dropRT.pivot;

        Image bg = go.AddComponent<Image>();
        bg.color = new Color(0.15f, 0.15f, 0.15f, 0.9f);

        InputField inputField = go.AddComponent<InputField>();

        GameObject textGo = new GameObject("Text");
        textGo.transform.SetParent(go.transform, false);
        RectTransform textRT = textGo.AddComponent<RectTransform>();
        textRT.anchorMin = Vector2.zero;
        textRT.anchorMax = Vector2.one;
        textRT.offsetMin = new Vector2(10, 6);
        textRT.offsetMax = new Vector2(-10, -6);

        Text textComp = textGo.AddComponent<Text>();
        textComp.font = Font.CreateDynamicFontFromOSFont("Arial", 13);
        textComp.fontSize = 13;
        textComp.color = Color.white;
        textComp.alignment = TextAnchor.MiddleLeft;
        textComp.supportRichText = false;
        inputField.textComponent = textComp;

        GameObject plcGo = new GameObject("Placeholder");
        plcGo.transform.SetParent(go.transform, false);
        RectTransform plcRT = plcGo.AddComponent<RectTransform>();
        plcRT.anchorMin = Vector2.zero;
        plcRT.anchorMax = Vector2.one;
        plcRT.offsetMin = new Vector2(10, 6);
        plcRT.offsetMax = new Vector2(-10, -6);

        Text plcComp = plcGo.AddComponent<Text>();
        plcComp.font = Font.CreateDynamicFontFromOSFont("Arial", 13);
        plcComp.fontSize = 13;
        plcComp.color = new Color(0.5f, 0.5f, 0.5f, 1f);
        plcComp.alignment = TextAnchor.MiddleLeft;
        plcComp.supportRichText = false;
        plcComp.text = "手动输入IP...";
        inputField.placeholder = plcComp;

        inputField.lineType = InputField.LineType.SingleLine;
        inputField.onEndEdit.AddListener(OnIPInputEndEdit);

        _ipInputField = inputField;
        _ipInputField.gameObject.SetActive(false);
    }

    void OnIPInputEndEdit(string text)
    {
        _ipInputField.gameObject.SetActive(false);
        _ipDropdown.gameObject.SetActive(true);

        string ip = text.Trim();
        if (!string.IsNullOrEmpty(ip) && ip != ManualEntryLabel)
            _ipDropdown.captionText.text = ip;
    }

    void OnIPDropdownChanged(int index)
    {
        if (_ipInputField == null) return;

        if (index == _ipDropdown.options.Count - 1)
        {
            _ipDropdown.gameObject.SetActive(false);
            _ipInputField.gameObject.SetActive(true);
            _isManualInput = true;

            string currentIP = "";
            if (index - 1 >= 0)
                currentIP = _ipDropdown.options[index - 1].text;
            _ipInputField.text = currentIP;
            _ipInputField.ActivateInputField();
        }
        else
        {
            _isManualInput = false;
            _ipInputField.gameObject.SetActive(false);
            _ipDropdown.gameObject.SetActive(true);
        }
    }

    void InitIPDropdown()
    {
        if (_ipDropdown == null) return;

        List<string> localIPs = GetLocalIPs();
        _ipDropdown.options.Clear();
        foreach (string ip in localIPs)
            _ipDropdown.options.Add(new Dropdown.OptionData(ip));

        if (localIPs.Count > 0)
            _ipDropdown.value = localIPs[0] == "127.0.0.1" && localIPs.Count > 1 ? 1 : 0;
    }

    List<string> GetLocalIPs()
    {
        List<string> ips = new List<string>();
        ips.Add("127.0.0.1");

        try
        {
            foreach (NetworkInterface ni in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (ni.OperationalStatus != OperationalStatus.Up) continue;

                foreach (UnicastIPAddressInformation addr in ni.GetIPProperties().UnicastAddresses)
                {
                    if (addr.Address.AddressFamily == AddressFamily.InterNetwork)
                    {
                        string ip = addr.Address.ToString();
                        if (!ips.Contains(ip))
                            ips.Add(ip);
                    }
                }
            }

            if (ips.Count == 1)
            {
                IPHostEntry hostEntry = Dns.GetHostEntry(Dns.GetHostName());
                foreach (IPAddress ipAddr in hostEntry.AddressList)
                {
                    if (ipAddr.AddressFamily == AddressFamily.InterNetwork)
                    {
                        string ip = ipAddr.ToString();
                        if (!ips.Contains(ip))
                            ips.Add(ip);
                    }
                }
            }
        }
        catch { }

        return ips;
    }
}
