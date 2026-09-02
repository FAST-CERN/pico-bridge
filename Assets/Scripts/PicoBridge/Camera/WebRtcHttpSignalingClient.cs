using System;
using System.Collections;
using System.Collections.Generic;
using Unity.WebRTC;
using UnityEngine;
using UnityEngine.Networking;

namespace PicoBridge.Camera
{
    /// <summary>
    /// Direct teleimager video client: a WebRTC offerer signaled by one HTTPS
    /// POST to the teleimager /offer endpoint (vanilla ICE — candidates are
    /// embedded in the SDP, the server has no trickle endpoint). Mirrors
    /// WebRtcCameraReceiver's render/watchdog path but owns its signaling
    /// instead of consuming TCP function frames, so it stays alive while the
    /// panel (and the PC-push receiver it drives) is hidden in immersive mode.
    /// </summary>
    public class WebRtcHttpSignalingClient : MonoBehaviour
    {
        [Tooltip("teleimager /offer endpoint, e.g. https://<jetson-ip>:60001/offer. The server's self-signed certificate is accepted.")]
        [SerializeField] private string url = DefaultUrl;
        [Tooltip("Codec hint sent in the POST body (\"h264\" or \"vp8\"); null lets the server use its config.")]
        [SerializeField] private string codec = "h264";
        [Tooltip("Resolution hint sent in the POST body (\"720p\"/\"1080p\"); advisory until the server-side switch lands.")]
        [SerializeField] private string resolution = "1080p";

        private RTCPeerConnection _peer;
        private VideoStreamTrack _videoTrack;
        private Texture _texture;
        private Coroutine _updateCoroutine;
        private Coroutine _connectCoroutine;
        private Coroutine _resetCoroutine;
        private Coroutine _reconnectCoroutine;
        private Coroutine _statsCoroutine;
        private readonly List<string> _iceCandidates = new List<string>();
        private string _status = "Idle";
        private int _frameCount;
        private bool _ignorePeerStateChanges;
        private bool _wantStream;
        private float _startedAt = -1f;
        private float _disconnectedAt = -1f;
        private float _lastFrameAt = -1f;
        private float _lastFrameIntervalMs;
        private static bool _offerSdpLogged;
        private static bool _answerSdpLogged;

        public const string DefaultUrl = "https://192.168.5.5:60001/offer";

        /// <summary>PlayerPrefs key persisting the user-entered /offer endpoint
        /// (sbs-1080p map t06): the serialized default only seeds a fresh
        /// install, so robot network changes no longer need an APK rebuild.</summary>
        private const string UrlPrefKey = "pico_bridge.teleimager_url";
        private const string ResolutionPrefKey = "pico_bridge.teleimager_resolution";

        public Texture Texture => _texture;
        public string Status => _status;
        public int FrameCount => _frameCount;
        public float LastFrameIntervalMs => _lastFrameIntervalMs;
        public bool IsActive => _peer != null || _connectCoroutine != null;
        public bool HasVideoSignal => _texture != null && _frameCount > 0;
        public bool IsConfigured => !string.IsNullOrEmpty(url) && url.StartsWith("http", StringComparison.Ordinal);

        public string ServerUrl => url;
        public string StreamResolution => resolution;

        private void Awake()
        {
            url = PlayerPrefs.GetString(UrlPrefKey, url);
            resolution = PlayerPrefs.GetString(ResolutionPrefKey, resolution);
        }

        /// <summary>Apply and persist a new /offer endpoint. Returns false for
        /// values that do not look like an http(s) URL (state left unchanged).</summary>
        public bool SetServerUrl(string value)
        {
            value = (value ?? string.Empty).Trim();
            if (!value.StartsWith("http", StringComparison.Ordinal))
                return false;
            url = value;
            PlayerPrefs.SetString(UrlPrefKey, value);
            PlayerPrefs.Save();
            return true;
        }

        /// <summary>Apply and persist the resolution hint ("720p"/"1080p").
        /// Takes effect on the next StartStream.</summary>
        public bool SetStreamResolution(string value)
        {
            value = (value ?? string.Empty).Trim().ToLowerInvariant();
            if (value != "720p" && value != "1080p")
                return false;
            resolution = value;
            PlayerPrefs.SetString(ResolutionPrefKey, value);
            PlayerPrefs.Save();
            return true;
        }

