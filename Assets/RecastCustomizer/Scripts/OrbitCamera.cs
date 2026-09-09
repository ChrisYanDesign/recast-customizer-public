using UnityEngine;

namespace RecastCustomizer
{
    /// <summary>
    /// Product-viewer camera: the target stays locked in the centre of frame and the camera
    /// orbits around it within a limited arc.
    ///
    /// Deliberately constrained rather than free-flying:
    ///   - yaw is clamped to a front-facing arc, so the viewer never ends up behind the
    ///     asset where the key light does not reach
    ///   - the pivot is the target's bounds centre and cannot be moved, so the subject can
    ///     never drift out of frame
    ///   - zoom is clamped to a band around the framed distance, so it cannot be pushed
    ///     until the asset is a speck or pulled inside the mesh
    ///
    /// Call <see cref="FrameTarget"/> to fit the subject and derive all the limits from its
    /// actual size — the editor tool does this at setup.
    ///
    /// Reads the legacy Input class so it works whether or not the new Input System is
    /// installed, and so touch-drag maps to orbit for free on mobile and in a web build.
    /// </summary>
    [RequireComponent(typeof(Camera))]
    public class OrbitCamera : MonoBehaviour
    {
        [Header("Target")]
        [SerializeField] private Transform target;
        [Tooltip("Offset from the target's pivot to the point the camera looks at. " +
                 "FrameTarget sets this to the bounds centre automatically.")]
        [SerializeField] private Vector3 targetOffset = Vector3.zero;

        [Header("Orbit")]
        [SerializeField] private float orbitSpeed = 500f;
        [Tooltip("Degrees the view may swing one way from the starting angle, the " +
                 "direction you get by dragging the mouse LEFT.")]
        [Range(15f, 180f)]
        [SerializeField] private float yawLimitLeft = 90f;
        [Tooltip("Degrees the view may swing the other way, dragging the mouse RIGHT. " +
                 "On the falcon glove this is the side that swings round toward the cuff, " +
                 "so it is held short of the angle where the open sleeve hole comes into " +
                 "view. If the wrong side is clamping, swap these two numbers.")]
        [Range(15f, 180f)]
        [SerializeField] private float yawLimitRight = 50f;
        [Tooltip("Degrees the view may tilt above and below the starting angle.")]
        [Range(10f, 89f)]
        [SerializeField] private float pitchLimit = 55f;

        [Header("Framing")]
        [Tooltip("How much room to leave around the asset when framing it. 1 fits the " +
                 "bounding sphere exactly; below 1 crops in for a tighter hero shot.")]
        [Range(0.4f, 2f)]
        [SerializeField] private float framePadding = 0.72f;

        [Header("Zoom")]
        [Tooltip("Closest approach, as a fraction of the framed distance.")]
        [Range(0.15f, 1f)]
        [SerializeField] private float minZoomFactor = 0.32f;
        [Tooltip("Furthest pull-back, as a fraction of the framed distance.")]
        [Range(1f, 4f)]
        [SerializeField] private float maxZoomFactor = 2.2f;
        [SerializeField] private float zoomSpeed = 3f;

        [Header("Pan")]
        [Tooltip("Hold the middle mouse button to slide the view. Right-drag works too.")]
        [SerializeField] private bool allowPan = true;
        [SerializeField] private float panSpeed = 3.2f;
        [Tooltip("How far the pivot may slide from the asset, as a fraction of the framed " +
                 "distance. Keeps the glove from being pushed off screen entirely.")]
        [Range(0.05f, 1f)]
        [SerializeField] private float panLimit = 0.38f;

        [Header("Feel")]
        [Tooltip("0 = instant, higher = smoother. Seconds of catch-up.")]
        [SerializeField] private float smoothing = 0.08f;
        [SerializeField] private float autoSpinSpeed = 0f;

        [Header("Breath")]
        [Tooltip("How far the camera dips in when the outfit changes, as a fraction of the " +
                 "framed distance. A small settle reads as weight; too much reads as a jolt.")]
        [Range(0f, 0.2f)]
        [SerializeField] private float breathAmount = 0.045f;
        [Tooltip("Seconds for the dip and recovery.")]
        [Range(0.1f, 1.5f)]
        [SerializeField] private float breathTime = 0.42f;

        // resolved at Start / FrameTarget
        private float _frameDistance = 3f;
        private float _distance = 3f;
        private float _targetDistance = 3f;
        private float _homeYaw;
        private float _homePitch;
        private float _yaw;
        private float _pitch;
        private Vector3 _velocity;
        private Vector3 _panOffset;
        private float _breath;      // 0 at rest, 1 at the bottom of the dip
        private float _breathTimer;

        private void Start()
        {
            if (target == null)
            {
                var assembly = FindFirstObjectByType<ModularGloveAssembly>();
                if (assembly != null) target = assembly.transform;
            }

            // whatever angle the camera was placed at in the scene becomes "front"
            var euler = transform.eulerAngles;
            _homeYaw = euler.y;
            _homePitch = Normalize(euler.x);
            _yaw = _homeYaw;
            _pitch = _homePitch;

            // Frame from the asset's real bounds at startup. The editor tool also calls this,
            // but the distance it works out lives in a private field that does not survive
            // into play mode — so without this the view opens at an arbitrary default and
            // the glove sits too far away regardless of how it was framed in the editor.
            FrameTarget();
        }

