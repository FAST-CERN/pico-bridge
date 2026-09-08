#if UNITY_EDITOR
using System;
using UnityEngine;
using PicoBridge.Tracking;

namespace PicoBridge.Editor
{
    /// <summary>
    /// Off-device smoke for the tracker data foundation (tracker-ik map t01):
    /// TrackerFrameCache, TrackerPosePoller (injected sources), and
    /// TrackerSessionStatus. Run headless:
    ///
    ///   Unity.exe -batchmode -nographics -projectPath &lt;repo&gt; \
    ///     -executeMethod PicoBridge.Editor.PicoBridgeT01TrackerSmoke.Run -logFile &lt;log&gt;
    ///
    /// Success marker: "[T01SMOKE] PASS". Expected values are hand-written
    /// golden literals (wire contract / flip convention), never recomputed.
    /// </summary>
    public static class PicoBridgeT01TrackerSmoke
    {
        // Raw PXR-frame sample; the flip is (-Z, -Qz, -Qw): the improper Z-mirror
        // conjugates axis AND sense, negating Qz and Qw (device-confirmed by the
        // gizmo cube rotating against the hand when Qw was kept, 2026-09-08).
        private static readonly Vector3 RawPos = new Vector3(0.5f, -0.25f, 1.0f);
        private static readonly Quaternion RawRot = new Quaternion(0.1f, 0.2f, 0.3f, 0.9f);
        private static readonly Vector3 FlippedPos = new Vector3(0.5f, -0.25f, -1.0f);
        private static readonly Quaternion FlippedRot = new Quaternion(0.1f, 0.2f, -0.3f, -0.9f);

