using System;
using System.Collections.Generic;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using UnityEngine;

public class NetworkDiscovery : MonoBehaviour
{
    private const int DiscoveryPort = 7778;
    private const string Magic = "RTSS";
    private const float BroadcastInterval = 1f;

    private UdpClient _udpClient;
    private Thread _receiveThread;
    private object _queueLock = new object();
    private Queue<string> _discoveredQueue = new Queue<string>();
    private HashSet<string> _discoveredSet = new HashSet<string>();
    private bool _running;
    private float _timer;

    void Start()
    {
        try
        {
            _udpClient = new UdpClient();
            _udpClient.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
            _udpClient.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.Broadcast, 1);
            _udpClient.Client.Bind(new IPEndPoint(IPAddress.Any, DiscoveryPort));
        }
        catch
        {
            Debug.LogWarning("NetworkDiscovery: failed to bind UDP port");
            return;
        }

        _running = true;
        _receiveThread = new Thread(ReceiveLoop) { IsBackground = true };
        _receiveThread.Start();
    }

    void Update()
    {
        _timer += Time.deltaTime;
        if (_timer >= BroadcastInterval)
        {
            _timer = 0f;
            Broadcast();
        }
    }

    void Broadcast()
    {
        try
        {
            byte[] packet = new byte[6];
            Encoding.ASCII.GetBytes(Magic, 0, 4, packet, 0);
            BitConverter.GetBytes((ushort)SceneConfig.Port).CopyTo(packet, 4);
            _udpClient.Send(packet, packet.Length, new IPEndPoint(IPAddress.Broadcast, DiscoveryPort));
        }
        catch { }
    }

    void ReceiveLoop()
    {
        IPEndPoint remote = new IPEndPoint(IPAddress.Any, DiscoveryPort);
        while (_running)
        {
            try
            {
                byte[] data = _udpClient.Receive(ref remote);
                if (data.Length < 6) continue;
                if (Encoding.ASCII.GetString(data, 0, 4) != Magic) continue;

                string ip = remote.Address.ToString();
                if (IsLocalIP(ip)) continue;

                lock (_queueLock)
                {
                    if (_discoveredSet.Add(ip))
                        _discoveredQueue.Enqueue(ip);
                }
            }
            catch (SocketException)
            {
                if (_running) continue;
                break;
            }
            catch
            {
                break;
            }
        }
    }

    public bool TryGetDiscoveredIP(out string ip)
    {
        lock (_queueLock)
        {
            if (_discoveredQueue.Count > 0)
            {
                ip = _discoveredQueue.Dequeue();
                return true;
            }
        }
        ip = null;
        return false;
    }

    bool IsLocalIP(string ip)
    {
        List<string> localIPs = GetLocalIPs();
        return localIPs.Contains(ip);
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
                        if (!ips.Contains(ip)) ips.Add(ip);
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
                        if (!ips.Contains(ip)) ips.Add(ip);
                    }
                }
            }
        }
        catch { }

        return ips;
    }

    void OnDestroy()
    {
        _running = false;
        _receiveThread?.Join(500);
        _udpClient?.Close();
    }
}
