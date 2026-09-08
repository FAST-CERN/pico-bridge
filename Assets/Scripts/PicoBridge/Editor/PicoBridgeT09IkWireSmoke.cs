#if UNITY_EDITOR
using System;
using System.IO;
using UnityEngine;
using PicoBridge.Tracking;

namespace PicoBridge.Editor
{
    /// <summary>
    /// Off-device smoke for the tracker-mode body wire content (tracker-ik
    /// map t09): the full pipeline tracker cache → t17 TryMap → t08 IK →
    /// Body JSON, plus the avatar cache feed. Frame rulings under test:
    /// the wire carries the IK's Unity-convention values VERBATIM (no
    /// serialize-time flip — unlike AppendBody, whose inputs are native),
    /// the head source flips native→Unity once on the way in, and
    /// BodyFrameCache gets the flipped-back native pose for the SDK avatar.
    /// Lost-side semantics ride the solver state machine (Held freeze).
    /// Run headless like the T03 smoke; marker "[T09SMOKE] PASS".
    /// </summary>
    public static class PicoBridgeT09IkWireSmoke
    {
        public static void Run()
        {
            int checks = 0;

            void Check(bool ok, string label)
            {
                if (!ok)
                    throw new InvalidOperationException("[T09SMOKE] FAIL: " + label);
                checks++;
                Debug.Log("[T09SMOKE] ok: " + label);
            }

            // ── fixtures ──
            TrackerFrameCache.ResetForTest();
            BodyFrameCache.ResetForTest();
            var storePath = Path.Combine(Application.temporaryCachePath, "t09-ik-wire-trim.json");
            TrackerHandCalibration.ResetForTest(storePath); // zero trim: hand ≡ puck
            const float clockNow = 10f;
            TrackerFrameCache.Clock = () => clockNow;

            // Native head source (the TrackerBodyHead contract): the
            // collector must flip it into the Unity frame the IK solves in.
            var nativeHeadPos = new Vector3(0.2f, 1.68f, 0.35f);
            var nativeHeadRot = Quaternion.Euler(-15f, 40f, 5f);
            const long headTs = 424242L;
            TrackerBodyHead.Source = (out Vector3 p, out Quaternion q, out long t) =>
            {
                p = nativeHeadPos;
                q = nativeHeadRot;
                t = headTs;
                return true;
            };
            var unityHeadPos = new Vector3(nativeHeadPos.x, nativeHeadPos.y, -nativeHeadPos.z);
            var unityHeadRot = new Quaternion(nativeHeadRot.x, nativeHeadRot.y, -nativeHeadRot.z, -nativeHeadRot.w);

            // Left hand: a reachable interior target (zero trim ⇒ the hand
            // pose IS the published puck pose). Right: never published.
            var leftShoulder = UpperBodyIkSolver.ShoulderAnchor(unityHeadPos, unityHeadRot, true);
            var leftPuckPos = leftShoulder + new Vector3(0.35f, -0.25f, 0.85f).normalized * 0.4f;
            var leftPuckRot = Quaternion.AngleAxis(30f, new Vector3(0.2f, 0.7f, 0.6f).normalized);
            TrackerFrameCache.PublishValid("left", 77, leftPuckPos, leftPuckRot, clockNow);

            var collector = new PicoTrackingCollector
            {
                TrackerBodyEnabled = true,
                OperatorHeightM = 1.75f,
            };
            var sb = (System.Text.StringBuilder)GetPrivate(collector, "_sb");

            try
            {
                sb.Clear();
                InvokePrivate(collector, "AppendTrackerBody");
                var json = sb.ToString();
                var poses = PoseFields(json);
                var parsed = ParsePoses(poses);

                // ── 1. block contract ──
                Check(poses.Length == 24 && json.EndsWith("],\"len\":24}") &&
                      json.Contains("\"poseSpace\":\"pico_body_local\"") &&
                      json.Contains("\"alignment\":\"pico_native\""),
                    "wire: 24 joints, block contract intact");
                Check(json.Contains($"\"t\":{headTs}"),
                    "wire: head timestamp propagates to joints");

                // ── 2. head slot = flipped source (numeric, not golden) ──
                Check((parsed[15].pos - unityHeadPos).magnitude < 1e-5f &&
                      Quaternion.Angle(parsed[15].rot, unityHeadRot) < 0.01f,
                    "wire: head slot = native source flipped to Unity");

                // ── 3. umbrella: wire joints == reference IK verbatim ──
                // (proves no serialize-time flip / no double correction)
                var reference = new UpperBodyIkSolver(1.75f);
                var refResult = reference.Solve(new UpperBodyIkSolver.FrameInput
                {
                    HeadPosition = unityHeadPos,
                    HeadRotation = unityHeadRot,
                    Left = new UpperBodyIkSolver.HandInput { Valid = true, Position = leftPuckPos, Rotation = leftPuckRot },
                    Right = default,
                    NowSeconds = 0.0,
                });
                bool verbatim = true;
                for (int i = 0; i < 24; i++)
                {
                    var dp = (parsed[i].pos - refResult.Positions[i]).magnitude;
                    var da = Quaternion.Angle(parsed[i].rot, refResult.Rotations[i]);
                    if (dp >= 1e-5f || da >= 0.05f)
                        Debug.Log($"[T09SMOKE] diag joint {i}: posDelta={dp:E3} rotDelta={da:F3}° wire={parsed[i].pos} ref={refResult.Positions[i]}");
                    verbatim &= dp < 1e-5f && da < 0.05f;
                }
                Check(verbatim, "wire: all 24 joints = IK output verbatim (Unity convention, no extra flip)");

                // ── 4. end-to-end left hand: published puck → wire wrist ──
                Check((parsed[20].pos - leftPuckPos).magnitude < 5e-3f,
                    "wire: left wrist = published puck position (zero trim, ≤5mm)");
                Check(Quaternion.Angle(parsed[20].rot, leftPuckRot * UpperBodyIkSolver.LeftWristConvention) < 1f,
                    "wire: left wrist rotation = puck∘C_wrist (≤1°)");

                // ── 5. never-seen right side = root-placed template arm ──
                var templateWrist = refResult.Positions[UpperBodyIkSolver.RightWrist];
                Check((parsed[21].pos - templateWrist).magnitude < 1e-5f,
                    "wire: right (never seen) = template arm");

                // ── 6. avatar cache: native = flip of the wire pose ──
                Check(BodyFrameCache.HasData, "avatar: BodyFrameCache fed in tracker mode");
                var cachePos = new Vector3[BodyFrameCache.JointCount];
                var cacheRot = new Quaternion[BodyFrameCache.JointCount];
                Check(BodyFrameCache.TryGetFrame(cachePos, cacheRot), "avatar: frame readable");
                bool cacheOk = true;
                foreach (int i in new[] { 0, 15, 20, 21 })
                {
                    var expectPos = new Vector3(parsed[i].pos.x, parsed[i].pos.y, -parsed[i].pos.z);
                    var expectRot = new Quaternion(parsed[i].rot.x, parsed[i].rot.y, -parsed[i].rot.z, -parsed[i].rot.w);
                    cacheOk &= (cachePos[i] - expectPos).magnitude < 1e-5f &&
                               Quaternion.Angle(cacheRot[i], expectRot) < 0.05f;
                }
                Check(cacheOk, "avatar: cached poses = flipped (native) versions of the wire poses");

                // ── 7. optical loss → Held freeze (wire stays verbatim) ──
                var frozenLeftWrist = poses[20];
                TrackerFrameCache.PublishInvalid("left", 77, clockNow);
                sb.Clear();
                InvokePrivate(collector, "AppendTrackerBody");
                var posesHeld = PoseFields(sb.ToString());
                Check(posesHeld[20] == frozenLeftWrist,
                    "wire: lost side freezes its last solved arm (Held, verbatim)");

                // ── 8. stale cache (no publish for 0.6s) → still Held ──
                TrackerFrameCache.Clock = () => clockNow + 0.6f;
                sb.Clear();
                InvokePrivate(collector, "AppendTrackerBody");
                var posesStale = PoseFields(sb.ToString());
                Check(posesStale[20] == frozenLeftWrist,
                    "wire: stale side still Held within the 1s window");
            }
            finally
            {
                TrackerBodyHead.Source = null;
                TrackerFrameCache.Clock = () => Time.unscaledTime;
                TrackerFrameCache.ResetForTest();
                BodyFrameCache.ResetForTest();
            }

            Debug.Log($"[T09SMOKE] PASS ({checks} checks)");
        }