        public static void Run()
        {
            int checks = 0;

            void Check(bool ok, string label)
            {
                if (!ok)
                    throw new InvalidOperationException("[T01SMOKE] FAIL: " + label);
                checks++;
                Debug.Log("[T01SMOKE] ok: " + label);
            }

            bool Nearly(float a, float b) => Mathf.Abs(a - b) < 1e-5f;

            // ── 1. TrackerFrameCache ─────────────────────────────

            TrackerFrameCache.ResetForTest();
            TrackerFrameCache.Clock = () => 100f;

            Check(!TrackerFrameCache.TryGetFrame("left", out _), "cache: absent before publish");

            TrackerFrameCache.PublishValid("left", 7, FlippedPos, FlippedRot, 100f);
            Check(TrackerFrameCache.TryGetFrame("left", out var left) && left.Sn == 7 && left.Valid && left.HasPose,
                "cache: valid publish stored with sn/valid/hasPose");
            Check(Nearly(left.Position.x, 0.5f) && Nearly(left.Position.y, -0.25f) && Nearly(left.Position.z, -1.0f),
                "cache: stored position is the flipped golden (0.5,-0.25,-1.0)");
            Check(Nearly(left.Rotation.x, 0.1f) && Nearly(left.Rotation.y, 0.2f) &&
                  Nearly(left.Rotation.z, -0.3f) && Nearly(left.Rotation.w, -0.9f),
                "cache: stored rotation is the flipped golden (0.1,0.2,-0.3,-0.9)");

            TrackerFrameCache.PublishInvalid("left", 7, 100.2f);
            Check(TrackerFrameCache.TryGetFrame("left", out var ghost) && !ghost.Valid &&
                  Nearly(ghost.Position.z, -1.0f) && Nearly(ghost.LastUpdate, 100.2f),
                "cache: invalid publish clears validity, keeps last pose, stamps time");

            Check(TrackerFrameCache.IsFresh(ghost, 100.3f), "cache: fresh within StaleAfter");
            Check(!TrackerFrameCache.IsFresh(ghost, 101.0f), "cache: stale past StaleAfter (0.5s)");

            TrackerFrameCache.ResetForTest();
            Check(!TrackerFrameCache.TryGetFrame("left", out _), "cache: reset clears sides");

            // ── 2. TrackerPosePoller (injected sources) ──────────

            bool poseValid = true;
            bool apiOk = true;
            var pollerObject = new GameObject("T01SmokePoller");
            try
            {
                var poller = pollerObject.AddComponent<TrackerPosePoller>();
                poller.SnSource = (string side, out long sn) =>
                {
                    sn = side == "left" ? 7 : 0;
                    return side == "left";
                };
                poller.PoseSource = (long sn, out Vector3 p, out Quaternion q, out bool valid) =>
                {
                    p = RawPos; q = RawRot; valid = poseValid;
                    return apiOk;
                };

                poller.ManualPoll(100f);
                Check(TrackerFrameCache.TryGetFrame("left", out var pl) && pl.Valid && pl.Sn == 7 &&
                      Nearly(pl.Position.z, -1.0f) && Nearly(pl.Rotation.z, -0.3f) && Nearly(pl.Rotation.w, -0.9f),
                    "poller: raw PXR pose flipped into cache (-Z, -Qz, -Qw - improper-mirror conjugation)");
                Check(MotionTrackerBinding.TryGetOpticalSample("left", out bool optical) && optical,
                    "poller: optical sample fed true on valid");
                Check(!TrackerFrameCache.TryGetFrame("right", out _),
                    "poller: side without bound SN publishes nothing");

                poseValid = false;
                poller.ManualPoll(101f);
                Check(TrackerFrameCache.TryGetFrame("left", out var inv) && !inv.Valid &&
                      Nearly(inv.Position.z, -1.0f) && Nearly(inv.LastUpdate, 101f),
                    "poller: optical loss keeps ghost pose, validity off");
                Check(MotionTrackerBinding.TryGetOpticalSample("left", out bool opticalLost) && !opticalLost,
                    "poller: optical sample fed false on loss");

                apiOk = false;
                poller.ManualPoll(102f);
                Check(TrackerFrameCache.TryGetFrame("left", out var apiFail) && !apiFail.Valid &&
                      Nearly(apiFail.LastUpdate, 102f),
                    "poller: API failure maps to invalid sample (existing viz semantics)");
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(pollerObject);
            }

            // ── 3. TrackerSessionStatus ──────────────────────────

            MotionTrackerBinding.ResetForTest();
            TrackerFrameCache.ResetForTest();
            TrackerSessionStatus.ResetForTest();
            int guidanceCalls = 0;
            TrackerSessionStatus.GuidanceCaller = () => guidanceCalls++;

            var idle = TrackerSessionStatus.Evaluate();
            Check(idle.Left.State == TrackerSideState.Unbound && idle.Right.State == TrackerSideState.Unbound,
                "status: unbound sides report Unbound");
            Check(!idle.ShouldPromptGuidance, "status: no guidance while enumeration not started");

            MotionTrackerBinding.SetBindingStateForTest(true, 7, 8, 7); // left connected, right bound only
            var mixed = TrackerSessionStatus.Evaluate();
            Check(mixed.Left.State == TrackerSideState.OpticalLost && mixed.Right.State == TrackerSideState.Disconnected,
                "status: connected-without-pose = OpticalLost, bound-offline = Disconnected");
            Check(mixed.ShouldPromptGuidance, "status: guidance wanted once tracker flow live with no valid side");

            TrackerFrameCache.PublishValid("left", 7, FlippedPos, FlippedRot, 100f);
            TrackerFrameCache.Clock = () => 100.1f;
            var live = TrackerSessionStatus.Evaluate();
            Check(live.Left.State == TrackerSideState.Valid && live.AnyValid && !live.ShouldPromptGuidance,
                "status: fresh valid frame = Valid, no guidance");
            Check(TrackerSessionStatus.PanelSuffix().Length == 0, "status: panel suffix empty when any side valid");

            TrackerFrameCache.Clock = () => 105f; // everything stale now
            Check(TrackerSessionStatus.PanelSuffix() == " !conn",
                "status: panel suffix flags trouble as ASCII !conn (font has no CJK)");
            Check(TrackerSessionStatus.PromptText(TrackerSideState.Unbound).Length > 0 &&
                  TrackerSessionStatus.PromptText(TrackerSideState.OpticalLost).Length > 0,
                "status: per-state prompt text present");

            var wantGuidance = TrackerSessionStatus.Evaluate();
            Check(wantGuidance.ShouldPromptGuidance, "status: guidance wanted again once sides lost");
            TrackerSessionStatus.RequestGuidance();
            TrackerSessionStatus.RequestGuidance();
            Check(guidanceCalls == 2, "status: explicit guidance always fires the caller");
            Check(!TrackerSessionStatus.Evaluate().ShouldPromptGuidance,
                "status: auto guidance latched off after first request");

            // ── 4. Panel SN-row suffix (prefab, real controller) ─

            const string PanelPrefabPath = "Assets/Prefabs/PicoBridge/PicoBridgePanel.prefab";
            var prefab = UnityEditor.AssetDatabase.LoadAssetAtPath<GameObject>(PanelPrefabPath);
            Check(prefab != null, "panel: prefab loads");
            var panelInstance = (GameObject)UnityEditor.PrefabUtility.InstantiatePrefab(prefab);
            var panelManagerObject = new GameObject("T01PanelManager");
            try
            {
                var controller = panelInstance.GetComponentInChildren<UI.PicoBridgePanelController>(true);
                Check(controller != null, "panel: controller found");
                var panelManager = panelManagerObject.AddComponent<PicoBridgeManager>();
                SetPrivate(controller, "manager", panelManager);
                InvokePrivate(controller, "ConfigureArmSourceControl");
                var armRow = FindDeep(panelInstance.transform, "ArmSourceControl");
                Check(armRow != null && armRow.Find("TrackerBinding") != null, "panel: SN text node present");
                var snText = armRow.Find("TrackerBinding").GetComponent<TMPro.TMP_Text>();
                Check(snText != null, "panel: SN text is TMP_Text");

                MotionTrackerBinding.SetBindingStateForTest(true, 7, 8, 7);
                TrackerFrameCache.ResetForTest();
                TrackerFrameCache.Clock = () => 100f;
                InvokePrivate(controller, "RefreshArmSourceControl");
                Check(snText.text.Contains("!conn"), "panel: SN row flags when no side valid (suffix)");

                TrackerFrameCache.PublishValid("left", 7, FlippedPos, FlippedRot, 100f);
                InvokePrivate(controller, "RefreshArmSourceControl");
                Check(!snText.text.Contains("⚠"), "panel: SN row clean when a side is valid");
                Check(snText.text.Contains("L:7"), "panel: SN row still shows binding summary");
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(panelInstance);
                UnityEngine.Object.DestroyImmediate(panelManagerObject);
            }

            // ── 5. Visualizer renders from the cache ─────────────

            var vizObject = new GameObject("T01SmokeViz");
            try
            {
                var viz = vizObject.AddComponent<MotionTrackerVisualizer>();
                InvokePrivate(viz, "Start");
                var leftGizmo = GetPrivate(viz, "_left");
                var leftRootGo = (GameObject)GetPrivate(leftGizmo, "Root");
                Check(leftRootGo != null && !leftRootGo.activeSelf, "viz: hidden before first frame");

                TrackerFrameCache.ResetForTest();
                TrackerFrameCache.Clock = () => 100f;
                TrackerFrameCache.PublishValid("left", 7, FlippedPos, FlippedRot, 100f);
                InvokePrivate(viz, "PollSide", "left", leftGizmo);
                Check(leftRootGo.activeSelf, "viz: side shows on fresh valid frame");
                Check(Nearly(leftRootGo.transform.position.x, 0.5f) && Nearly(leftRootGo.transform.position.y, -0.25f) &&
                      Nearly(leftRootGo.transform.position.z, -1.0f), "viz: gizmo placed at flipped golden pose");

                var cubeMat = (Material)GetPrivate(leftGizmo, "CubeMaterial");
                Check(Nearly(cubeMat.color.r, 1.0f) && Nearly(cubeMat.color.g, 0.55f) && Nearly(cubeMat.color.b, 0.15f),
                    "viz: valid cube painted side color (left orange 1.0/0.55/0.15)");

                TrackerFrameCache.PublishInvalid("left", 7, 100.2f);
                InvokePrivate(viz, "PollSide", "left", leftGizmo);
                Check(leftRootGo.activeSelf && Nearly(leftRootGo.transform.position.z, -1.0f) &&
                      Nearly(cubeMat.color.r, 0.4f) && Nearly(cubeMat.color.g, 0.4f) && Nearly(cubeMat.color.b, 0.44f),
                    "viz: optical loss keeps ghost pose, cube gray (0.4/0.4/0.44)");

                TrackerFrameCache.Clock = () => 105f; // stale
                InvokePrivate(viz, "PollSide", "left", leftGizmo);
                Check(!leftRootGo.activeSelf, "viz: stale side hides");
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(vizObject);
            }

            // ── 6. Wire contract from the cache (golden literals) ─

            var collector = new PicoTrackingCollector();
            var sb = (System.Text.StringBuilder)GetPrivate(collector, "_sb");

            TrackerFrameCache.ResetForTest();
            TrackerFrameCache.Clock = () => 100f;
            TrackerFrameCache.PublishValid("left", 7, FlippedPos, FlippedRot, 100f);
            TrackerFrameCache.PublishInvalid("right", 8, 100f);
            sb.Clear();
            InvokePrivate(collector, "AppendTrackerSide", "left");
            Check(sb.ToString() ==
                ",\"left\":{\"sn\":7,\"p\":\"0.500000,-0.250000,-1.000000,0.100000,0.200000,-0.300000,-0.900000\",\"valid\":true}",
                "wire: valid side serializes the receiver-0.2.x golden literal");

            sb.Clear();
            InvokePrivate(collector, "AppendTrackerSide", "right");
            Check(sb.ToString() ==
                ",\"right\":{\"sn\":8,\"p\":\"0.000000,0.000000,0.000000,0.000000,0.000000,0.000000,1.000000\",\"valid\":false}",
                "wire: pose-less invalid side serializes zero pose + valid:false");

            sb.Clear();
            TrackerFrameCache.Clock = () => 105f; // everything stale now
            InvokePrivate(collector, "AppendTrackerSide", "left");
            InvokePrivate(collector, "AppendTrackerSide", "right");
            Check(sb.ToString().Length == 0, "wire: stale sides omitted (receiver sees inactive)");

            TrackerSessionStatus.GuidanceCaller = null;
            MotionTrackerBinding.ResetForTest();
            TrackerFrameCache.ResetForTest();
            TrackerSessionStatus.ResetForTest();

            Debug.Log($"[T01SMOKE] PASS ({checks} checks)");
        }

