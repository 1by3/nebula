using UnityEngine;

namespace Nebula.World
{
    /// <summary>
    /// Scene-object form of <see cref="NebulaChunks.Loaded"/>/<see cref="NebulaChunks.Unloading"/>: drop a subclass
    /// in the game scene and override <see cref="OnChunkLoaded"/>. It exists because a chunk generator is usually
    /// authored content — a prefab list, a material, a noise setting — and a <c>MonoBehaviour</c> is where a Unity
    /// developer expects to put those, whereas the static events are what a headless service or a test wants.
    /// Both paths are the same events; use whichever reads better.
    /// <para>
    /// Subscription happens in <c>OnEnable</c>, and <see cref="NebulaChunks.Loaded"/> back-fills, so a component
    /// enabled long after the first chunks arrived still builds all of them.
    /// </para>
    /// </summary>
    public abstract class ChunkContent : MonoBehaviour
    {
        protected virtual void OnEnable()
        {
            NebulaChunks.Loaded += OnChunkLoadedInternal;
            NebulaChunks.Unloading += OnChunkUnloadingInternal;
        }

        protected virtual void OnDisable()
        {
            NebulaChunks.Loaded -= OnChunkLoadedInternal;
            NebulaChunks.Unloading -= OnChunkUnloadingInternal;
        }

        private void OnChunkLoadedInternal(in ChunkContext chunk) => OnChunkLoaded(in chunk);
        private void OnChunkUnloadingInternal(in ChunkContext chunk) => OnChunkUnloading(in chunk);

        /// <summary>Build this chunk's content under <see cref="ChunkContext.Root"/>. Runs on every role.</summary>
        protected abstract void OnChunkLoaded(in ChunkContext chunk);

        /// <summary>The chunk is about to be destroyed, with everything under its root. Only override to release things kept elsewhere.</summary>
        protected virtual void OnChunkUnloading(in ChunkContext chunk) { }
    }
}
