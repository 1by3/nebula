using System.Collections.Generic;
using Nebula;
using UnityEngine;

namespace NebulaSamples
{
    /// <summary>
    /// Spawns a crowd of <see cref="CrowdWalker"/>s on a worker, in two tiers (<c>docs/server-owned-entities.md</c>):
    /// <see cref="ActiveCount"/> active ones updated every <see cref="ActiveUpdateInterval"/> ticks at
    /// <see cref="RelevancePriority.Normal"/>, and the rest updated every <see cref="LightUpdateInterval"/> ticks at
    /// <see cref="LightPriority"/>, falling asleep after <see cref="LightSleepWhenUnobserved"/> seconds with no client
    /// near and waking when one comes. It is a <b>sample</b>: which characters are active, and when one changes tier,
    /// is the game's decision. A game would usually promote the characters near players and demote the rest by setting
    /// the same properties.
    /// <para>
    /// Put it in the scene the workers load. Only the worker that owns the container at <see cref="AreaCentre"/>
    /// spawns, once, when the mesh is ready; walkers spawned into another worker's container are handed to it on the
    /// next tick. It does not persist or re-spawn anything: a worker restart spawns a new crowd.
    /// </para>
    /// </summary>
    public sealed class CrowdSpawner : MonoBehaviour
    {
        [Tooltip("A registered network prefab with a NetworkIdentity, a root NetworkTransform and a CrowdWalker.")]
        public GameObject WalkerPrefab;
        [Tooltip("How many walkers to spawn in all.")]
        public int Count = 1000;
        [Tooltip("How many of them are active; the rest are the lighter tier.")]
        public int ActiveCount = 150;
        [Tooltip("Ticks between two updates of an active walker: 6 is 10 a second.")]
        public int ActiveUpdateInterval = 6;
        [Tooltip("Ticks between two updates of a light walker: 60 is one a second.")]
        public int LightUpdateInterval = 60;
        [Tooltip("How clients rate a light walker's updates. Background: updated only near them.")]
        public RelevancePriority LightPriority = RelevancePriority.Background;
        [Tooltip("Seconds with no client near after which a light walker falls asleep (dormant: not ticked, not sent). It wakes when a client comes near. 0 keeps it awake.")]
        public float LightSleepWhenUnobserved = 10f;
        [Tooltip("The box the crowd spawns and wanders in, in world space: its centre and its size on the ground (x, z).")]
        public Vector3 AreaCentre;
        public Vector2 AreaSize = new Vector2(512f, 512f);
        [Tooltip("Seed for the spawn positions, so two runs place the crowd the same way.")]
        public int Seed = 359;

        /// <summary>The worker this spawner runs on. A real game hands it over from <see cref="NebulaGameMode.OnWorkerStarted"/>; the sample finds it.</summary>
        public NebulaWorker Worker;

        /// <summary>The walkers this spawner spawned, in order: the first <see cref="ActiveCount"/> are the active tier.</summary>
        public readonly List<NetworkIdentity> Spawned = new List<NetworkIdentity>();

        private bool _done;

        private void Update()
        {
            if (_done) return;
            if (Worker == null) Worker = FindAnyObjectByType<NebulaWorker>();
            if (Worker == null || !Worker.IsRegistered || !Worker.MeshReady) return;
            var home = ContainerRegistry.Find(AreaCentre);
            if (home == null) return;
            _done = true;
            if (home.OwnerWorkerId != Worker.WorkerId) return; // another worker spawns the crowd
            SpawnAll();
        }

        /// <summary>Spawn the whole crowd now on <see cref="Worker"/>. Called once by <see cref="Update"/>; public for tests and tools.</summary>
        public void SpawnAll()
        {
            var random = new System.Random(Seed);
            ushort prefabId = NetworkPrefabs.IdOf(WalkerPrefab);
            for (int i = 0; i < Count; i++)
            {
                var position = new Vector3(
                    AreaCentre.x + ((float)random.NextDouble() - 0.5f) * AreaSize.x,
                    AreaCentre.y,
                    AreaCentre.z + ((float)random.NextDouble() - 0.5f) * AreaSize.y);
                var container = ContainerRegistry.Find(position);
                var identity = NetworkPrefabs.Instantiate(prefabId, position, Quaternion.identity, container != null ? container.transform : null);
                Configure(identity, i);
                Worker.SpawnServerDriven(identity, container);
                Spawned.Add(identity);
            }
        }

        /// <summary>
        /// Give walker number <paramref name="index"/> its tier and its area, before it is spawned: the first
        /// <see cref="ActiveCount"/> are active, the rest light.
        /// </summary>
        public void Configure(NetworkIdentity identity, int index)
        {
            bool active = index < ActiveCount;
            identity.UpdateInterval = active ? ActiveUpdateInterval : LightUpdateInterval;
            identity.RelevancePriority = active ? RelevancePriority.Normal : LightPriority;
            identity.SleepWhenUnobserved = active ? 0f : LightSleepWhenUnobserved;
            var walker = identity.GetComponent<CrowdWalker>();
            if (walker != null)
            {
                walker.AreaCentre = AreaCentre;
                walker.AreaSize = AreaSize;
            }
        }
    }
}
