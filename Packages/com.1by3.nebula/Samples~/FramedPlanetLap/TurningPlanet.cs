using Nebula;
using UnityEngine;

namespace NebulaSamples
{
    /// <summary>
    /// A planet: a carrier whose box has its own physics frame and regions of its own, turned slowly about its own up
    /// by its authority, with flat ground leased under it as runtime containers. It is a <b>sample</b>; what matters is
    /// the prefab's shape:
    /// <list type="bullet">
    /// <item>a <see cref="NetworkIdentity"/>, a <see cref="NetworkTransform"/> and a <see cref="Container"/> on the same
    /// root, with <see cref="Container.OwnPhysicsFrame"/> on and <see cref="Container.FrameInterest"/> set to
    /// <see cref="FrameInterestMode.OwnRegions"/>, so what stands on it is bucketed in its own coordinates and its turning
    /// churns no region key (<c>docs/container-tree.md</c> D18);</item>
    /// <item>ground as runtime containers whose parent is the planet's container (<see cref="ContainerPlacement.Child"/>,
    /// <see cref="ContainerAuthority.Leased"/>), each requested by the worker that should simulate it (D20).</item>
    /// </list>
    /// </summary>
    [RequireComponent(typeof(Container))]
    public sealed class TurningPlanet : NetworkBehaviour
    {
        [Tooltip("Turn rate about the planet's own up, degrees per second. 0 holds it still.")]
        public float DegreesPerSecond = 0.5f;
        [Tooltip("Ground chunks along x, each ChunkSize wide, centred on the planet.")]
        public int Chunks = 4;
        [Tooltip("Size of one ground chunk, metres. Its height should be the box's, so a ship is in a chunk up to the box's top.")]
        public Vector3 ChunkSize = new Vector3(2000f, 1000f, 8000f);
        [Tooltip("First runtime id of the ground chunks; chunk i is FirstChunkId + i.")]
        public ulong FirstChunkId = 1000;

        private float _angle;

        /// <summary>Where chunk <paramref name="index"/> is centred in the planet's own coordinates.</summary>
        public Vector3 ChunkCenter(int index)
        {
            var box = GetComponent<Container>();
            return new Vector3((index - (Chunks - 1) * 0.5f) * ChunkSize.x, box.Center.y, 0f);
        }

        /// <summary>
        /// Worker: request the chunks this worker should hold, every <paramref name="workers"/>-th one from
        /// <paramref name="slot"/> (0-based), so a mesh of that many workers deals them alternately. Call it on every
        /// worker once the planet has spawned there.
        /// </summary>
        public void RequestGround(NebulaWorker worker, int slot, int workers)
        {
            var planet = Identity.Carried;
            if (worker == null || planet == null) return;
            for (int i = 0; i < Chunks; i++)
            {
                if (workers > 1 && i % workers != slot) continue;
                worker.RequestRuntimeContainer(FirstChunkId + (ulong)i,
                    ContainerPlacement.Child(planet.ContainerId, ChunkCenter(i), ChunkSize, ContainerAuthority.Leased));
            }
        }

        public override void NetworkTick(uint tick, float deltaTime)
        {
            if (!HasAuthority || DegreesPerSecond == 0f) return;
            // An absolute angle rather than an increment, so a long run does not drift.
            _angle = Mathf.Repeat(_angle + DegreesPerSecond * deltaTime, 360f);
            transform.rotation = Quaternion.Euler(0f, _angle, 0f);
        }

        private void Reset()
        {
            var box = GetComponent<Container>();
            if (box == null) return;
            box.OwnPhysicsFrame = true;
            box.FrameInterest = FrameInterestMode.OwnRegions;
            box.Size = new Vector3(8000f, 1000f, 8000f);
            box.Center = new Vector3(0f, 100f, 0f);
        }
    }
}
