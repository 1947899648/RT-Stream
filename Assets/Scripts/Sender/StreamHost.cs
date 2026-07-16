using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using UnityEngine;

public class StreamHost : MonoBehaviour
{
    public int port = 7777;
    public int targetFps = 30;
    public int keyFrameInterval = 30;

    private TcpListener listener;
    private List<TcpClient> clients = new List<TcpClient>();
    private TileDiffer tileDiffer;
    private int seq;
    private float timer;
    private Thread acceptThread;
    private bool running;
    private int texWidth, texHeight;

    public int ClientCount
    {
        get { lock (clients) return clients.Count; }
    }

    void Start()
    {
        var canvas = FindObjectOfType<DrawingCanvas>();
        texWidth = canvas.textureWidth;
        texHeight = canvas.textureHeight;
        tileDiffer = new TileDiffer(canvas.CanvasTexture);

        listener = new TcpListener(IPAddress.Any, port);
        listener.Start();
        running = true;
        acceptThread = new Thread(AcceptLoop) { IsBackground = true };
        acceptThread.Start();
    }

    void Update()
    {
        tileDiffer.Update();

        timer += Time.deltaTime;
        float interval = 1f / targetFps;
        if (timer < interval) return;
        timer -= interval;

        if (!tileDiffer.TryGetDirtyTiles(out var dirtyTiles)) return;
        if (dirtyTiles.Count == 0) return;

        byte[] packet;
        if (seq % keyFrameInterval == 0)
        {
            packet = FrameCodec.EncodeKeyFrame(texWidth, texHeight, tileDiffer.LatestRawData);
        }
        else
        {
            packet = FrameCodec.EncodeDeltaFrame(dirtyTiles);
        }

        SendToAll(packet);
        seq++;
    }

    void AcceptLoop()
    {
        while (running)
        {
            try
            {
                var client = listener.AcceptTcpClient();
                lock (clients)
                {
                    clients.Add(client);
                }

                var lastData = tileDiffer.LatestRawData;
                if (lastData != null)
                {
                    byte[] keyFrame = FrameCodec.EncodeKeyFrame(texWidth, texHeight, lastData);
                    SendToOne(client, keyFrame);
                }
            }
            catch (SocketException)
            {
                break;
            }
            catch (ObjectDisposedException)
            {
                break;
            }
        }
    }

    void SendToAll(byte[] data)
    {
        byte[] prefixed = new byte[4 + data.Length];
        BitConverter.GetBytes(data.Length).CopyTo(prefixed, 0);
        Buffer.BlockCopy(data, 0, prefixed, 4, data.Length);

        lock (clients)
        {
            for (int i = clients.Count - 1; i >= 0; i--)
            {
                try
                {
                    clients[i].GetStream().Write(prefixed, 0, prefixed.Length);
                }
                catch
                {
                    clients[i].Close();
                    clients.RemoveAt(i);
                }
            }
        }
    }

    void SendToOne(TcpClient client, byte[] data)
    {
        try
        {
            byte[] prefixed = new byte[4 + data.Length];
            BitConverter.GetBytes(data.Length).CopyTo(prefixed, 0);
            Buffer.BlockCopy(data, 0, prefixed, 4, data.Length);
            client.GetStream().Write(prefixed, 0, prefixed.Length);
        }
        catch { }
    }

    void OnDestroy()
    {
        running = false;
        listener?.Stop();

        lock (clients)
        {
            foreach (var c in clients) c.Close();
            clients.Clear();
        }

        acceptThread?.Join(1000);
    }
}
