using UnityEngine;
using PicoBridge.Camera;
using PicoBridge.Immersive;
using PicoBridge.Network;
using PicoBridge.Tracking;
using Unity.XR.PXR;
using System.Collections;
using UnityEngine.XR.Interaction.Toolkit;

namespace PicoBridge
{
    /// <summary>
    /// Main entry point. Manages TCP connection, UDP discovery, and tracking data flow.
    /// Attach to a GameObject in the scene.
    /// </summary>
    public class PicoBridgeManager : MonoBehaviour
    {
        [Header("Server")]
        public string serverAddress = "192.168.1.100";
        public int serverPort = 63901;
        public bool autoDiscovery = true;

        [Header("Tracking")]
        public bool sendHead = true;
        public bool sendControllers = true;
        public bool sendHands = true;
        public bool sendBody = false;
        public bool sendMotion = false;

        [Header("Arm Source (mocap map t07)")]
        // Upper-body source mutex: Trackers (HMD + 2 motion trackers, the
        // mocap-map default) or Body (PICO body tracking; needs gloves off +
        // controllers held). At most one of sendBody/sendMotion is ever on.
        [SerializeField] private ArmStreamMode armStream = ArmStreamMode.Trackers;
        // Operator height in meters -> body-tracking bone lengths (ratio table).
        [Range(1.0f, 2.2f)]
        [SerializeField] private float operatorHeight = 1.75f;

        public enum ArmStreamMode { Trackers, Body }

        public ArmStreamMode ArmStream => armStream;
        public float OperatorHeight => operatorHeight;

        [Header("Timing")]
        [Range(30, 120)]
        public int trackingFps = 72;

        [Header("PC Control")]
        [SerializeField] private bool allowPcVideoPreview;
        [SerializeField] private bool autoRequestPcVideoPreview;

        [Header("Scene Visuals")]
        public bool hideTrackingVisualsWithoutSignal = true;

        private PicoTcpClient _tcp;
        private UdpDiscovery _discovery;
#if !UNITY_EDITOR
        private PicoTrackingCollector _collector;
#endif
        private WebRtcCameraReceiver _webRtcCamera;
        private WebRtcHttpSignalingClient _teleimagerStream;
        private StereoImmersiveBootstrap _stereoImmersive;
        private float _trackingInterval;
        private float _trackingTimer;
        private bool _autoConnected;
#if UNITY_ANDROID && !UNITY_EDITOR
        private Coroutine _videoSeeThroughCoroutine;
#endif

        public PicoTcpClient TcpClient => _tcp;
        public UdpDiscovery Discovery => _discovery;
        public WebRtcCameraReceiver WebRtcCamera => _webRtcCamera;
        public WebRtcHttpSignalingClient TeleimagerStream => _teleimagerStream;
        public bool IsConnected => _tcp != null && _tcp.State == SocketState.Working;
        public bool AllowPcVideoPreview => allowPcVideoPreview;
        public bool AutoRequestPcVideoPreview => autoRequestPcVideoPreview;

        private void Awake()
        {
            // Mount-correction config must load on the main thread
            // (persistentDataPath) before any BridgeControl or collector
            // touch can race it from the TCP receive thread.
            Tracking.BodyMountCorrection.EnsureLoaded();

            _tcp = gameObject.AddComponent<PicoTcpClient>();
            _tcp.serverAddress = serverAddress;
            _tcp.serverPort = serverPort;
            _tcp.DeviceSN = SystemInfo.deviceUniqueIdentifier;

            _tcp.OnConnected += () => Debug.Log("[PicoBridge] Connected");
            _tcp.OnDisconnected += () =>
            {
                Debug.Log("[PicoBridge] Disconnected");
                _autoConnected = false;
            };
            _tcp.OnFunctionReceived += OnFunction;

            // UDP discovery
            _discovery = gameObject.AddComponent<UdpDiscovery>();
            _discovery.OnServerFound += OnServerDiscovered;

            // Camera preview
            _webRtcCamera = gameObject.AddComponent<WebRtcCameraReceiver>();

            // Direct teleimager stream (HTTP signaling; URL defaults live on
            // the client so runtime AddComponent picks up code defaults).
            _teleimagerStream = gameObject.AddComponent<WebRtcHttpSignalingClient>();

            // Stereo immersive FPV rig (self-assembling, hidden until entered)
            _stereoImmersive = new GameObject("StereoImmersiveRig").AddComponent<StereoImmersiveBootstrap>();

#if !UNITY_EDITOR
            _collector = new PicoTrackingCollector();
#endif
            _trackingInterval = 1f / trackingFps;
        }

