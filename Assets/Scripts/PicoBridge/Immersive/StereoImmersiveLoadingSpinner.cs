using System.Collections.Generic;
using UnityEngine;

namespace PicoBridge.Immersive
{
    /// <summary>
    /// Head-locked "no video yet" comet spinner floating in front of the
    /// immersive screen until the stream delivers its first frame
    /// (spec -- UI grill 2026-09-02: small hollow ring orbiting with a gray
    /// trail as wide as the ring's diameter, fading toward the tail).
    ///
    /// The trail is NOT a rigidly rotated static gradient (that reads
    /// mechanical): the orbit is tiled with fixed tangent quads whose alpha
    /// is re-derived every frame from their angular lag behind the moving
    /// ring head, so the tail emits from the head and dissolves -- the
    /// classic textureless comet (research/spinner-techniques.md). Draw
    /// order: ring renders after the trail (renderQueue 3001 vs 3000) and
    /// the trail starts one gap behind the head so it never crosses the
    /// ring's hollow. Runtime-assembled by StereoImmersiveBootstrap;
    /// Sprites/Default keeps it free of font/texture assets.
    /// </summary>
    [DisallowMultipleComponent]
    public class StereoImmersiveLoadingSpinner : MonoBehaviour
    {
        [Tooltip("Head angular speed, degrees per second.")]
        [SerializeField] private float spinSpeed = 240f;

        private const float OrbitRadius = 0.14f;
        private const float HeadOuterRadius = 0.024f;  // ring outer radius (diameter ~4.8cm)
        private const float HeadBandWidth = 0.009f;    // ring band thickness
        private const int TrailSegments = 56;          // fixed tiles around the full orbit
        private const float TrailArcDegrees = 250f;
        private const float TrailGapDegrees = 9f;      // clears the ring's hollow
        private const float TrailWidth = 0.038f;       // slightly under the ring diameter
        private const float RampWidth = 0.2f;

        private Transform _root;
        private Transform _head;
        private readonly Quaternion[] _segRot = new Quaternion[TrailSegments];
        private readonly Vector3[] _segPos = new Vector3[TrailSegments];
        private readonly Material[] _segMat = new Material[TrailSegments];
        private float _headAngle;
        private bool _visible;
        private static readonly Color TrailTint = new Color(0.62f, 0.66f, 0.68f, 1f);

        /// <summary>Show while there is no video texture; toggling is free when unchanged.</summary>
        public bool Visible
        {
            get => _visible;
            set
            {
                if (_visible == value) return;
                _visible = value;
                if (_root != null)
                    _root.gameObject.SetActive(value);
            }
        }

        /// <summary>Build the comet (procedural ring head + fixed trail tiles)
        /// as a child of the rig, slightly in front of the screen quad.</summary>
        public void Build(Transform rigRoot, float distance)
        {
            var shader = Shader.Find("Sprites/Default");
            var root = new GameObject("LoadingSpinner");
            root.transform.SetParent(rigRoot, false);
            _root = root.transform;
            float zHead = distance - 0.05f;
            float zTrail = zHead - 0.002f;

            // Head: hollow ring as a procedural annulus mesh; drawn after the
            // trail via renderQueue so the ring always occludes cleanly.
            var headGo = new GameObject("RingHead");
            headGo.transform.SetParent(_root, false);
            var filter = headGo.AddComponent<MeshFilter>();
            filter.sharedMesh = BuildAnnulusMesh(HeadOuterRadius, HeadOuterRadius - HeadBandWidth);
            var headRenderer = headGo.AddComponent<MeshRenderer>();
            var headMat = new Material(shader) { color = new Color(0.95f, 0.97f, 0.98f, 1f) };
            headMat.renderQueue = 3001;
            headRenderer.sharedMaterial = headMat;
            _head = headGo.transform;

            // Trail tiles: tangent quads fixed around the orbit; only their
            // material alpha animates (lag behind the head).
            float segAngle = 360f / TrailSegments;
            float chord = 2f * OrbitRadius * Mathf.Sin(segAngle * Mathf.Deg2Rad * 0.5f);
            for (int k = 0; k < TrailSegments; k++)
            {
                float angle = k * segAngle;
                var slot = Quaternion.Euler(0f, 0f, angle);

                var seg = GameObject.CreatePrimitive(PrimitiveType.Quad);
                seg.name = "Trail" + k;
                Object.Destroy(seg.GetComponent<Collider>());
                seg.transform.SetParent(_root, false);
                _segPos[k] = slot * new Vector3(0f, OrbitRadius, zTrail);
                _segRot[k] = slot * Quaternion.Euler(0f, 0f, 90f); // tangent
                seg.transform.localPosition = _segPos[k];
                seg.transform.localRotation = _segRot[k];
                seg.transform.localScale = new Vector3(TrailWidth, chord * 1.25f, 1f);

                var mat = new Material(shader);
                mat.renderQueue = 3000;
                _segMat[k] = mat;
                seg.GetComponent<Renderer>().sharedMaterial = mat;
            }

            _headAngle = 0f;
            ApplyFrame(0f);
            Visible = false;
        }

        private void Update()
        {
            if (_root == null || !_visible)
                return;
            // unscaled: a loading screen must survive timeScale = 0
            // (spinner-techniques.md, Unity Time docs).
            _headAngle = Mathf.Repeat(_headAngle - spinSpeed * Time.unscaledDeltaTime, 360f);
            ApplyFrame(_headAngle);
        }

        /// <summary>Per-frame comet alpha: lag is measured in the direction
        /// opposite the motion, so the tail sits BEHIND the head. Asymmetric
        /// easing: quick smooth ramp to peak just off the head, quadratic
        /// decay toward the tail tip.</summary>
        private void ApplyFrame(float headAngle)
        {
            if (_head != null)
                _head.localPosition = Quaternion.Euler(0f, 0f, headAngle)
                    * new Vector3(0f, OrbitRadius, _head.localPosition.z);

            float segAngle = 360f / TrailSegments;
            for (int k = 0; k < TrailSegments; k++)
            {
                // head moves clockwise (angle decreasing): positions it has
                // passed lie at increasing angle => lag = segAngle_k - head.
                float lag = Mathf.Repeat(k * segAngle - headAngle, 360f);
                float x = (lag - TrailGapDegrees) / TrailArcDegrees;
                float alpha;
                if (x < 0f || x > 1f)
                    alpha = 0f;
                else if (x < RampWidth)
                    alpha = Mathf.SmoothStep(0f, 1f, x / RampWidth);
                else
                {
                    float t = (x - RampWidth) / (1f - RampWidth);
                    alpha = (1f - t) * (1f - t);
                }
                _segMat[k].color = new Color(TrailTint.r, TrailTint.g, TrailTint.b, 0.6f * alpha);
            }
        }

        private static Mesh BuildAnnulusMesh(float outer, float inner)
        {
            const int segments = 40;
            var vertices = new List<Vector3>(segments * 2);
            var triangles = new List<int>(segments * 6);
            for (int i = 0; i <= segments; i++)
            {
                float phi = 2f * Mathf.PI * i / segments;
                var dir = new Vector3(Mathf.Cos(phi), Mathf.Sin(phi), 0f);
                vertices.Add(dir * outer);
                vertices.Add(dir * inner);
                if (i > 0)
                {
                    int b = vertices.Count - 1;
                    triangles.AddRange(new[] { b - 3, b - 1, b, b - 3, b, b - 2 });
                }
            }
            var mesh = new Mesh
            {
                vertices = vertices.ToArray(),
                triangles = triangles.ToArray()
            };
            mesh.RecalculateNormals();
            return mesh;
        }
    }
}