        public bool ShouldRetry
        {
            get
            {
                float now = Time.realtimeSinceStartup;
                bool initialTimedOut = !HasVideoSignal && _startedAt > 0f && now - _startedAt >= InitialFrameTimeout;
                bool disconnectedTimedOut = _disconnectedAt > 0f && now - _disconnectedAt >= DisconnectedRetryTimeout;
                return initialTimedOut || disconnectedTimedOut;
            }
        }

        private const float InitialFrameTimeout = 10f;
        private const float DisconnectedRetryTimeout = 12f;
        private const float IceGatheringTimeout = 5f;
        private const float PostFailureRetryDelay = 2f;
        private const float SlowFrameIntervalMs = 120f;

        /// <summary>Override the inspector defaults (programmatic configuration).</summary>
        public void Configure(string streamUrl, string streamCodec = "h264")
        {
            url = streamUrl;
            if (!string.IsNullOrEmpty(streamCodec))
                codec = streamCodec;
        }

        /// <summary>Start (or restart) the direct stream. Safe to call repeatedly.</summary>
        public void StartStream()
        {
            if (!IsConfigured)
            {
                _status = "No teleimager URL";
                Debug.LogWarning("[HttpSignaling] Not configured; set url before StartStream");
                return;
            }

            _wantStream = true;
            BeginConnect();
        }

        public void StopStream()
        {
            _wantStream = false;
            if (_reconnectCoroutine != null)
            {
                StopCoroutine(_reconnectCoroutine);
                _reconnectCoroutine = null;
            }
            // An in-flight handshake coroutine must die with the peer: it
            // would otherwise resume after its POST and dereference the
            // disposed peer (NullReferenceException at SetRemoteDescription).
            if (_connectCoroutine != null)
            {
                StopCoroutine(_connectCoroutine);
                _connectCoroutine = null;
            }
            ResetPeer(clearSignal: true);
            _status = "Idle";
            _startedAt = -1f;
            _disconnectedAt = -1f;
            _lastFrameAt = -1f;
            _lastFrameIntervalMs = 0f;
        }

        private void Update()
        {
            // Self-supervised retry: the panel (and its polling) is hidden
            // while immersive mode is active, so nobody else drives retries.
            if (_wantStream && ShouldRetry)
            {
                Debug.LogWarning($"[HttpSignaling] retrying ({_status})");
                BeginConnect();
            }
        }

        private void BeginConnect()
        {
            if (_reconnectCoroutine != null)
            {
                StopCoroutine(_reconnectCoroutine);
                _reconnectCoroutine = null;
            }
            if (_connectCoroutine != null)
                StopCoroutine(_connectCoroutine);
            _connectCoroutine = StartCoroutine(ConnectCoroutine());
        }