        private void Start()
        {
            ConfigurePassthroughRendering();
            ConfigureTrackingVisualGuards();
#if UNITY_EDITOR
            SuppressEditorOnlyControllerRenderers();
#else
            // Early tracker-event subscription: trackers already powered on
            // when the app starts must bind without a power-cycle (the sendMotion
            // gate below would otherwise leave the subscription too late).
            MotionTrackerBinding.EnsureSubscribed();
            // In-headset tracker gizmos (t07 addendum): FOV feedback + mount
            // orientation reference for offset recalibration (pico_tracker_local axes).
            MotionTrackerVisualizer.EnsureCreated(transform);
            // Single tracker-pose acquisition authority (tracker-ik map t01):
            // polls both sides into TrackerFrameCache for the wire, gizmos,
            // and later calibration/IK; also runs the session-status tick
            // (auto OS-mode guidance once per session).
            TrackerPosePoller.EnsureCreated(transform);
            // SDK body avatar driven by the corrected output (bodytrack-deploy
            // t09): the operator calibrates against the familiar white cubes.
            BodyTrackingBlockDriver.EnsureCreated();
#endif
            // Apply the configured arm-source mode up front (t07): scene
            // default Trackers keeps both streams off until the receiver or
            // the panel asks (cb46907 regression contract); Body starts
            // device body tracking immediately.
            ApplyArmStream();
            StartVideoSeeThroughBootstrap();

            if (autoDiscovery)
                _discovery.StartListening();

            // Don't auto-connect to hardcoded IP if discovery is on
            if (!autoDiscovery)
                _tcp.Connect();
        }

        private void OnEnable()
        {
#if UNITY_ANDROID && !UNITY_EDITOR
            PXR_Plugin.System.SessionStateChanged += OnSessionStateChanged;
#endif
        }

        private void OnDisable()
        {
#if UNITY_ANDROID && !UNITY_EDITOR
            PXR_Plugin.System.SessionStateChanged -= OnSessionStateChanged;
#endif
        }

        private void OnApplicationPause(bool pauseStatus)
        {
            if (!pauseStatus)
                StartVideoSeeThroughBootstrap();
        }

        private void Update()
        {
#if UNITY_EDITOR
            SuppressEditorOnlyControllerRenderers();
#endif

            // Rate-limited tracking send
            _trackingTimer += Time.deltaTime;
            if (_trackingTimer >= _trackingInterval && IsConnected)
            {
                _trackingTimer = 0;
                string json;
                #if UNITY_EDITOR
                json = MockTrackingData.GenerateJson(Time.time);
                #else
                if (_collector == null) return;
                _collector.HeadEnabled = sendHead;
                _collector.ControllerEnabled = sendControllers;
                _collector.HandTrackingEnabled = sendHands;
                _collector.BodyTrackingEnabled = sendBody;
                _collector.MotionTrackerEnabled = sendMotion;
                json = _collector.CollectJson();
                #endif
                _tcp.EnqueueTracking(json);
            }
        }

        private void ConfigureTrackingVisualGuards()
        {
            if (!hideTrackingVisualsWithoutSignal)
                return;

            foreach (var bodyBlock in FindObjectsOfType<global::PXR_BodyTrackingBlock>(true))
            {
                var target = bodyBlock.skeletonJoints != null ? bodyBlock.skeletonJoints.gameObject : bodyBlock.gameObject;
                AddTrackingVisualGuard(target, TrackingVisualSignalSource.Body);
            }

#if !PICO_OPENXR_SDK
            foreach (var hand in FindObjectsOfType<global::PXR_Hand>(true))
            {
                var source = hand.handType == HandType.HandLeft
                    ? TrackingVisualSignalSource.LeftHand
                    : TrackingVisualSignalSource.RightHand;
                AddTrackingVisualGuard(hand.gameObject, source);
            }
#endif

            foreach (var controller in FindObjectsOfType<ActionBasedController>(true))
            {
                var objectName = controller.gameObject.name.ToLowerInvariant();
                if (objectName.Contains("left"))
                    AddTrackingVisualGuard(controller.gameObject, TrackingVisualSignalSource.LeftController);
                else if (objectName.Contains("right"))
                    AddTrackingVisualGuard(controller.gameObject, TrackingVisualSignalSource.RightController);
            }
        }

