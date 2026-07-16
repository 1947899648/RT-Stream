using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Threading;
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

    private List<string> _discoveredIPs = new List<string>();
    private object _scanLock = new object();

    void Start()
    {
        _senderBtn.onClick.AddListener(LoadSender);
        _receiverBtn.onClick.AddListener(LoadReceiver);

        InitIPDropdown();
        ThreadPool.QueueUserWorkItem(_ => ScanSubnet());

        if (_sizeDropdown != null && _sizeDropdown.options.Count > 0)
            _sizeDropdown.value = 0;
    }

    void Update()
    {
        lock (_scanLock)
        {
            if (_discoveredIPs.Count == 0) return;
            foreach (string ip in _discoveredIPs)
            {
                if (!_ipDropdown.options.Any(o => o.text == ip))
                    _ipDropdown.options.Add(new Dropdown.OptionData(ip));
            }
            _discoveredIPs.Clear();
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
        if (_ipDropdown != null && _ipDropdown.options.Count > 0)
            SceneConfig.HostIP = _ipDropdown.options[_ipDropdown.value].text;

        if (int.TryParse(_portInput.text, out int p) && p > 0 && p < 65536)
            SceneConfig.Port = p;

        if (_sizeDropdown != null && _sizeDropdown.options.Count > 0)
        {
            string sizeText = _sizeDropdown.options[_sizeDropdown.value].text;
            if (int.TryParse(sizeText, out int s) && s >= 64 && s <= 4096)
                SceneConfig.TextureSize = s;
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
            foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (ni.OperationalStatus != OperationalStatus.Up) continue;

                foreach (var addr in ni.GetIPProperties().UnicastAddresses)
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
                var hostEntry = Dns.GetHostEntry(Dns.GetHostName());
                foreach (var ipAddr in hostEntry.AddressList)
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

    void ScanSubnet()
    {
        string wifiIP = null;
        try
        {
            foreach (string ip in GetLocalIPs())
            {
                if (ip != "127.0.0.1" && ip.StartsWith("192.168."))
                {
                    wifiIP = ip;
                    break;
                }
            }
        }
        catch { }

        if (string.IsNullOrEmpty(wifiIP)) return;

        int lastDot = wifiIP.LastIndexOf('.');
        string prefix = wifiIP.Substring(0, lastDot + 1);

        int pending = 0;
        int port = SceneConfig.Port;

        for (int i = 1; i <= 254; i++)
        {
            string target = prefix + i;
            if (target == wifiIP) continue;

            Interlocked.Increment(ref pending);
            ThreadPool.QueueUserWorkItem(_ =>
            {
                try
                {
                    Socket socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
                    socket.Blocking = false;

                    try
                    {
                        socket.Connect(target, port);
                        lock (_scanLock) _discoveredIPs.Add(target);
                    }
                    catch (SocketException ex)
                    {
                        if (ex.SocketErrorCode == SocketError.WouldBlock)
                        {
                            if (socket.Poll(200000, SelectMode.SelectWrite))
                            {
                                if (socket.RemoteEndPoint != null)
                                    lock (_scanLock) _discoveredIPs.Add(target);
                            }
                        }
                    }
                    finally
                    {
                        socket.Close();
                    }
                }
                catch { }
                finally
                {
                    Interlocked.Decrement(ref pending);
                }
            });
        }

        while (pending > 0)
            Thread.Sleep(50);
    }
}
