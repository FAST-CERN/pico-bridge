using System;
using UnityEngine;
#if UNITY_ANDROID && !UNITY_EDITOR
using UnityEngine.Android;
#endif

namespace PicoBridge.Audio
{
    /// <summary>Explicit audio consent and lifecycle; native workers perform PCM I/O.</summary>
    public sealed class PicoAudioController : MonoBehaviour
    {
        private PicoBridgeManager _manager;
        private string _attemptedHost;
        private bool _paused;
        private float _nextRefresh;
        public bool Enabled { get; private set; }
        public bool Muted { get; private set; }
        public string Status { get; private set; } = "Audio off";
#if UNITY_ANDROID && !UNITY_EDITOR
        private PermissionCallbacks _permissionCallbacks;
        private const string ServiceClass = "com.picobridge.audio.PicoAudioService";
#endif

        private void Awake() { _manager = GetComponent<PicoBridgeManager>(); }

        public void ToggleAudio()
        {
            if (Enabled) { Enabled = false; StopNative(); Status = "Audio off"; return; }
            Enabled = true;
#if UNITY_ANDROID && !UNITY_EDITOR
            if (!Permission.HasUserAuthorizedPermission(Permission.Microphone))
            {
                Status = "Allow microphone access";
                _permissionCallbacks = new PermissionCallbacks();
                _permissionCallbacks.PermissionGranted += _ => { if (Enabled) TryStart(); };
                _permissionCallbacks.PermissionDenied += _ => PermissionDenied();
                _permissionCallbacks.PermissionDeniedAndDontAskAgain += _ => PermissionDenied();
                Permission.RequestUserPermission(Permission.Microphone, _permissionCallbacks);
                return;
            }
#endif
            TryStart();
        }

        public void ToggleMute()
        {
            Muted = !Muted;
#if UNITY_ANDROID && !UNITY_EDITOR
            try { using (var service = new AndroidJavaClass(ServiceClass)) service.CallStatic("setMuted", Muted); }
            catch (Exception error) { Debug.LogWarning("[PicoAudio] " + error.Message); }
#endif
        }

        private void PermissionDenied() { Enabled = false; Status = "Microphone permission denied"; }

        private void Update()
        {
            if (Time.unscaledTime < _nextRefresh) return;
            _nextRefresh = Time.unscaledTime + 0.5f;
            if (!Enabled || _paused) return;
            if (_manager == null || !_manager.IsConnected)
            {
                if (_attemptedHost != null) StopNative();
                Status = "Audio waiting for robot";
                return;
            }
            if (_attemptedHost != _manager.serverAddress) TryStart();
#if UNITY_ANDROID && !UNITY_EDITOR
            if (_attemptedHost != null)
            {
                try { using (var service = new AndroidJavaClass(ServiceClass)) Status = service.CallStatic<string>("getStatus"); }
                catch (Exception error) { Status = "Audio error: " + error.Message; }
            }
#endif
        }

        private void TryStart()
        {
            if (!Enabled || _paused) return;
            if (_manager == null || !_manager.IsConnected) { Status = "Audio waiting for robot"; return; }
#if UNITY_ANDROID && !UNITY_EDITOR
            if (!Permission.HasUserAuthorizedPermission(Permission.Microphone)) return;
            // One attempt per connection/explicit toggle. Errors do not cause a restart storm.
            string host = _manager.serverAddress;
            if (_attemptedHost == host) return;
            if (_attemptedHost != null) StopNative();
            _attemptedHost = host;
            try
            {
                using (var unity = new AndroidJavaClass("com.unity3d.player.UnityPlayer"))
                using (var activity = unity.GetStatic<AndroidJavaObject>("currentActivity"))
                using (var service = new AndroidJavaClass(ServiceClass))
                {
                    service.CallStatic("setMuted", Muted);
                    service.CallStatic("start", activity, host);
                }
                Status = "Starting audio";
            }
            catch (Exception error) { Status = "Audio error: " + error.Message; Debug.LogWarning("[PicoAudio] " + error); }
#else
            Status = "Audio requires an Android headset";
#endif
        }

        private void StopNative()
        {
#if UNITY_ANDROID && !UNITY_EDITOR
            try
            {
                using (var unity = new AndroidJavaClass("com.unity3d.player.UnityPlayer"))
                using (var activity = unity.GetStatic<AndroidJavaObject>("currentActivity"))
                using (var service = new AndroidJavaClass(ServiceClass)) service.CallStatic("stop", activity);
            }
            catch (Exception error) { Debug.LogWarning("[PicoAudio] stop: " + error.Message); }
#endif
            _attemptedHost = null;
        }

        private void OnApplicationPause(bool paused)
        {
            _paused = paused;
            if (paused) { StopNative(); if (Enabled) Status = "Audio paused"; }
            else TryStart();
        }
        private void OnDestroy() { StopNative(); }
        private void OnApplicationQuit() { StopNative(); }
    }
}