        private static void AddTrackingVisualGuard(GameObject target, TrackingVisualSignalSource source)
        {
            if (target == null)
                return;

            var guard = target.GetComponent<TrackingVisualSignalGate>();
            if (guard == null)
                guard = target.AddComponent<TrackingVisualSignalGate>();

            guard.Configure(source);
        }

#if UNITY_EDITOR
        private void SuppressEditorOnlyControllerRenderers()
        {
            if (!hideTrackingVisualsWithoutSignal)
                return;

            foreach (var controller in FindObjectsOfType<ActionBasedController>(true))
            {
                if (controller == null)
                    continue;

                foreach (var rendererComponent in controller.GetComponentsInChildren<Renderer>(true))
                {
                    if (rendererComponent != null)
                        rendererComponent.enabled = false;
                }
            }
        }
#endif

        private void OnServerDiscovered(string ip, int port)
        {
            Debug.Log($"[PicoBridge] Server discovered: {ip}:{port}");
            // Auto-connect to first discovered server if not already connected
            if (!IsConnected && !_autoConnected)
            {
                _autoConnected = true;
                SetServer(ip, port);
            }
        }

        private void OnFunction(string functionName, string json)
        {
            Debug.Log($"[PicoBridge] Function: {functionName}");
            if (functionName == "BridgeControl")
            {
                HandleBridgeControl(json);
                return;
            }
            if (functionName == "WebRtcOffer" || functionName == "WebRtcIceCandidate")
                _webRtcCamera?.HandleFunction(functionName, json);
        }

        private void HandleBridgeControl(string json)
        {
            string channel = ExtractString(json, "channel");
            string type = ExtractString(json, "type");
            if (channel == "video" && type == "set_policy")
            {
                bool enabled = ExtractBool(json, "enabled") ?? false;
                bool autoPreview = ExtractBool(json, "auto_preview") ?? enabled;
                ApplyVideoPolicy(enabled, autoPreview);
                return;
            }

            // Motion tracker streaming toggle (mocap map t03): panel button is
            // deferred, so the PC side flips it over the existing
            // BridgeControl channel. Default stays off (sendBody style).
            // t07: enabling motion also leaves Body mode (arm-source mutex).
            if (channel == "tracking" && type == "set_motion")
            {
                sendMotion = ExtractBool(json, "enabled") ?? false;
                if (sendMotion)
                    SetArmStream(ArmStreamMode.Trackers, keepMotionEnabled: true);
                else
                    ApplyArmStream();
                Debug.Log($"[PicoBridge] BridgeControl: sendMotion={sendMotion}");
                return;
            }

            // Body tracking toggle (mocap map t07): receiver asks for PICO
            // body tracking instead of tracker synthesis (gloves off +
            // controllers held). Starts/stops body tracking on the device and
            // is mutually exclusive with motion streaming. Optional "height"
            // (meters) overrides the operator height for bone lengths.
            if (channel == "tracking" && type == "set_body")
            {
                bool enabled = ExtractBool(json, "enabled") ?? false;
                float? height = ExtractFloat(json, "height");
                if (height.HasValue)
                    operatorHeight = Mathf.Clamp(height.Value, 1.0f, 2.2f);
                if (enabled)
                {
                    SetArmStream(ArmStreamMode.Body);
                }
                else
                {
                    sendBody = false;
                    BodyTrackingRuntime.EnsureStopped();
                    armStream = ArmStreamMode.Trackers;
                    ApplyArmStream();
                }
                Debug.Log($"[PicoBridge] BridgeControl: set_body={enabled} height={operatorHeight:0.00}");
            }

            // Mount-correction params (bodytrack-deploy t07): per-side
            // yaw/level degrees applied post-AppendBody on the Wrist/Hand
            // joints. Remote push persists on-device as the boot default
            // (in-headset knobs, t08, write the same store). Sides absent
            // from the payload keep their stored values.
            if (channel == "tracking" && type == "set_mount_correction")
            {
                bool correctionEnabled = ExtractBool(json, "enabled") ?? true;
                Tracking.BodyMountCorrection.SetEnabled(correctionEnabled);
                ApplyMountCorrectionSide(json, "left");
                ApplyMountCorrectionSide(json, "right");
                Debug.Log($"[PicoBridge] BridgeControl: set_mount_correction enabled={correctionEnabled} " +
                          $"L={DescribeMountCorrectionSide("left")} R={DescribeMountCorrectionSide("right")}");
            }
        }

