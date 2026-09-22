using System.Reflection;
using Nebula;
using Nebula.ServicePrimitives;
using NUnit.Framework;

namespace Nebula.ServiceTests;

[TestFixture]
public class WorkerRecoveryTests
{
    [SetUp]
    public void SetUp() => ContainerRegistry.Load(new ServiceManifest());

    [TearDown]
    public void TearDown() => ContainerRegistry.Load(new ServiceManifest());

    [Test]
    public void RuntimeObjectSurvivesDeferredRecoveryButNotSubsequentRetirement()
    {
        using var plane = new LocalControlPlane();
        plane.Connect();
        var registration = new WorkerRegistration { WorkerId = "w1", WorkerIndex = 1 };
        registration.Register(plane);
        plane.EnsureRuntimeContainer("rt_42", new Bounds(new Vector3(12, 0, 0), new Vector3(8, 8, 8)), "w1");
        ContainerRegistry.SyncRuntime(plane.Leases);
        ContainerRegistry.ApplyLease("rt_42", "w1", 1, 1, LeaseState.Active);
        var original = ContainerRegistry.FindById("rt_42");
        int unloaded = 0;
        void OnUnregister(Container c) { if (c == original) unloaded++; }
        ContainerRegistry.RuntimeUnregistering += OnUnregister;
        try
        {
            plane.ResetControlPlane();
            var remote = DispatchProxy.Create<IControlPlane, DeferredWrites>();
            var deferred = (DeferredWrites)(object)remote;
            deferred.Plane = plane;

            Assert.That(registration.ReclaimContainers(remote), Is.EqualTo(1));
            Assert.That(plane.Leases, Is.Empty, "the mirror must still be empty while its write is queued");
            registration.SyncRuntime(remote);
            plane.SetSetting("unrelated", "change");
            Assert.That(registration.ReclaimContainers(remote), Is.Zero);
            registration.SyncRuntime(remote);
            Assert.That(ContainerRegistry.FindById("rt_42"), Is.SameAs(original));
            Assert.That(unloaded, Is.Zero, "recovery must not invoke scene unload or occupant evacuation");

            deferred.Flush();
            registration.ReclaimContainers(remote);
            registration.SyncRuntime(remote);
            Assert.That(ContainerRegistry.FindById("rt_42"), Is.SameAs(original));
            Assert.That(plane.FindLease("rt_42").WorkerId, Is.EqualTo("w1"));

            plane.RemoveContainer("rt_42");
            Assert.That(registration.ReclaimContainers(remote), Is.Zero, "same-document retirement must not be reclaimed");
            registration.SyncRuntime(remote);
            Assert.That(ContainerRegistry.FindById("rt_42"), Is.Null);
            Assert.That(unloaded, Is.EqualTo(1));
        }
        finally { ContainerRegistry.RuntimeUnregistering -= OnUnregister; }
    }

    // Models the write/read delay of RemoteControlPlane without a race against an HTTP sender thread.
    public class DeferredWrites : DispatchProxy
    {
        public LocalControlPlane Plane;
        private readonly Queue<Action> writes = new();
        protected override object Invoke(MethodInfo targetMethod, object[] args)
        {
            if (targetMethod.Name == nameof(IControlPlane.EnsureRuntimeContainer))
            {
                writes.Enqueue(() => targetMethod.Invoke(Plane, args));
                return null;
            }
            return targetMethod.Invoke(Plane, args);
        }
        public void Flush() { while (writes.TryDequeue(out var write)) write(); }
    }
}
