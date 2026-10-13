using System.Collections;
using System.Collections.Generic;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

namespace Nebula.Tests
{
    /// <summary>
    /// Conformance scenario 73 (<c>docs/portals.md</c> D35, D36, R3) with real physics: the street is the first worker's and the hall the second's, and a
    /// member's pawn (a <see cref="CharacterController"/>) and a thrown server-owned crate go through an open link into the hall. Each commits on the tick its
    /// root passes the plane and is handed to the second worker on that same tick, nothing holds at the face and nothing is corrected by more than 0.1 m
    /// or turned away from the speed it had; the second worker holds each as a body again, with its collider back, in the hall's scene.
    /// </summary>
    [Category("Conformance")]
    public sealed class PortalSplitCommitPlayTests
    {
        private PortalPhysicsRig _rig;
        private PortalHarness H => _rig.H;
        private readonly List<PortalRefusal> _refused = new List<PortalRefusal>();
        private ushort _propPrefab;

        [SetUp]
        public void SetUp()
        {
            _rig = new PortalPhysicsRig(2);
            H.HallOnSecondWorker = true;
            _refused.Clear();
            H.Door.Refused += (entity, link, why) => _refused.Add(why);
            // A prop as a game builds one: a rigidbody that hands its momentum over with its authority.
            var prop = new GameObject("prop-prefab");
            prop.AddComponent<NetworkIdentity>();
            var box = prop.AddComponent<BoxCollider>();
            box.size = Vector3.one;
            box.sharedMaterial = PortalPhysicsRig.Slippery();
            var body = prop.AddComponent<Rigidbody>();
            body.mass = 1f;
            body.linearDamping = 0f;
            prop.AddComponent<NetworkTransform>();
            prop.AddComponent<NetworkRigidbody>();
            _propPrefab = H.Mesh.RegisterPrefab(prop);
        }

        [UnityTearDown]
        public IEnumerator TearDown()
        {
            _rig.Dispose();
            yield return null;
            yield return null;
        }

        private static Vector3 Street(float x, float y, float z) => new Vector3(20f + x, y, 20f + z);

        private NetworkIdentity Authority(ulong netId)
        {
            var near = H.W.Find(netId);
            return near != null && near.HasAuthority ? near : H.Other.Find(netId);
        }