        private static void ApplyMountCorrectionSide(string json, string side)
        {
            string sideJson = ExtractObject(json, side);
            if (sideJson.Length == 0)
                return;
            float? yaw = ExtractFloat(sideJson, "yaw");
            float? level = ExtractFloat(sideJson, "level");
            if (!yaw.HasValue && !level.HasValue)
                return;
            Tracking.BodyMountCorrection.SetSide(side, yaw ?? 0f, level ?? 0f);
        }

        private static string DescribeMountCorrectionSide(string side)
        {
            var entry = Tracking.BodyMountCorrection.GetSide(side);
            return entry == null ? "--" : $"yaw {entry.yaw:0.#} level {entry.level:0.#}";
        }

        /// <summary>
        /// Switch the upper-body source (panel pills or BridgeControl). Mutex:
        /// Body turns motion streaming off; Trackers turns body tracking off
        /// (motion stays receiver-gated unless asked on directly).
        /// </summary>
        public void SetArmStream(ArmStreamMode mode, bool keepMotionEnabled = false)
        {
            armStream = mode;
            if (keepMotionEnabled)
                sendMotion = sendMotion || mode == ArmStreamMode.Trackers;
            ApplyArmStream();
        }

        public void RequestTrackersMode()
        {
            sendMotion = true;
            SetArmStream(ArmStreamMode.Trackers, keepMotionEnabled: true);
        }

        public void RequestBodyMode()
        {
            SetArmStream(ArmStreamMode.Body);
        }

        private void ApplyArmStream()
        {
            switch (armStream)
            {
                case ArmStreamMode.Trackers:
                    sendBody = false;
                    BodyTrackingRuntime.EnsureStopped();
                    break;
                case ArmStreamMode.Body:
                    sendMotion = false;
                    sendBody = true;
                    BodyTrackingRuntime.EnsureStarted(operatorHeight);
                    break;
            }
        }

        private void ApplyVideoPolicy(bool enabled, bool autoPreview)
        {
            allowPcVideoPreview = enabled;
            autoRequestPcVideoPreview = enabled && autoPreview;
            if (!allowPcVideoPreview || !autoRequestPcVideoPreview)
                _webRtcCamera?.StopPreview();
        }

        private static string ExtractString(string json, string key)
        {
            string needle = $"\"{key}\"";
            int keyIndex = json.IndexOf(needle, System.StringComparison.Ordinal);
            if (keyIndex < 0) return string.Empty;
            int colon = json.IndexOf(':', keyIndex + needle.Length);
            if (colon < 0) return string.Empty;
            int start = json.IndexOf('"', colon + 1);
            if (start < 0) return string.Empty;
            var result = new System.Text.StringBuilder();
            bool escape = false;
            for (int i = start + 1; i < json.Length; i++)
            {
                char c = json[i];
                if (escape)
                {
                    switch (c)
                    {
                        case 'n': result.Append('\n'); break;
                        case 'r': result.Append('\r'); break;
                        case 't': result.Append('\t'); break;
                        case '\\': result.Append('\\'); break;
                        case '"': result.Append('"'); break;
                        default: result.Append(c); break;
                    }
                    escape = false;
                }
                else if (c == '\\')
                    escape = true;
                else if (c == '"')
                    return result.ToString();
                else
                    result.Append(c);
            }
            return string.Empty;
        }

