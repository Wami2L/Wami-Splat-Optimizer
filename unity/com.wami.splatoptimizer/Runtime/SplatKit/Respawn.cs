// Wami Splat Optimizer — puts an object back where it started if it falls through the splat collisions.
// SPDX-License-Identifier: MIT

using UnityEngine;
using UnityEngine.Serialization;

namespace SplatKit
{
    /// <summary>
    /// Devuelve el objeto a donde empezó si cae por debajo de <see cref="minHeight"/> (las colisiones de un entorno
    /// 3DGS salen de los splats y pueden tener huecos). Sirve para el rig VR (CharacterController) y para Rigidbody.
    /// Puts the object back where it started when it falls below <see cref="minHeight"/>.
    /// </summary>
    [AddComponentMenu("Wami Splat Optimizer/Respawn")]
    public class Respawn : MonoBehaviour
    {
        [FormerlySerializedAs("alturaMinima")] public float minHeight = -5f;

        Vector3 m_pos;
        Quaternion m_rot;

        void Start()
        {
            m_pos = transform.position;
            m_rot = transform.rotation;
        }

        void Update()
        {
            if (transform.position.y >= minHeight) return;
            // El CharacterController pisa la posición que se le ponga desde fuera mientras está activo.
            var cc = GetComponent<CharacterController>();
            if (cc) cc.enabled = false;
            transform.SetPositionAndRotation(m_pos, m_rot);
            if (cc) cc.enabled = true;
            if (TryGetComponent<Rigidbody>(out var rb) && !rb.isKinematic)
            {
                rb.linearVelocity = Vector3.zero;
                rb.angularVelocity = Vector3.zero;
            }
        }
    }
}