        [UnityTest]
        public IEnumerator AMembersPawnAndAThrownCrateCrossIntoTheOtherWorkersRoomThroughOneHandoverEachWithNoHoldAndNoCorrection()
        {
            var link = _rig.OpenDoor();
            Assert.That(H.Hall.OwnerWorkerId, Is.EqualTo(H.Other.Id));
            Assert.That(NebulaDiagnostics.SplitPortalLinks, Is.EqualTo(1));
            var walker = _rig.Walker(0f, -4f, PortalHarness.Client);
            var crate = H.W.SpawnServerDriven(_propPrefab, H.Street, Street(1.2f, 0.5f, -4f), Quaternion.identity);
            crate.GetComponent<NetworkRigidbody>().SetVelocity(new Vector3(0f, 0f, 3f));
            ulong walkerId = walker.NetId, crateId = crate.NetId;
            _rig.Steps(3);

            int transfers = H.Mesh.DeliveredOf(MsgId.AuthorityTransfer, H.Other.Id).Count;
            int crossings = NebulaDiagnostics.CrossWorkerPortalCrossings;
            int refusals = NebulaDiagnostics.PortalRefusals;
            var last = new Dictionary<ulong, Vector3> { { walkerId, Authority(walkerId).transform.position }, { crateId, Authority(crateId).transform.position } };
            var crossedAt = new Dictionary<ulong, int>();
            float worstWalker = 0f, worstCrate = 0f, backwards = 0f;
            float walkSpeed = 2f;
            for (int i = 0; i < 400 && crossedAt.Count < 2; i++)
            {
                PortalPhysicsRig.Walk(Authority(walkerId), walkSpeed);
                float crateSpeed = PortalPhysicsRig.BodyOf(Authority(crateId)).linearVelocity.z;
                _rig.Step();
                var w = Authority(walkerId);
                var c = Authority(crateId);
                // What it moved by this tick against what it was driven to: any hold, snap or correction at the face shows up as the difference.
                worstWalker = Mathf.Max(worstWalker, Mathf.Abs((w.transform.position.z - last[walkerId].z) - walkSpeed * PortalPhysicsRig.Dt));
                worstCrate = Mathf.Max(worstCrate, Mathf.Abs((c.transform.position.z - last[crateId].z) - crateSpeed * PortalPhysicsRig.Dt));
                backwards = Mathf.Max(backwards, last[walkerId].z - w.transform.position.z, last[crateId].z - c.transform.position.z);
                last[walkerId] = w.transform.position;
                last[crateId] = c.transform.position;
                // The two workers share this process's physics scenes, so the copy the first worker keeps of something it handed away stands in the hall's scene beside
                // the real one; a live mesh keeps it in a process of its own. It is switched off, as the scene it would be in is not the hall's there.
                foreach (var id in crossedAt.Keys)
                    foreach (var collider in H.W.Find(id).GetComponentsInChildren<Collider>(true)) collider.enabled = false;
                foreach (var e in new[] { w, c })
                    if (e.InstanceId == PortalHarness.Private && !crossedAt.ContainsKey(e.NetId))
                    {
                        crossedAt[e.NetId] = i;
                        Assert.That(ReferenceEquals(e, H.Other.Find(e.NetId)), Is.True, "authority had already followed the container to the worker that owns the hall");
                        Assert.That(e.HasAuthority, Is.True);
                        Assert.That(e.Container, Is.SameAs(H.Hall));
                    }
            }

            Assert.That(crossedAt.Count, Is.EqualTo(2), "both went through");
            Assert.That(worstWalker, Is.LessThanOrEqualTo(0.1f), "the pawn was not corrected by more than 0.1 m on any tick");
            Assert.That(worstCrate, Is.LessThanOrEqualTo(0.1f), "nor the crate");
            Assert.That(backwards, Is.LessThan(0.01f), "and neither was ever put back: nothing holds at the face");
            Assert.That(H.Mesh.DeliveredOf(MsgId.AuthorityTransfer, H.Other.Id).Count - transfers, Is.EqualTo(2), "one handover each");
            Assert.That(NebulaDiagnostics.CrossWorkerPortalCrossings - crossings, Is.EqualTo(2));
            Assert.That(NebulaDiagnostics.PortalRefusals, Is.EqualTo(refusals));
            Assert.That(_refused, Is.Empty);
            _rig.Steps(20);

            var farCrate = H.Other.Find(crateId);
            Assert.That(farCrate.HasAuthority, Is.True);
            Assert.That(farCrate.IsPortalGhost, Is.False);
            Assert.That(farCrate.GetComponent<BoxCollider>().enabled, Is.True, "its collider is back: a body on the worker that owns the room");
            Assert.That(farCrate.gameObject.scene, Is.EqualTo(_rig.HallScene));
            Assert.That(PortalPhysicsRig.BodyOf(farCrate).linearVelocity.z, Is.EqualTo(3f).Within(0.3f), "and it carries the speed it was thrown with");
            Assert.That(H.Other.Find(walkerId).HasAuthority, Is.True);
            Assert.That(link.State, Is.EqualTo(PortalState.Open));
            yield break;
        }

        [UnityTest]
        public IEnumerator WhileTheFarSideIsLeasedToAWorkerThatHasNotAnsweredTheCrateMeetsTheFaceAndTheReasonIsReported()
        {
            H.Door.PartyOf = entity => PortalHarness.Owner;
            var peer = H.W.PeersById[H.Other.Id];
            peer.GetType().GetField("HelloReceived").SetValue(peer, false);
            var link = H.Door.Open(PortalHarness.Owner, new[] { PortalHarness.Client });
            _rig.Steps(4);
            Assert.That(link.State, Is.EqualTo(PortalState.Preparing));
            Assert.That(link.Wait, Is.EqualTo(PortalWait.FarWorkerHandshake));

            var crate = _rig.Crate(Street(0f, 0.5f, -3f), PortalPhysicsRig.Slippery());
            PortalPhysicsRig.BodyOf(crate).linearVelocity = new Vector3(0f, 0f, 2f);
            _rig.Steps(240);
            Assert.That(crate.InstanceId, Is.EqualTo(0UL), "stopped at the face, not held in a preparation");
            Assert.That(_refused.Count, Is.GreaterThan(0));
            Assert.That(_refused[0], Is.EqualTo(PortalRefusal.LinkNotOpen), "refused with the reason, and the link's own wait says what it waits for");
            Assert.That(link.Wait, Is.EqualTo(PortalWait.FarWorkerHandshake));
            yield break;
        }
    }
}