        /// <summary>Inner JSON of a nested object value ("" when absent), so the
        /// flat ExtractX helpers can run scoped inside payload sub-objects.</summary>
        private static string ExtractObject(string json, string key)
        {
            string needle = $"\"{key}\"";
            int keyIndex = json.IndexOf(needle, System.StringComparison.Ordinal);
            if (keyIndex < 0) return string.Empty;
            int open = json.IndexOf('{', keyIndex + needle.Length);
            if (open < 0) return string.Empty;
            int depth = 0;
            for (int i = open; i < json.Length; i++)
            {
                switch (json[i])
                {
                    case '{': depth++; break;
                    case '}':
                        depth--;
                        if (depth == 0)
                            return json.Substring(open + 1, i - open - 1);
                        break;
                }
            }
            return string.Empty;
        }

        private static bool? ExtractBool(string json, string key)
        {
            string needle = $"\"{key}\"";
            int keyIndex = json.IndexOf(needle, System.StringComparison.Ordinal);
            if (keyIndex < 0) return null;
            int colon = json.IndexOf(':', keyIndex + needle.Length);
            if (colon < 0) return null;
            int start = colon + 1;
            while (start < json.Length && char.IsWhiteSpace(json[start])) start++;
            if (start + 4 <= json.Length && string.Compare(json, start, "true", 0, 4, System.StringComparison.Ordinal) == 0)
                return true;
            if (start + 5 <= json.Length && string.Compare(json, start, "false", 0, 5, System.StringComparison.Ordinal) == 0)
                return false;
            return null;
        }

        private static float? ExtractFloat(string json, string key)
        {
            string needle = $"\"{key}\"";
            int keyIndex = json.IndexOf(needle, System.StringComparison.Ordinal);
            if (keyIndex < 0) return null;
            int colon = json.IndexOf(':', keyIndex + needle.Length);
            if (colon < 0) return null;
            int start = colon + 1;
            while (start < json.Length && char.IsWhiteSpace(json[start])) start++;
            int end = start;
            while (end < json.Length && (char.IsDigit(json[end]) || json[end] == '.' || json[end] == '-' || json[end] == '+' || json[end] == 'e' || json[end] == 'E'))
                end++;
            if (end == start)
                return null;
            return float.TryParse(json.Substring(start, end - start), System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out var value) ? value : (float?)null;
        }

        /// <summary>
        /// Change server address at runtime (e.g. from UI input or discovery).
        /// </summary>
        public void SetServer(string address, int port = NetCMD.DEFAULT_TCP_PORT)
        {
            serverAddress = address;
            serverPort = port;
            if (_tcp != null)
            {
                _tcp.Disconnect();
                _tcp.serverAddress = address;
                _tcp.serverPort = port;
                _tcp.autoReconnect = true;
                _tcp.Connect();
            }
        }

        private static void EnableVideoSeeThrough()
        {
#if UNITY_ANDROID && !UNITY_EDITOR
            PXR_Manager.EnableVideoSeeThrough = true;
#endif
        }

        private static void ConfigurePassthroughRendering()
        {
#if UNITY_ANDROID && !UNITY_EDITOR
            PXR_Plugin.Render.UPxr_EnablePremultipliedAlpha(true);
#endif
        }

        private void StartVideoSeeThroughBootstrap()
        {
#if UNITY_ANDROID && !UNITY_EDITOR
            if (_videoSeeThroughCoroutine != null)
                StopCoroutine(_videoSeeThroughCoroutine);

            _videoSeeThroughCoroutine = StartCoroutine(EnableVideoSeeThroughWithRetry());
#endif
        }

        private IEnumerator EnableVideoSeeThroughWithRetry()
        {
            const int maxAttempts = 12;
            const float retryDelay = 0.5f;

            for (int attempt = 0; attempt < maxAttempts; attempt++)
            {
                ConfigurePassthroughRendering();
                EnableVideoSeeThrough();
                yield return new WaitForSeconds(retryDelay);
            }

#if UNITY_ANDROID && !UNITY_EDITOR
            _videoSeeThroughCoroutine = null;
#endif
        }

        private void OnSessionStateChanged(XrSessionState state)
        {
#if UNITY_ANDROID && !UNITY_EDITOR
            if (state == XrSessionState.Ready ||
                state == XrSessionState.Synchronized ||
                state == XrSessionState.Visible ||
                state == XrSessionState.Focused)
            {
                StartVideoSeeThroughBootstrap();
            }
#endif
        }
    }
}
