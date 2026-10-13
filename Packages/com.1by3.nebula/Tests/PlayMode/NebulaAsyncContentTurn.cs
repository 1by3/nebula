using UnityEngine;

namespace Nebula.Tests
{
    /// <summary>Test content that turns itself in its Awake, the way a template's content may (see <see cref="InstanceAsyncContentPlayTests"/>).</summary>
    public sealed class NebulaAsyncContentTurn : MonoBehaviour
    {
        private void Awake() => transform.localRotation = Quaternion.Euler(0f, 90f, 0f);
    }
}
