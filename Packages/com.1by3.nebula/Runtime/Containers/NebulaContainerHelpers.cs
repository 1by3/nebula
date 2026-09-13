using UnityEngine;

namespace Nebula
{
    /// <summary>Geometry helpers for placing things inside a <see cref="Container"/>.</summary>
    public static class NebulaContainerHelpers
    {
        /// <summary>A convenient uniform spawn point inside a container's floor area.</summary>
        public static Vector3 RandomPointIn(Container container, float margin = 2f, float y = 0f)
        {
            var half = container.Size * 0.5f;
            float x = Random.Range(-half.x + margin, half.x - margin);
            float z = Random.Range(-half.z + margin, half.z - margin);
            var local = container.Center + new Vector3(x, 0f, z);
            var world = container.ToWorld(local);
            world.y = y;
            return world;
        }
    }
}
