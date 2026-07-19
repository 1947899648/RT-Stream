using System;
using System.Collections.Generic;
using UnityEngine;

public class RoomHost : MonoBehaviour
{
    [SerializeField] private DrawableSurface _surface3D;
    [SerializeField] private DrawableSurface _surfaceUI;
    [SerializeField] private ComputeShader _tileDiffShader;
    [SerializeField] private string _roomName = "Room";

    private Telepathy.Client _client;
    private int _myClientId;
    private int _myRoomId;
    private bool _roomCreated;

    private RenderTexture _rt3D, _rtUI;
    private GpuTileDiffer _differ3D, _differUI;
    private bool _needKey3D = true, _needKeyUI = true;

    void Start()
    {
        int size = SceneConfig.TextureSize;

        _rt3D = new RenderTexture(size, size, 0, RenderTextureFormat.ARGB32);
        _rtUI = new RenderTexture(size, size, 0, RenderTextureFormat.ARGB32);

        _surface3D.Initialize(_rt3D);
        _surfaceUI.Initialize(_rtUI);

        _differ3D = new GpuTileDiffer(_rt3D, _tileDiffShader);
        _differUI = new GpuTileDiffer(_rtUI, _tileDiffShader);

        _client = new Telepathy.Client(256 * 1024);
        _client.OnConnected = () =>
            _client.Send(new ArraySegment<byte>(Protocol.ClientHelloMsg(Protocol.RoleHC)));
        _client.OnData = OnData;
        _client.Connect(SceneConfig.ServerIP, SceneConfig.Port);
    }

    void OnData(ArraySegment<byte> data)
    {
        byte type = Protocol.GetMessageType(data);
        switch (type)
        {
            case Protocol.ClientId:
                _myClientId = BitConverter.ToInt32(data.Array, data.Offset + 1);
                _client.Send(new ArraySegment<byte>(Protocol.CreateRoomMsg(_roomName)));
                break;

            case Protocol.RoomCreated:
                _myRoomId = BitConverter.ToInt32(data.Array, data.Offset + 1);
                _roomCreated = true;
                _needKey3D = true;
                _needKeyUI = true;
                Debug.Log($"Room created: id={_myRoomId}");
                break;

            case Protocol.RoomUsersChanged:
                _needKey3D = true;
                _needKeyUI = true;
                break;

            case Protocol.DrawingCmd:
                Protocol.ReadDrawingCmd(data, out byte cid, out int uid,
                    out byte r, out byte g, out byte b, out byte a,
                    out float size, out float[] pts);
                DrawableSurface target = cid == 0 ? _surface3D : _surfaceUI;
                target.ApplyRemoteCommand(new Color32(r, g, b, a), size, pts);
                break;
        }
    }

    void Update()
    {
        _client?.Tick(100);
        if (!_roomCreated) return;

        CheckAndSend(_differ3D, 0, ref _needKey3D);
        CheckAndSend(_differUI, 1, ref _needKeyUI);
    }

    void CheckAndSend(GpuTileDiffer differ, byte canvasId, ref bool needKeyFrame)
    {
        differ.Update(needKeyFrame);
        if (needKeyFrame) needKeyFrame = false;

        if (!differ.TryGetResult(out List<DirtyTile> tiles, out byte[] fullFrame)) return;

        byte[] packet;
        byte msgType;

        if (fullFrame != null)
        {
            int size = SceneConfig.TextureSize;
            packet = FrameCodec.EncodeKeyFrame(size, size, fullFrame);
            msgType = Protocol.CanvasKey;
        }
        else if (tiles != null && tiles.Count > 0)
        {
            packet = FrameCodec.EncodeDeltaFrame(tiles);
            msgType = Protocol.CanvasDirty;
        }
        else
        {
            return;
        }

        _client.Send(new ArraySegment<byte>(Protocol.CanvasFrameMsg(msgType, canvasId, packet)));
    }

    void OnDestroy()
    {
        _client?.Disconnect();
        _differ3D?.Dispose();
        _differUI?.Dispose();
        _rt3D?.Release();
        _rtUI?.Release();
    }
}
