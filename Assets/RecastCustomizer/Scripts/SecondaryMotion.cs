using UnityEngine;

namespace RecastCustomizer
{
    /// <summary>
    /// A soft sway for a part that hangs, so it answers a change of outfit a beat after the
    /// camera does.
    ///
    /// Two things matter and both were wrong in the first pass.
    ///
    /// First, where it turns. A transform rotates about its own origin, and this mesh's
    /// origin sits well away from the mesh itself, so rotating it swung the whole tassel
    /// bodily and tore the strap away from the cuff. It has to turn about the point where it
    /// is attached: the top of the tassel stays welded to the glove, and the swing grows
    /// toward the loose fringe at the bottom. That is how anything hanging behaves.
    ///
    /// Second, how it moves. A springy bounce reads as rubber. Cord and leather do not bounce
    /// -- they lean, drift back, and settle. So the swing is slow and shallow with a long
    /// decay, closer to a flag than a spring.
    /// </summary>
    [DisallowMultipleComponent]
    public class SecondaryMotion : MonoBehaviour
    {
        [Tooltip("How far the loose end swings at the start, in degrees. Small on purpose: " +
                 "this should read as fabric settling, not as a spring.")]
        [Range(0f, 20f)]
        [SerializeField] private float amplitude = 7f;

        [Tooltip("Swings per second. Low reads heavy and calm; high reads springy.")]
        [Range(0.2f, 3f)]
        [SerializeField] private float frequency = 2.6f;

        [Tooltip("Seconds for the sway to die away.")]
        [Range(0.5f, 6f)]
        [SerializeField] private float settle = 0.675f;

        [Tooltip("Axis the piece swings around, in local space.")]
        [SerializeField] private Vector3 axis = new Vector3(0f, 0f, 1f);

        [Tooltip("A slower, smaller sway on a second axis, so the motion is not a flat " +
                 "back-and-forth in one plane.")]
        [Range(0f, 1f)]
        [SerializeField] private float crossAxis = 0.35f;

        [Tooltip("The attachment point, in local space, that must not move. Left at zero it " +
                 "is worked out from the top of the mesh, which is where a hanging piece " +
                 "joins whatever it hangs from.")]
        [SerializeField] private Vector3 pivot = Vector3.zero;

        private Quaternion _restRotation;
        private Vector3 _restPosition;
        private Vector3 _pivot;
        private float _time = -1f;      // negative means at rest
        private float _strength = 1f;

        private void Awake() => Capture();
        private void OnEnable() { Capture(); _time = -1f; }

        private void Capture()
        {
            _restRotation = transform.localRotation;
            _restPosition = transform.localPosition;

            _pivot = pivot;
            if (_pivot == Vector3.zero)
            {
                var mf = GetComponent<MeshFilter>();
                if (mf != null && mf.sharedMesh != null)
                {
                    // Top-centre of the mesh: for a tassel, a pendant, a strap, that is the
                    // end that is fastened down.
                    var b = mf.sharedMesh.bounds;
                    _pivot = new Vector3(b.center.x, b.max.y, b.center.z);
                }
            }
        }

        /// <summary>Set it swaying. Called when the outfit changes.</summary>
        public void Nudge(float strength = 1f)
        {
            _time = 0f;
            _strength = Mathf.Clamp(strength, 0f, 3f);
        }

        private void LateUpdate()
        {
            if (_time < 0f) return;

            _time += Time.deltaTime;

            float decay = Mathf.Exp(-_time / Mathf.Max(0.0001f, settle * 0.4f));
            if (decay < 0.002f)
            {
                transform.localRotation = _restRotation;
                transform.localPosition = _restPosition;
                _time = -1f;
                return;
            }

            float a = amplitude * _strength * decay;
            float phase = _time * frequency * Mathf.PI * 2f;

            var main = Quaternion.AngleAxis(Mathf.Sin(phase) * a, axis.normalized);

            // The cross sway runs at a different rate on purpose. Matching rates would trace
            // a straight diagonal; mismatched ones trace a slow figure of eight, which is
            // what something hanging on a cord actually does.
            var other = Vector3.Cross(axis.normalized, Vector3.up);
            if (other.sqrMagnitude < 0.001f) other = Vector3.right;
            var cross = Quaternion.AngleAxis(
                Mathf.Sin(phase * 0.61f) * a * crossAxis, other.normalized);

            var rot = _restRotation * main * cross;

            // Turn about the attachment point rather than the transform origin. Position is
            // corrected so the pivot lands exactly where it sits at rest, which is what keeps
            // the top of the tassel welded to the cuff while the fringe swings.
            transform.localRotation = rot;
            transform.localPosition = _restPosition
                                    + _restRotation * _pivot
                                    - rot * _pivot;
        }

        private void OnDrawGizmosSelected()
        {
            if (!Application.isPlaying) Capture();
            Gizmos.color = new Color(1f, 0.82f, 0.34f, 0.9f);
            Gizmos.matrix = transform.parent != null
                ? transform.parent.localToWorldMatrix : Matrix4x4.identity;
            Gizmos.DrawWireSphere(_restPosition + _restRotation * _pivot, 0.012f);
        }
    }
}