        private IEnumerator ConnectCoroutine()
        {
            EnsureWebRtcUpdateLoop();
            ResetPeer(clearSignal: true);
            CreatePeer();
            _startedAt = Time.realtimeSinceStartup;
            _disconnectedAt = -1f;

            _status = "Creating offer";
            var transceiver = _peer.AddTransceiver(TrackKind.Video);
            transceiver.Direction = RTCRtpTransceiverDirection.RecvOnly;

            var offerOp = _peer.CreateOffer();
            yield return offerOp;
            if (offerOp.IsError)
            {
                _status = "CreateOffer failed";
                Debug.LogError($"[HttpSignaling] CreateOffer failed: {offerOp.Error.message}");
                ScheduleReconnect();
                yield break;
            }

            var offer = offerOp.Desc;
            var localOp = _peer.SetLocalDescription(ref offer);
            yield return localOp;
            if (localOp.IsError)
            {
                _status = "SetLocalDescription failed";
                Debug.LogError($"[HttpSignaling] SetLocalDescription failed: {localOp.Error.message}");
                ScheduleReconnect();
                yield break;
            }

            // Vanilla ICE: the server answers once with its own candidates
            // and has no trickle endpoint, so ours must ride inside the offer.
            _iceCandidates.Clear();
            float deadline = Time.realtimeSinceStartup + IceGatheringTimeout;
            while (Time.realtimeSinceStartup < deadline &&
                   _peer != null &&
                   _peer.GatheringState != RTCIceGatheringState.Complete)
                yield return null;

            string sdp = _peer != null ? _peer.LocalDescription.sdp : null;
            if (_peer == null)
                yield break; // torn down mid-handshake; StopStream owns the state

            bool candidatesWereEmbedded = sdp != null && sdp.Contains("a=candidate:");
            string patched = WebRtcSignalingProtocol.PatchSdpWithCandidates(sdp, _iceCandidates);
            if (!_offerSdpLogged)
            {
                _offerSdpLogged = true;
                Debug.Log($"[HttpSignaling] offer SDP (embedded={candidatesWereEmbedded} gathered={_iceCandidates.Count}):\n{patched}");
            }

            _status = "POST /offer";
            string body = WebRtcSignalingProtocol.BuildOfferRequestBody(
                patched, string.IsNullOrEmpty(codec) ? null : codec,
                string.IsNullOrEmpty(resolution) ? null : resolution);
            using (var request = new UnityWebRequest(url, "POST"))
            {
                request.timeout = 10;
                request.uploadHandler = new UploadHandlerRaw(System.Text.Encoding.UTF8.GetBytes(body));
                request.downloadHandler = new DownloadHandlerBuffer();
                request.SetRequestHeader("Content-Type", "application/json");
                request.certificateHandler = new AcceptAnyCertificate();
                yield return request.SendWebRequest();

                // The POST can outlive the stream (exit during the server's
                // slow ICE gathering): bail out instead of touching the
                // disposed peer.
                if (_peer == null)
                    yield break;

                if (request.result != UnityWebRequest.Result.Success || request.responseCode != 200)
                {
                    _status = $"HTTP {request.responseCode}";
                    string responseBody = request.downloadHandler != null ? request.downloadHandler.text : "";
                    Debug.LogError($"[HttpSignaling] POST failed: {request.result} code={request.responseCode} err={request.error} body={responseBody}");
                    ScheduleReconnect();
                    yield break;
                }

                var answerText = request.downloadHandler.text;
                string answerSdp = WebRtcSignalingProtocol.ExtractJsonString(answerText, "sdp");
                if (string.IsNullOrEmpty(answerSdp))
                {
                    _status = "Bad answer";
                    Debug.LogError($"[HttpSignaling] answer missing sdp: {answerText}");
                    ScheduleReconnect();
                    yield break;
                }
                Debug.Log($"[HttpSignaling] answer applied ({WebRtcSignalingProtocol.ExtractJsonString(answerText, "type")})");
                if (!_answerSdpLogged)
                {
                    _answerSdpLogged = true;
                    Debug.Log($"[HttpSignaling] answer SDP:\n{answerSdp}");
                }

                var answer = new RTCSessionDescription { type = RTCSdpType.Answer, sdp = answerSdp };
                if (_peer == null)
                    yield break; // torn down while the answer was in flight
                var remoteOp = _peer.SetRemoteDescription(ref answer);
                yield return remoteOp;
                if (remoteOp.IsError)
                {
                    _status = "Answer failed";
                    Debug.LogError($"[HttpSignaling] SetRemoteDescription failed: {remoteOp.Error.message}");
                    ScheduleReconnect();
                    yield break;
                }
            }

            _status = "Connecting";
            _connectCoroutine = null;
        }

        private void CreatePeer()
        {
            if (_peer != null)
                return;

            var configuration = new RTCConfiguration
            {
                iceServers = Array.Empty<RTCIceServer>()
            };
            _peer = new RTCPeerConnection(ref configuration);

            _peer.OnIceCandidate = candidate =>
            {
                // Not sent anywhere: candidates are patched into the offer
                // SDP (vanilla ICE). Collected here as the fallback source.
                if (candidate == null || string.IsNullOrEmpty(candidate.Candidate))
                    return;
                _iceCandidates.Add(candidate.Candidate);
            };

            _peer.OnConnectionStateChange = state =>
            {
                if (_ignorePeerStateChanges)
                    return;

                _status = $"WebRTC {state}";
                if (state == RTCPeerConnectionState.Disconnected)
                {
                    if (_disconnectedAt < 0f)
                        _disconnectedAt = Time.realtimeSinceStartup;
                }
                else
                {
                    _disconnectedAt = -1f;
                }

                if (state == RTCPeerConnectionState.Failed ||
                    state == RTCPeerConnectionState.Closed)
                {
                    SchedulePeerReset("Stream interrupted", clearSignal: false);
                }

                Debug.Log($"[HttpSignaling] Connection state: {state}");
            };

            _peer.OnTrack = e =>
            {
                if (e.Track is VideoStreamTrack track)
                {
                    _videoTrack = track;
                    _videoTrack.OnVideoReceived += texture =>
                    {
                        float now = Time.realtimeSinceStartup;
                        if (_lastFrameAt > 0f)
                        {
                            _lastFrameIntervalMs = (now - _lastFrameAt) * 1000f;
                            if (_lastFrameIntervalMs >= SlowFrameIntervalMs)
                                Debug.LogWarning($"[HttpSignaling] Slow video frame interval: {_lastFrameIntervalMs:0.0} ms");
                        }
                        _lastFrameAt = now;
                        _disconnectedAt = -1f;
                        _texture = texture;
                        _frameCount++;
                        _status = "Stream live";
                    };
                    _status = "Video track received";
                    Debug.Log("[HttpSignaling] Video track received");
                }
            };
        }

