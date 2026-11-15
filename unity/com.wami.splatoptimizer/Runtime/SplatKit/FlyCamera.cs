using UnityEngine;
#if SPLATKIT_INPUT_SYSTEM && ENABLE_INPUT_SYSTEM
using UnityEngine.InputSystem;
#endif

namespace SplatKit
{
    /// <summary>Cámara de vuelo de escritorio para recorrer entornos: botón derecho + ratón para mirar,
    /// WASD para moverse, Q/E para bajar/subir, Shift para ir rápido y la rueda cambia la velocidad.</summary>
    [AddComponentMenu("Wami Splat Optimizer/Fly Camera")]
    public class FlyCamera : MonoBehaviour
    {
        public float speed = 1.5f;
        public float lookSensitivity = 0.15f;

        float yaw, pitch;

        void Start()
        {
            var e = transform.eulerAngles;
            yaw = e.y;
            pitch = e.x > 180 ? e.x - 360 : e.x;
        }

        void Update()
        {
#if SPLATKIT_INPUT_SYSTEM && ENABLE_INPUT_SYSTEM
            var kb = Keyboard.current;
            var mouse = Mouse.current;
            if (kb == null || mouse == null) return;

            if (mouse.rightButton.isPressed)
            {
                var d = mouse.delta.ReadValue();
                yaw += d.x * lookSensitivity;
                pitch = Mathf.Clamp(pitch - d.y * lookSensitivity, -89f, 89f);
                transform.rotation = Quaternion.Euler(pitch, yaw, 0);
            }

            var scroll = mouse.scroll.ReadValue().y;
            if (scroll != 0) speed = Mathf.Clamp(speed * (scroll > 0 ? 1.15f : 1 / 1.15f), 0.1f, 20f);

            var move = Vector3.zero;
            if (kb.wKey.isPressed) move += transform.forward;
            if (kb.sKey.isPressed) move -= transform.forward;
            if (kb.dKey.isPressed) move += transform.right;
            if (kb.aKey.isPressed) move -= transform.right;
            if (kb.eKey.isPressed) move += Vector3.up;
            if (kb.qKey.isPressed) move -= Vector3.up;
            var mult = kb.leftShiftKey.isPressed ? 4f : 1f;
            transform.position += move.normalized * speed * mult * Time.deltaTime;
#endif
        }
    }
}
