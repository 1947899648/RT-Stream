using UnityEngine;
using UnityEngine.Events;

namespace WPZ0325.RTStream
{
    [System.Serializable]
    public class DirtyTilesUnityEvent : UnityEvent<string, int[]> { }

    [RequireComponent(typeof(MonoRTStreamSender))]
    public class MonoRTStreamSenderUnityEvent : MonoBehaviour
    {
        [SerializeField] private bool _debugLog;

        public UnityEvent OnHostStarted = new UnityEvent();
        public UnityEvent OnHostStopped = new UnityEvent();
        public UnityEvent<string, int, int> OnRenderTextureRegistered = new UnityEvent<string, int, int>();
        public UnityEvent<string> OnRenderTextureUnregistered = new UnityEvent<string>();
        public UnityEvent<string> OnRenderTextureSyncStarted = new UnityEvent<string>();
        public UnityEvent<string> OnRenderTextureSyncPaused = new UnityEvent<string>();
        public UnityEvent<string> OnRenderTextureKeyFrameSent = new UnityEvent<string>();
        public DirtyTilesUnityEvent OnRenderTextureDirtyTilesSent = new DirtyTilesUnityEvent();
        public UnityEvent<int> OnClientConnected = new UnityEvent<int>();
        public UnityEvent<int> OnClientDisconnected = new UnityEvent<int>();

        private MonoRTStreamSender _target;

        void Awake()
        {
            _target = GetComponent<MonoRTStreamSender>();

            _target.OnHostStarted += HandleHostStarted;
            _target.OnHostStopped += HandleHostStopped;
            _target.OnRenderTextureRegistered += HandleRenderTextureRegistered;
            _target.OnRenderTextureUnregistered += HandleRenderTextureUnregistered;
            _target.OnRenderTextureSyncStarted += HandleRenderTextureSyncStarted;
            _target.OnRenderTextureSyncPaused += HandleRenderTextureSyncPaused;
            _target.OnRenderTextureKeyFrameSent += HandleRenderTextureKeyFrameSent;
            _target.OnRenderTextureDirtyTilesSent += HandleRenderTextureDirtyTilesSent;
            _target.OnClientConnected += HandleClientConnected;
            _target.OnClientDisconnected += HandleClientDisconnected;
        }

        void OnDestroy()
        {
            if (_target == null) return;

            _target.OnHostStarted -= HandleHostStarted;
            _target.OnHostStopped -= HandleHostStopped;
            _target.OnRenderTextureRegistered -= HandleRenderTextureRegistered;
            _target.OnRenderTextureUnregistered -= HandleRenderTextureUnregistered;
            _target.OnRenderTextureSyncStarted -= HandleRenderTextureSyncStarted;
            _target.OnRenderTextureSyncPaused -= HandleRenderTextureSyncPaused;
            _target.OnRenderTextureKeyFrameSent -= HandleRenderTextureKeyFrameSent;
            _target.OnRenderTextureDirtyTilesSent -= HandleRenderTextureDirtyTilesSent;
            _target.OnClientConnected -= HandleClientConnected;
            _target.OnClientDisconnected -= HandleClientDisconnected;
        }

        void HandleHostStarted()
        {
#if UNITY_EDITOR
            if (_debugLog) Debug.Log("[Sender] HostStarted");
#endif
            OnHostStarted.Invoke();
        }

        void HandleHostStopped()
        {
#if UNITY_EDITOR
            if (_debugLog) Debug.Log("[Sender] HostStopped");
#endif
            OnHostStopped.Invoke();
        }

        void HandleRenderTextureRegistered(string id, int w, int h)
        {
#if UNITY_EDITOR
            if (_debugLog) Debug.Log($"[Sender] RenderTextureRegistered: {id} ({w}x{h})");
#endif
            OnRenderTextureRegistered.Invoke(id, w, h);
        }

        void HandleRenderTextureUnregistered(string id)
        {
#if UNITY_EDITOR
            if (_debugLog) Debug.Log($"[Sender] RenderTextureUnregistered: {id}");
#endif
            OnRenderTextureUnregistered.Invoke(id);
        }

        void HandleRenderTextureSyncStarted(string id)
        {
#if UNITY_EDITOR
            if (_debugLog) Debug.Log($"[Sender] RenderTextureSyncStarted: {id}");
#endif
            OnRenderTextureSyncStarted.Invoke(id);
        }

        void HandleRenderTextureSyncPaused(string id)
        {
#if UNITY_EDITOR
            if (_debugLog) Debug.Log($"[Sender] RenderTextureSyncPaused: {id}");
#endif
            OnRenderTextureSyncPaused.Invoke(id);
        }

        void HandleRenderTextureKeyFrameSent(string id)
        {
#if UNITY_EDITOR
            if (_debugLog) Debug.Log($"[Sender] RenderTextureKeyFrameSent: {id}");
#endif
            OnRenderTextureKeyFrameSent.Invoke(id);
        }

        void HandleRenderTextureDirtyTilesSent(string id, int[] indices)
        {
#if UNITY_EDITOR
            if (_debugLog) Debug.Log($"[Sender] RenderTextureDirtyTilesSent: {id} tiles={indices.Length}");
#endif
            OnRenderTextureDirtyTilesSent.Invoke(id, indices);
        }

        void HandleClientConnected(int c)
        {
#if UNITY_EDITOR
            if (_debugLog) Debug.Log($"[Sender] ClientConnected: {c}");
#endif
            OnClientConnected.Invoke(c);
        }

        void HandleClientDisconnected(int c)
        {
#if UNITY_EDITOR
            if (_debugLog) Debug.Log($"[Sender] ClientDisconnected: {c}");
#endif
            OnClientDisconnected.Invoke(c);
        }
    }
}
