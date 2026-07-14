// WebGpuWater - free-fly camera (Unity 6 / URP port).
// WASD to move on the view plane, Q/E down/up, hold RIGHT mouse to look, hold Shift to boost.
// An alternative to OrbitCamera for exploring large bodies; supports both the new Input System and
// the legacy Input manager, mirroring OrbitCamera's input abstraction.
using UnityEngine;
#if ENABLE_INPUT_SYSTEM
using UnityEngine.InputSystem;
#endif

namespace AbstractOcclusion.WebGpuWater
{
    [RequireComponent(typeof(Camera))]
    public sealed class FlyCamera : MonoBehaviour
    {
        [Header("Move")]
        [Tooltip("Metres per second at normal speed.")]
        [SerializeField] internal float moveSpeed = 6f;
        [Tooltip("Speed multiplier while Shift is held.")]
        [SerializeField] internal float boostMultiplier = 3f;

        [Header("Look")]
        [Tooltip("Degrees of rotation per pixel of mouse movement while the right button is held.")]
        [SerializeField] internal float lookSensitivity = 0.1f;

        const float MinPitch = -89.99f;
        const float MaxPitch = 89.99f;

        float _yaw;
        float _pitch;

        void OnEnable()
        {
            Vector3 euler = transform.eulerAngles;
            _pitch = NormalizePitch(euler.x);
            _yaw = euler.y;
        }

        void Update()
        {
            if (LookHeld()) ApplyLook(MouseDelta());
            ApplyMove(MoveInput(), BoostHeld());
        }

        void ApplyLook(Vector2 mouseDelta)
        {
            _yaw += mouseDelta.x * lookSensitivity;
            _pitch = Mathf.Clamp(_pitch - mouseDelta.y * lookSensitivity, MinPitch, MaxPitch);
            transform.rotation = Quaternion.Euler(_pitch, _yaw, 0f);
        }

        // moveInput: x = right/left, y = up/down (Q/E), z = forward/back, in local axes.
        void ApplyMove(Vector3 moveInput, bool boost)
        {
            if (moveInput == Vector3.zero) return;
            Vector3 local = transform.right * moveInput.x + Vector3.up * moveInput.y + transform.forward * moveInput.z;
            float speed = moveSpeed * (boost ? boostMultiplier : 1f);
            // Unscaled: a free-fly camera should keep its speed regardless of any time scaling or pause
            // (a per-body water timeScale, a paused game, etc.), so movement never "loses speed".
            transform.position += Vector3.ClampMagnitude(local, 1f) * (speed * Time.unscaledDeltaTime);
        }

        // Wrap Unity's 0..360 euler.x into a signed pitch so the clamp is symmetric.
        static float NormalizePitch(float eulerX) => eulerX > 180f ? eulerX - 360f : eulerX;

        // ---- input abstraction (both input backends) ----

        static Vector3 MoveInput()
        {
            Vector3 m = Vector3.zero;
#if ENABLE_INPUT_SYSTEM
            var k = Keyboard.current;
            if (k == null) return m;
            if (k.wKey.isPressed) m.z += 1f;
            if (k.sKey.isPressed) m.z -= 1f;
            if (k.dKey.isPressed) m.x += 1f;
            if (k.aKey.isPressed) m.x -= 1f;
            if (k.eKey.isPressed) m.y += 1f;
            if (k.qKey.isPressed) m.y -= 1f;
#else
            if (Input.GetKey(KeyCode.W)) m.z += 1f;
            if (Input.GetKey(KeyCode.S)) m.z -= 1f;
            if (Input.GetKey(KeyCode.D)) m.x += 1f;
            if (Input.GetKey(KeyCode.A)) m.x -= 1f;
            if (Input.GetKey(KeyCode.E)) m.y += 1f;
            if (Input.GetKey(KeyCode.Q)) m.y -= 1f;
#endif
            return m;
        }

        static bool BoostHeld()
        {
#if ENABLE_INPUT_SYSTEM
            return Keyboard.current != null && Keyboard.current.leftShiftKey.isPressed;
#else
            return Input.GetKey(KeyCode.LeftShift);
#endif
        }

        static bool LookHeld()
        {
#if ENABLE_INPUT_SYSTEM
            return Mouse.current != null && Mouse.current.rightButton.isPressed;
#else
            return Input.GetMouseButton(1);
#endif
        }

        static Vector2 MouseDelta()
        {
#if ENABLE_INPUT_SYSTEM
            return Mouse.current != null ? Mouse.current.delta.ReadValue() : Vector2.zero;
#else
            return new Vector2(Input.GetAxisRaw("Mouse X"), Input.GetAxisRaw("Mouse Y"));
#endif
        }
    }
}