        private void ScheduleReconnect()
        {
            if (!_wantStream || _reconnectCoroutine != null)
                return;
            _reconnectCoroutine = StartCoroutine(ReconnectAfterDelay());
        }

        private IEnumerator ReconnectAfterDelay()
        {
            yield return new WaitForSeconds(PostFailureRetryDelay);
            _reconnectCoroutine = null;
            if (_wantStream)
                BeginConnect();
        }

        private void ResetPeer(bool clearSignal)
        {
            if (_resetCoroutine != null)
            {
                StopCoroutine(_resetCoroutine);
                _resetCoroutine = null;
            }

            _videoTrack?.Dispose();
            _videoTrack = null;
            if (clearSignal)
                _texture = null;
            var peer = _peer;
            _peer = null;
            if (peer != null)
            {
                _ignorePeerStateChanges = true;
                try
                {
                    peer.Close();
                    peer.Dispose();
                }
                finally
                {
                    _ignorePeerStateChanges = false;
                }
            }
            if (clearSignal)
                _frameCount = 0;
        }

        private void SchedulePeerReset(string status, bool clearSignal)
        {
            if (_resetCoroutine != null)
                return;

            _resetCoroutine = StartCoroutine(ResetPeerNextFrame(status, clearSignal));
        }

        private IEnumerator ResetPeerNextFrame(string status, bool clearSignal)
        {
            yield return null;
            _resetCoroutine = null;
            ResetPeer(clearSignal);
            _status = status;
        }

        private void EnsureWebRtcUpdateLoop()
        {
            if (_updateCoroutine == null)
                _updateCoroutine = StartCoroutine(WebRTC.Update());
            if (_statsCoroutine == null)
                _statsCoroutine = StartCoroutine(LogStatsPeriodically());
        }

        /// <summary>
        /// Latency attribution: log inbound-rtp stats every 5 s while a peer
        /// exists — decode fps, average jitter-buffer delay and loss. The
        /// jitter buffer is the segment we cannot set in this package
        /// version, so we measure it instead.
        /// </summary>
        private IEnumerator LogStatsPeriodically()
        {
            uint lastFramesDecoded = 0;
            float lastSampleAt = Time.realtimeSinceStartup;
            while (true)
            {
                yield return new WaitForSeconds(5f);
                var peer = _peer;
                if (peer == null)
                    continue;

                var op = peer.GetStats();
                yield return op;
                if (op.IsError || op.Value == null)
                    continue;

                using (var report = op.Value)
                {
                    foreach (var stat in report.Stats.Values)
                    {
                        if (stat.Type != RTCStatsType.InboundRtp || !(stat is RTCInboundRTPStreamStats inbound))
                            continue;

                        float now = Time.realtimeSinceStartup;
                        float decodeFps = inbound.framesDecoded > lastFramesDecoded
                            ? (inbound.framesDecoded - lastFramesDecoded) / (now - lastSampleAt)
                            : 0f;
                        lastFramesDecoded = inbound.framesDecoded;
                        lastSampleAt = now;

                        double avgJitterBufferMs = inbound.jitterBufferEmittedCount > 0
                            ? inbound.jitterBufferDelay * 1000.0 / inbound.jitterBufferEmittedCount
                            : 0.0;
                        Debug.Log($"[HttpSignaling] stats: decodeFps={decodeFps:0.0} framesDecoded={inbound.framesDecoded} " +
                                  $"avgJitterBuffer={avgJitterBufferMs:0.0}ms target={inbound.jitterBufferTargetDelay * 1000.0:0.0}ms " +
                                  $"packetsLost={inbound.packetsLost} jitter={inbound.jitter:0.000}s");
                    }
                }
            }
        }

        /// <summary>teleimager serves HTTPS with a self-signed certificate; the media path (DTLS/SRTP) is unaffected.</summary>
        private class AcceptAnyCertificate : CertificateHandler
        {
            protected override bool ValidateCertificate(byte[] certificateData) => true;
        }

        private void OnDestroy()
        {
            StopStream();
        }
    }
}
