using System.Collections.Generic;
using UnityEngine;

public class RoomBrowserUI : MonoBehaviour
{
    [SerializeField] private StreamClient _streamClient;

    private List<RoomInfo> _rooms = new List<RoomInfo>();
    private bool _showRoomList = true;
    private GUIStyle _styleBtn, _styleTitle, _styleStatus;

    void Start()
    {
        if (_streamClient == null) return;

        _streamClient.OnRoomListReceived += OnRoomList;
        _streamClient.OnRoomJoined += () => _showRoomList = false;
        _streamClient.OnRoomLeft += () => _showRoomList = true;

        _streamClient.Connect();
    }

    void OnRoomList(List<RoomInfo> rooms)
    {
        _rooms = rooms;
    }

    void OnGUI()
    {
        if (_styleBtn == null)
        {
            _styleBtn = new GUIStyle(GUI.skin.button) { fontSize = 20 };
            _styleTitle = new GUIStyle { fontSize = 24, fontStyle = FontStyle.Bold, normal = { textColor = Color.white } };
            _styleStatus = new GUIStyle { fontSize = 18, normal = { textColor = Color.gray } };
        }

        if (_showRoomList)
            DrawRoomList();
        else
            DrawInRoomUI();
    }

    void DrawRoomList()
    {
        GUILayout.BeginArea(new Rect(20, 20, 400, 600));
        GUILayout.Label("房间列表", _styleTitle);
        GUILayout.Space(10);

        if (!_streamClient.IsConnected)
        {
            GUILayout.Label("正在连接服务器...", _styleStatus);
        }
        else if (_rooms.Count == 0)
        {
            GUILayout.Label("暂无可用房间", _styleStatus);
        }
        else
        {
            foreach (RoomInfo r in _rooms)
            {
                if (GUILayout.Button($"{r.name}  ({r.userCount}人)", _styleBtn, GUILayout.Height(40)))
                    _streamClient.JoinRoom(r.roomId);
            }
        }

        GUILayout.EndArea();
    }

    void DrawInRoomUI()
    {
        GUILayout.BeginArea(new Rect(20, 20, 400, 200));
        GUILayout.Label($"房间已加入 (R{_streamClient.MyRoomId})", _styleTitle);
        GUILayout.Space(10);
        GUILayout.Label("在下方画布上绘制即可同步", _styleStatus);
        GUILayout.Space(20);

        if (GUILayout.Button("离开房间", _styleBtn, GUILayout.Height(40)))
            _streamClient.LeaveRoom();

        GUILayout.EndArea();
    }
}
