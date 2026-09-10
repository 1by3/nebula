using UnityEngine;

namespace Nebula.World
{
    /// <summary>
    /// Marks the root of a cell scene with its grid coordinate. Optional at runtime (the streamer moves every root
    /// object of a cell scene, whether or not it carries this), but the editor tooling puts one on each cell it
    /// creates so the scene knows which cell it is and can draw its bounds.
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class WorldCell : MonoBehaviour
    {
        public Vector3Int Coord;
        [Tooltip("Copied from the world definition when the cell was created; only used for the editor gizmo.")]
        public Vector3 CellSize = new Vector3(256f, 256f, 256f);

        private void OnDrawGizmos()
        {
            Gizmos.color = new Color(1f, 0.85f, 0.2f, 0.5f);
            Gizmos.DrawWireCube(transform.position, CellSize);
        }
    }
}