        private void LateUpdate()
        {
            if (target == null) return;

            HandleInput();

            _distance = smoothing > 0f
                ? Mathf.Lerp(_distance, _targetDistance, 1f - Mathf.Exp(-Time.deltaTime / smoothing))
                : _targetDistance;

            if (!Mathf.Approximately(autoSpinSpeed, 0f))
                _yaw = ClampYaw(_yaw + autoSpinSpeed * Time.deltaTime);

            UpdateBreath();

            // pan is clamped to a small radius, so the glove can be nudged for framing but
            // never pushed out of the shot entirely
            var pivot = target.position + targetOffset + _panOffset;
            var rotation = Quaternion.Euler(_pitch, _yaw, 0f);
            float dolly = _distance * (1f - _breath * breathAmount);
            var wanted = pivot + rotation * new Vector3(0f, 0f, -dolly);

            transform.position = smoothing > 0f
                ? Vector3.SmoothDamp(transform.position, wanted, ref _velocity, smoothing)
                : wanted;
            transform.rotation = rotation;
        }

        private void HandleInput()
        {
            // The interface sits on top of the viewport, so a drag or a scroll that starts
            // over a control belongs to that control, not to the camera. Without this the
            // 3D view zooms while the user is scrolling the panel.
            if (GloveCustomizerUI.PointerOverUI) return;

            if (Input.GetMouseButton(0))
            {
                _yaw = ClampYaw(_yaw + Input.GetAxis("Mouse X") * orbitSpeed * Time.deltaTime);
                _pitch = ClampPitch(_pitch - Input.GetAxis("Mouse Y") * orbitSpeed * Time.deltaTime);
            }

            // middle button (or right) slides the view without changing the orbit
            if (allowPan && (Input.GetMouseButton(2) || Input.GetMouseButton(1)))
            {
                var slide = new Vector3(-Input.GetAxis("Mouse X"), -Input.GetAxis("Mouse Y"), 0f);
                _panOffset += transform.rotation * slide * (_distance * panSpeed * Time.deltaTime);

                float maxPan = _frameDistance * panLimit;
                if (_panOffset.magnitude > maxPan) _panOffset = _panOffset.normalized * maxPan;
            }

            float scroll = Input.GetAxis("Mouse ScrollWheel");
            if (Mathf.Abs(scroll) > 0.0001f)
            {
                _targetDistance -= scroll * zoomSpeed * Mathf.Max(0.5f, _targetDistance);
                _targetDistance = Mathf.Clamp(_targetDistance, MinDistance, MaxDistance);
            }
        }

        private float MinDistance => _frameDistance * minZoomFactor;
        private float MaxDistance => _frameDistance * maxZoomFactor;

        /// <summary>
        /// Pull to a fraction of the framed distance, e.g. 1.35 to stand back a little.
        /// Used by the walkthrough so every part marker fits on screen while it explains
        /// them; clamped to the same limits a scroll would obey.
        /// </summary>
        public void ZoomTo(float factorOfFrame)
        {
            _targetDistance = Mathf.Clamp(_frameDistance * factorOfFrame, MinDistance, MaxDistance);
        }

        /// <summary>
        /// Keeps the view inside an arc around the starting angle. The arc is deliberately
        /// lopsided: an asset that is open at one end has an angle past which you are
        /// looking into the hole rather than at the model, and that angle is not the same
        /// on both sides.
        /// </summary>
        private float ClampYaw(float yaw)
        {
            float delta = Mathf.DeltaAngle(_homeYaw, yaw);
            delta = Mathf.Clamp(delta, -yawLimitLeft, yawLimitRight);
            return _homeYaw + delta;
        }

        private float ClampPitch(float pitch)
        {
            float delta = Mathf.DeltaAngle(_homePitch, Normalize(pitch));
            delta = Mathf.Clamp(delta, -pitchLimit, pitchLimit);
            return Normalize(_homePitch + delta);
        }

        /// <summary>Re-centre on the target and derive the distance limits from its size.</summary>
        public void FrameTarget()
        {
            if (target == null) return;

            var renderers = target.GetComponentsInChildren<Renderer>();
            if (renderers.Length == 0) return;

            var bounds = renderers[0].bounds;
            for (int i = 1; i < renderers.Length; i++) bounds.Encapsulate(renderers[i].bounds);

            var cam = GetComponent<Camera>();
            float radius = bounds.extents.magnitude;
            float fit = radius / Mathf.Sin(cam.fieldOfView * 0.5f * Mathf.Deg2Rad);

            // pivot on the bounds centre, not the prefab origin — this is what keeps the
            // glove centred instead of swinging around an off-model pivot point
            targetOffset = bounds.center - target.position;

            _panOffset = Vector3.zero;
            _frameDistance = fit * framePadding;
            _targetDistance = _distance = _frameDistance;

            cam.nearClipPlane = Mathf.Max(0.01f, _frameDistance * 0.01f);
            cam.farClipPlane = Mathf.Max(cam.farClipPlane, _frameDistance * 20f);
        }

        /// <summary>
        /// A short dip toward the asset and back. Called when the outfit changes: the eye
        /// reads the small settle as the object having weight, and it costs nothing.
        /// </summary>
        public void Breathe()
        {
            if (breathAmount <= 0f) return;
            _breathTimer = breathTime;
        }

        private void UpdateBreath()
        {
            if (_breathTimer <= 0f)
            {
                _breath = Mathf.Lerp(_breath, 0f, 1f - Mathf.Exp(-Time.deltaTime / 0.12f));
                return;
            }

            _breathTimer -= Time.deltaTime;
            // one smooth in-and-out over the window
            float t = 1f - Mathf.Clamp01(_breathTimer / Mathf.Max(0.0001f, breathTime));
            _breath = Mathf.Sin(t * Mathf.PI);
        }

        /// <summary>Snap back to the starting angle, pan and framing.</summary>
        public void ResetView()
        {
            _yaw = _homeYaw;
            _pitch = _homePitch;
            _panOffset = Vector3.zero;
            _targetDistance = _frameDistance;
        }

        private static float Normalize(float angle) => angle > 180f ? angle - 360f : angle;
    }
}
