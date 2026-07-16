using System;
using System.Collections.Concurrent;
using System.Net.Sockets;
using System.Threading;
using UnityEngine;

public class StreamClient : MonoBehaviour
{
    public string hostIP = "127.0.0.1";
    public int port = 7777;
    public RenderTexture displayRT;

    private TcpClient tcpClient;
    private NetworkStream stream;
    private Thread receiveThread;
    private ConcurrentQueue<byte[]> frameQueue = new ConcurrentQueue<byte[]>();
    private bool connected;
    private bool running;

    private Texture2D tex2D;
    private byte[] tileBuffer;
    private bool initialized;
    private int texWidth, texHeight;

    public bool IsConnected => connected;

    public void Connect()
    {
        Disconnect();
        try
        {
            tcpClient = new TcpClient();
            tcpClient.Connect(hostIP, port);
            stream = tcpClient.GetStream();
            connected = true;
            running = true;
            receiveThread = new Thread(ReceiveLoop) { IsBackground = true };
            receiveThread.Start();
        }
        catch (Exception e)
        {
            Debug.LogError($"StreamClient connect failed: {e.Message}");
            Close();
        }
    }

    public void Disconnect()
    {
        running = false;
        connected = false;
        initialized = false;
        receiveThread?.Join(500);
        Close();
        while (frameQueue.TryDequeue(out _)) { }
    }

    void Update()
    {
        if (!connected) return;

        while (frameQueue.TryDequeue(out var packet))
        {
            ProcessFrame(packet);
        }
    }

    void ReceiveLoop()
    {
        byte[] lenBuf = new byte[4];
        while (running && tcpClient != null && tcpClient.Connected)
        {
            try
            {
                if (!ReadExact(stream, lenBuf, 0, 4)) break;
                int frameLen = BitConverter.ToInt32(lenBuf, 0);
                if (frameLen <= 0 || frameLen > 64 * 1024 * 1024) break;

                byte[] frameData = new byte[frameLen];
                if (!ReadExact(stream, frameData, 0, frameLen)) break;
                frameQueue.Enqueue(frameData);
            }
            catch
            {
                break;
            }
        }
        connected = false;
    }

    bool ReadExact(NetworkStream s, byte[] buf, int offset, int count)
    {
        int received = 0;
        while (received < count)
        {
            int n = s.Read(buf, offset + received, count - received);
            if (n <= 0) return false;
            received += n;
        }
        return true;
    }

    void ProcessFrame(byte[] packet)
    {
        var type = FrameCodec.GetFrameType(packet);
        if (type == FrameType.KeyFrame)
        {
            FrameCodec.DecodeKeyFrame(packet, out texWidth, out texHeight, out var pixels);
            tileBuffer = pixels;

            if (tex2D != null) Destroy(tex2D);
            tex2D = new Texture2D(texWidth, texHeight, TextureFormat.RGBA32, false);
            initialized = true;
        }
        else if (type == FrameType.DeltaFrame)
        {
            if (!initialized) return;
            var tiles = FrameCodec.DecodeDeltaFrame(packet);
            int rowLen = texWidth * 4;

            foreach (var tile in tiles)
            {
                int tileX = (tile.index % (texWidth / 16)) * 16;
                int tileY = (tile.index / (texWidth / 16)) * 16;

                for (int y = 0; y < 16; y++)
                {
                    int dstOffset = ((tileY + y) * rowLen) + tileX * 4;
                    Buffer.BlockCopy(tile.data, y * 16 * 4, tileBuffer, dstOffset, 16 * 4);
                }
            }
        }

        if (initialized && tileBuffer != null)
        {
            tex2D.LoadRawTextureData(tileBuffer);
            tex2D.Apply();
            Graphics.Blit(tex2D, displayRT);
        }
    }

    void Close()
    {
        stream?.Close();
        tcpClient?.Close();
        stream = null;
        tcpClient = null;
    }

    void OnDestroy()
    {
        Disconnect();
        if (tex2D != null) Destroy(tex2D);
    }
}
