using System;
using System.Collections.Generic;
using UnityEngine;

namespace Nebula
{
    /// <summary>
    /// A physics constraint Nebula cannot simulate symmetrically: a <see cref="Joint"/> or a child
    /// <see cref="ArticulationBody"/> whose two bodies belong to networked entities in different containers, so that
    /// two workers can end up each owning one side. See
    /// <a href="https://nebula.1by3.co/docs/concepts/distributed-physics">Distributed physics</a>.
    /// </summary>
    public readonly struct CrossIslandJoint
    {
        /// <summary>The <see cref="Joint"/> or <see cref="ArticulationBody"/> component that spans the seam.</summary>
        public readonly Component Constraint;
        /// <summary>The entity the constraint component sits on.</summary>
        public readonly NetworkIdentity Owner;
        /// <summary>The entity that owns the body the constraint is connected to.</summary>
        public readonly NetworkIdentity Connected;
        /// <summary>The container <see cref="Owner"/> resolves to, or null for none.</summary>
        public readonly Container OwnerContainer;
        /// <summary>The container <see cref="Connected"/> resolves to, or null for none.</summary>
        public readonly Container ConnectedContainer;

        public CrossIslandJoint(Component constraint, NetworkIdentity owner, NetworkIdentity connected, Container ownerContainer, Container connectedContainer)
        {
            Constraint = constraint;
            Owner = owner;
            Connected = connected;
            OwnerContainer = ownerContainer;
            ConnectedContainer = connectedContainer;
        }

        /// <summary>The id of a container, or <c>"no container"</c> for null: what the diagnostics print.</summary>
        public static string Describe(Container container) => container != null ? container.ContainerId : "no container";

        /// <summary>One line naming the constraint, both entities and both containers.</summary>
        public override string ToString() =>
            $"{Constraint.GetType().Name} on '{Constraint.name}' joins '{Owner.name}' in {Describe(OwnerContainer)} to '{Connected.name}' in {Describe(ConnectedContainer)}";
    }

    /// <summary>
    /// Decides whether two networked bodies are simulated by one authority (the same <em>island</em>) and finds the
    /// joints that are not. Nebula simulates a dynamic body only on the worker that has authority over its entity;
    /// every other worker holds a kinematic ghost one tick behind. A <see cref="Joint"/> or
    /// <see cref="ArticulationBody"/> between entities in different containers can therefore have each of its two
    /// bodies simulated by a different worker, and PhysX on each side solves the constraint against a kinematic
    /// copy. Nebula does not make that symmetric. <c>Nebula &gt; Validate Project</c> and the worker use this class
    /// to warn about such constraints; see
    /// <a href="https://nebula.1by3.co/docs/concepts/distributed-physics">Distributed physics</a>.
    /// </summary>
    public static class PhysicsIslands
    {
        /// <summary>
        /// Extension point for cohesion hints (NEB-223, not yet available): return true when the game guarantees
        /// that the two entities always share an authoritative worker, and their joints are no longer reported.
        /// Null (the default) treats every pair of entities in different containers as separate islands.
        /// </summary>
        public static Func<NetworkIdentity, NetworkIdentity, bool> IsCohesive;

        /// <summary>
        /// Whether the worker warns about a cross-container joint when it spawns or gains authority over an entity.
        /// One warning per entity through <see cref="NebulaLog.Warn"/>; the check costs one component lookup per
        /// spawn and nothing per tick.
        /// </summary>
        public static bool WarnOnAuthority = true;

        private static readonly HashSet<ulong> Warned = new HashSet<ulong>();
        private static readonly List<CrossIslandJoint> Scratch = new List<CrossIslandJoint>();

        internal static void ResetForNewSession() => Warned.Clear();

        /// <summary>
        /// Two containers are one island when they are the same container. Two nulls (no containers at all) are one
        /// island as well; a null against a container is not.
        /// </summary>
        public static bool SameIsland(Container a, Container b) => a == b;

        /// <summary>
        /// Whether one authority simulates both entities: the same entity, a body that is not networked at all
        /// (null), a pair <see cref="IsCohesive"/> vouches for, or two entities in the same container
        /// (<paramref name="resolve"/>, default <see cref="ContainerOf"/>).
        /// </summary>
        public static bool SameIsland(NetworkIdentity a, NetworkIdentity b, Func<NetworkIdentity, Container> resolve = null)
        {
            if (a == null || b == null || a == b) return true;
            // NEB-223: cohesion hints plug in here; a cohesive pair is one island whatever their containers.
            if (IsCohesive != null && IsCohesive(a, b)) return true;
            resolve ??= ContainerOf;
            return SameIsland(resolve(a), resolve(b));
        }