        private static Transform FindDeep(Transform root, string name)
        {
            foreach (Transform child in root)
            {
                if (child.name == name)
                    return child;
                var found = FindDeep(child, name);
                if (found != null)
                    return found;
            }
            return null;
        }

        private static void InvokePrivate(object target, string method, params object[] args)
        {
            const System.Reflection.BindingFlags flags =
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Public;
            var info = target.GetType().GetMethod(method, flags);
            if (info == null)
                throw new InvalidOperationException($"[T01SMOKE] FAIL: method {target.GetType().Name}.{method} not found");
            info.Invoke(target, args);
        }

        private static object GetPrivate(object target, string field)
        {
            const System.Reflection.BindingFlags flags =
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Public;
            var info = target.GetType().GetField(field, flags);
            if (info == null)
                throw new InvalidOperationException($"[T01SMOKE] FAIL: field {target.GetType().Name}.{field} not found");
            return info.GetValue(target);
        }

        private static void SetPrivate(object target, string field, object value)
        {
            const System.Reflection.BindingFlags flags =
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Public;
            var info = target.GetType().GetField(field, flags);
            if (info == null)
                throw new InvalidOperationException($"[T01SMOKE] FAIL: field {target.GetType().Name}.{field} not found");
            info.SetValue(target, value);
        }
    }
}
#endif