        private struct ParsedPose
        {
            public Vector3 pos;
            public Quaternion rot;
        }

        private static string[] PoseFields(string bodyJson)
        {
            var matches = System.Text.RegularExpressions.Regex.Matches(bodyJson, "\"p\":\"([^\"]*)\"");
            var fields = new string[matches.Count];
            for (int i = 0; i < matches.Count; i++)
                fields[i] = matches[i].Groups[1].Value;
            return fields;
        }

        private static ParsedPose[] ParsePoses(string[] fields)
        {
            var culture = System.Globalization.CultureInfo.InvariantCulture;
            var parsed = new ParsedPose[fields.Length];
            for (int i = 0; i < fields.Length; i++)
            {
                var parts = fields[i].Split(',');
                // Normalize: F6 serialization rounding leaves ~1e-6 norm
                // error, which Unity's Angle does not divide out.
                var rot = new Quaternion(
                    float.Parse(parts[3], culture), float.Parse(parts[4], culture),
                    float.Parse(parts[5], culture), float.Parse(parts[6], culture)).normalized;
                parsed[i] = new ParsedPose
                {
                    pos = new Vector3(float.Parse(parts[0], culture), float.Parse(parts[1], culture), float.Parse(parts[2], culture)),
                    rot = rot,
                };
            }
            return parsed;
        }

        private static void InvokePrivate(object target, string method, params object[] args)
        {
            const System.Reflection.BindingFlags flags =
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Public;
            var info = target.GetType().GetMethod(method, flags);
            if (info == null)
                throw new InvalidOperationException($"[T09SMOKE] FAIL: method {target.GetType().Name}.{method} not found");
            info.Invoke(target, args);
        }

        private static object GetPrivate(object target, string field)
        {
            const System.Reflection.BindingFlags flags =
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Public;
            var info = target.GetType().GetField(field, flags);
            if (info == null)
                throw new InvalidOperationException($"[T09SMOKE] FAIL: field {target.GetType().Name}.{field} not found");
            return info.GetValue(target);
        }
    }
}
#endif
