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
        public UnityEvent<string, int, int> OnRenderTextureAnnounced = new UnityEvent<string, int, int>();

        public DirtyTilesUnityEvent OnRenderTextureDirtyTilesReceived = new DirtyTilesUnityEvent();

        private MonoRTStreamReceiver _target;

        void Awake()
        {
            _target = GetComponent<MonoRTStreamReceiver>();

            _target.OnConnectedToHost += HandleConnectedToHost;
            _target.OnDisconnectedFromHost += HandleDisconnectedFromHost;
            _target.OnConnectionFailed += HandleConnectionFailed;
            _target.OnRenderTextureAnnounced += HandleRenderTextureAnnounced;
            _target.OnRenderTextureDirtyTilesReceived += HandleRenderTextureDirtyTilesReceived;
        }

        void OnDestroy()
        {
            if (_target == null) return;

            _target.OnConnectedToHost -= HandleConnectedToHost;
            _target.OnDisconnectedFromHost -= HandleDisconnectedFromHost;
            _target.OnConnectionFailed -= HandleConnectionFailed;
            _target.OnRenderTextureAnnounced -= HandleRenderTextureAnnounced;
            _target.OnRenderTextureDirtyTilesReceived -= HandleRenderTextureDirtyTilesReceived;
        }

        void HandleConnectedToHost()
        {
#if UNITY_EDITOR
            if (_debugLog) Debug.Log("[Receiver] ConnectedToHost");
#endif
            OnConnectedToHost.Invoke();
        }

        void HandleDisconnectedFromHost()
        {
#if UNITY_EDITOR
            if (_debugLog) Debug.Log("[Receiver] DisconnectedFromHost");
#endif
            OnDisconnectedFromHost.Invoke();
        }

        void HandleConnectionFailed(string msg)
        {
#if UNITY_EDITOR
            if (_debugLog) Debug.Log($"[Receiver] ConnectionFailed: {msg}");
#endif
            OnConnectionFailed.Invoke(msg);
        }

        void HandleRenderTextureAnnounced(string id, int w, int h)
        {
#if UNITY_EDITOR
            if (_debugLog) Debug.Log($"[Receiver] RenderTextureAnnounced: {id} ({w}x{h})");
#endif
            OnRenderTextureAnnounced.Invoke(id, w, h);
        }

        void HandleRenderTextureDirtyTilesReceived(string id, int[] indices)
        {
#if UNITY_EDITOR
            if (_debugLog) Debug.Log($"[Receiver] RenderTextureDirtyTilesReceived: {id} tiles={indices.Length}");
#endif
            OnRenderTextureDirtyTilesReceived.Invoke(id, indices);
        }
    }
}
