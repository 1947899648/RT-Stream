using UnityEngine;
using UnityEngine.Events;

namespace WPZ0325.RTStream
{
    [RequireComponent(typeof(MonoRTStreamReceiver))]
    public class MonoRTStreamReceiverUnityEvent : MonoBehaviour
    {
        [SerializeField] private bool _debugLog;

        public UnityEvent OnConnectedToHost = new UnityEvent();
        public UnityEvent OnDisconnectedFromHost = new UnityEvent();
        public UnityEvent<string> OnConnectionFailed = new UnityEvent<string>();
        public UnityEvent<string, int, int> OnRenderTextureSubscribed = new UnityEvent<string, int, int>();
        public UnityEvent<string> OnRenderTextureUnsubscribed = new UnityEvent<string>();
        public DirtyTilesUnityEvent OnRenderTextureDirtyTilesReceived = new DirtyTilesUnityEvent();

        private MonoRTStreamReceiver _target;

        void Awake()
        {
            _target = GetComponent<MonoRTStreamReceiver>();

            _target.OnConnectedToHost += HandleConnectedToHost;
            _target.OnDisconnectedFromHost += HandleDisconnectedFromHost;
            _target.OnConnectionFailed += HandleConnectionFailed;
            _target.OnRenderTextureSubscribed += HandleRenderTextureSubscribed;
            _target.OnRenderTextureUnsubscribed += HandleRenderTextureUnsubscribed;
            _target.OnRenderTextureDirtyTilesReceived += HandleRenderTextureDirtyTilesReceived;
        }

        void OnDestroy()
        {
            if (_target == null) return;

            _target.OnConnectedToHost -= HandleConnectedToHost;
            _target.OnDisconnectedFromHost -= HandleDisconnectedFromHost;
            _target.OnConnectionFailed -= HandleConnectionFailed;
            _target.OnRenderTextureSubscribed -= HandleRenderTextureSubscribed;
            _target.OnRenderTextureUnsubscribed -= HandleRenderTextureUnsubscribed;
            _target.OnRenderTextureDirtyTilesReceived -= HandleRenderTextureDirtyTilesReceived;
        }

        void HandleConnectedToHost() { if (_debugLog) Debug.Log("[Receiver] ConnectedToHost"); OnConnectedToHost.Invoke(); }
        void HandleDisconnectedFromHost() { if (_debugLog) Debug.Log("[Receiver] DisconnectedFromHost"); OnDisconnectedFromHost.Invoke(); }
        void HandleConnectionFailed(string msg) { if (_debugLog) Debug.Log($"[Receiver] ConnectionFailed: {msg}"); OnConnectionFailed.Invoke(msg); }
        void HandleRenderTextureSubscribed(string id, int w, int h) { if (_debugLog) Debug.Log($"[Receiver] RenderTextureSubscribed: {id} ({w}x{h})"); OnRenderTextureSubscribed.Invoke(id, w, h); }
        void HandleRenderTextureUnsubscribed(string id) { if (_debugLog) Debug.Log($"[Receiver] RenderTextureUnsubscribed: {id}"); OnRenderTextureUnsubscribed.Invoke(id); }
        void HandleRenderTextureDirtyTilesReceived(string id, int[] indices) { if (_debugLog) Debug.Log($"[Receiver] RenderTextureDirtyTilesReceived: {id} tiles={indices.Length}"); OnRenderTextureDirtyTilesReceived.Invoke(id, indices); }
    }
}
