using UnityEngine;

namespace PicoBridge.Immersive
{
    /// <summary>
    /// Head-locked "no video yet" spinner floating in front of the immersive
    /// screen until the stream delivers its first frame. Runtime-assembled by
    /// StereoImmersiveBootstrap (same self-assembly pattern as the screen
    /// quad) and driven by StereoImmersiveController. Uses Sprites/Default so
    /// no font or texture asset is needed.
    /// </summary>
    [DisallowMultipleComponent]
    public class StereoImmersiveLoadingSpinner : MonoBehaviour
    {
        [Tooltip("Spin speed, degrees per second.")]
        [SerializeField] private float spinSpeed = 240f;

        private Transform _ring;
        private bool _visible;

        /// <summary>Show while there is no video texture; toggling is free when unchanged.</summary>
        public bool Visible
        {
            get => _visible;
            set
            {
                if (_visible == value) return;
                _visible = value;
                if (_ring != null)
                    _ring.gameObject.SetActive(value);
            }
        }

        /// <summary>Build the dash ring as a child of the rig, slightly in
        /// front of the screen quad (caller supplies the rig distance).</summary>
        public void Build(Transform rigRoot, float distance)
        {
            var root = new GameObject("LoadingSpinner");
            root.transform.SetParent(rigRoot, false);
            _ring = root.transform;

            // Three dashes 120° apart = classic loading ring; each dash is a
            // thin quad tinted by Sprites/Default (plain color, no texture).
            var shader = Shader.Find("Sprites/Default");
            for (int i = 0; i < 3; i++)
            {
                var dash = GameObject.CreatePrimitive(PrimitiveType.Quad);
                dash.name = "Dash" + i;
                Object.Destroy(dash.GetComponent<Collider>());
                dash.transform.SetParent(_ring, false);
                dash.transform.localPosition = new Vector3(0f, 0.14f, distance - 0.05f);
                dash.transform.localRotation = Quaternion.Euler(0f, 0f, 120f * i);
                dash.transform.localScale = new Vector3(0.02f, 0.09f, 1f);

                var material = new Material(shader) { color = new Color(0.85f, 0.9f, 0.92f, 1f) };
                dash.GetComponent<Renderer>().sharedMaterial = material;
            }

            Visible = false;
        }

        private void Update()
        {
            if (_ring == null || !Visible) return;
            _ring.Rotate(0f, 0f, -spinSpeed * Time.deltaTime, Space.Self);
        }
    }
}