        /// <summary>
        /// The container an entity belongs to: <see cref="NetworkIdentity.Container"/> once it is spawned, otherwise
        /// the nearest <see cref="Container"/> above it in the hierarchy (a carrier's own box is skipped; a carrier
        /// sits in the container around it, not in the one it carries). Null when neither is known.
        /// </summary>
        public static Container ContainerOf(NetworkIdentity identity)
        {
            if (identity == null) return null;
            if (identity.Container != null || identity.IsSpawned) return identity.Container;
            var parent = identity.transform.parent;
            return parent != null ? parent.GetComponentInParent<Container>(true) : null;
        }

        /// <summary>Cheap gate: whether the entity's hierarchy holds any <see cref="Joint"/> or <see cref="ArticulationBody"/>.</summary>
        public static bool HasConstraints(GameObject root) =>
            root != null && (root.GetComponentInChildren<Joint>(true) != null || root.GetComponentInChildren<ArticulationBody>(true) != null);

        /// <summary>
        /// Appends every constraint under <paramref name="root"/> whose two bodies are in different islands. A
        /// constraint is attributed to the nearest <see cref="NetworkIdentity"/> above it, so a constraint on a
        /// nested entity's hierarchy is reported from that entity, not from its carrier. A joint connected to the
        /// world (no connected body) or to a body without a NetworkIdentity is never reported: nothing else
        /// simulates that body. Returns the number of constraints added.
        /// </summary>
        public static int FindCrossIslandJoints(NetworkIdentity root, List<CrossIslandJoint> results, Func<NetworkIdentity, Container> resolve = null)
        {
            if (root == null || results == null) return 0;
            resolve ??= ContainerOf;
            int added = 0;
            foreach (var joint in root.GetComponentsInChildren<Joint>(true))
            {
                if (joint.GetComponentInParent<NetworkIdentity>(true) != root) continue;
                NetworkIdentity other = null;
                if (joint.connectedBody != null) other = joint.connectedBody.GetComponentInParent<NetworkIdentity>(true);
                else if (joint.connectedArticulationBody != null) other = joint.connectedArticulationBody.GetComponentInParent<NetworkIdentity>(true);
                if (other == null || other == root) continue;
                if (SameIsland(root, other, resolve)) continue;
                results.Add(new CrossIslandJoint(joint, root, other, resolve(root), resolve(other)));
                added++;
            }
            foreach (var body in root.GetComponentsInChildren<ArticulationBody>(true))
            {
                if (body.isRoot || body.GetComponentInParent<NetworkIdentity>(true) != root) continue;
                var parent = body.transform.parent;
                var parentBody = parent != null ? parent.GetComponentInParent<ArticulationBody>(true) : null;
                var other = parentBody != null ? parentBody.GetComponentInParent<NetworkIdentity>(true) : null;
                if (other == null || other == root) continue;
                if (SameIsland(root, other, resolve)) continue;
                results.Add(new CrossIslandJoint(body, root, other, resolve(root), resolve(other)));
                added++;
            }
            return added;
        }

        /// <summary>
        /// Worker side: called when an entity is spawned or handed in. Logs one warning for the entity, naming each
        /// cross-container constraint, the first time it finds one; later authority changes of the same entity are
        /// silent. Entities without a joint or articulation cost one component lookup.
        /// </summary>
        public static void CheckOnAuthority(NetworkIdentity identity)
        {
            if (!WarnOnAuthority || identity == null || !HasConstraints(identity.gameObject)) return;
            if (Warned.Contains(identity.NetId)) return;
            Scratch.Clear();
            if (FindCrossIslandJoints(identity, Scratch) == 0) return;
            Warned.Add(identity.NetId);
            var sb = new System.Text.StringBuilder();
            // ToString() explicitly: Append(identity) would bind to Append(bool) through UnityEngine.Object's implicit bool.
            sb.Append(identity.ToString()).Append(": ").Append(Scratch.Count).Append(" joint(s) across containers; PhysX solves each side against a kinematic ghost one tick behind, so the constraint is not symmetric. Keep both bodies in one container or move the seam (https://nebula.1by3.co/docs/concepts/distributed-physics).");
            for (int i = 0; i < Scratch.Count; i++) sb.Append("\n  ").Append(Scratch[i]);
            NebulaLog.Warn(sb.ToString());
        }
    }
}
