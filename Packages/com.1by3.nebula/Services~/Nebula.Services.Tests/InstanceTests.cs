using System.Collections;
using System.Reflection;
using Nebula;
using Nebula.ServicePrimitives;
using NUnit.Framework;

namespace Nebula.ServiceTests;

[TestFixture]
public class InstanceTests
{
    private NebulaGateway gateway;
    private object worker, outside, inside, other;
    private CaptureTransport transport;
    private const BindingFlags Private = BindingFlags.NonPublic | BindingFlags.Instance;

    private static object Field(object target, string name) => target.GetType().GetField(name, Private | BindingFlags.Public)!.GetValue(target)!;
    private static void Set(object target, string name, object value) => target.GetType().GetField(name, Private | BindingFlags.Public)!.SetValue(target, value);
    private object Call(string name, params object[] args) => typeof(NebulaGateway).GetMethod(name, Private)!.Invoke(gateway, args)!;
    private static object Nested(string name) => Activator.CreateInstance(typeof(NebulaGateway).GetNestedType(name, BindingFlags.NonPublic)!, true)!;

    [SetUp]
    public void Setup()
    {
        typeof(ContainerRegistry).GetMethod("Load", BindingFlags.Static | BindingFlags.NonPublic)!.Invoke(null, new object[] { new ServiceManifest
        { Containers = new() { new Container { ContainerId = "public", Index = 0, Size = new(100,100,100) } } } });
        ContainerRegistry.SyncRuntime(new[] { Lease(11, 101), Lease(22, 202) });
        gateway = new NebulaGateway();
        typeof(NebulaGateway).GetProperty("Config")!.SetValue(gateway, new NebulaConfig());
        transport = new CaptureTransport(); Set(gateway, "_transport", transport);
        worker = Nested("WorkerConn"); Set(worker, "Index", (ushort)1);
        outside = Client(1); inside = Client(2); other = Client(3);
        Spawn(1, 1, new ContainerRef(0));
        Spawn(2, 2, ContainerRef.Runtime(11));
        Spawn(3, 3, ContainerRef.Runtime(22));
        Flush(); transport.Messages.Clear();
    }

    private static LeaseInfo Lease(ulong id, ulong scope) => new() { ContainerId = "rt_" + id, HasBounds = true,
        BoundsSize = new(10,10,10), Instance = new() { InstanceId = scope, ObservePublic = true, ObservationSize = new(30,30,30) } };
    private object Client(uint id)
    {
        var client = Nested("ClientConn"); Set(client,"ClientId",id); Set(client,"PeerId",(int)id); Set(client,"Welcomed",true);
        ((IDictionary)Field(gateway,"_clientsById")).Add(id,client);
        return client;
    }
    private void Spawn(ulong id, uint client, ContainerRef container, uint epoch = 1) => Call("OnEntitySpawn", worker,
        new EntitySpawnMsg { NetId=id, OwnerClientId=client, Container=container, Epoch=epoch, LocalRotation=Quaternion.identity, LocalScale=Vector3.one });
    private static HashSet<ulong> Visible(object client) => (HashSet<ulong>)Field(client,"Visible");
    private void Flush() { Call("FlushReliable",outside); Call("FlushReliable",inside); Call("FlushReliable",other); }

    [Test]
    public void PrivateOccupantsSeePublicButNotOtherInstances()
    {
        Assert.That(Visible(outside), Is.EquivalentTo(new ulong[]{1}));
        Assert.That(Visible(inside), Is.EquivalentTo(new ulong[]{1,2}));
        Assert.That(Visible(other), Is.EquivalentTo(new ulong[]{1,3}));
        var late = Client(4);
        Call("ReconcileView", late);
        Assert.That(Visible(late), Is.EquivalentTo(new ulong[]{1}), "a client without a pawn cannot see private entities");
    }

    [Test]
    public void CrossingKeepsHallwayReplicaAndRemovesPrivateVisibilityOnExit()
    {
        Spawn(2,2,new ContainerRef(0),2);
        Assert.That(Visible(outside), Does.Contain(2UL));
        Spawn(2,2,ContainerRef.Runtime(11),3);
        Assert.That(Visible(outside), Does.Not.Contain(2UL));
        Assert.That(Visible(inside), Does.Contain(1UL));
        Assert.That(Visible(other), Does.Not.Contain(2UL));
    }

    [Test]
    public void VariablesNeverReachObserversOutsideTheInstance()
    {
        Call("OnEntityVars",worker,new EntityVarsMsg {NetId=2,Epoch=1,Vars=new byte[]{1,2,3}},new NetworkReader());
        Flush();
        Assert.That(transport.Messages.Select(m=>m.Peer), Is.EquivalentTo(new[]{2}));
    }

    [Test]
    public void UnknownRuntimeContainerFailsClosed()
    {
        Spawn(9,0,ContainerRef.Runtime(999));
        Assert.That(Visible(outside), Does.Not.Contain(9UL));
        Assert.That(Visible(inside), Does.Not.Contain(9UL));
    }

    [Test]
    public void RuntimeAdjacencyDoesNotCrossScopes()
    {
        Assert.That(ContainerRegistry.Resolve(ContainerRef.Runtime(11)).Neighbors, Is.Empty);
        Assert.That(ContainerRegistry.Resolve(new ContainerRef(0)).Neighbors, Is.Empty);
    }

    [Test]
    public void OwnershipMessagePreservesInstanceMetadata()
    {
        var info=Lease(11,ulong.MaxValue).Instance;
        info.ContentResource="Instances/Room";
        var writer=new NetworkWriter();
        ContainerOwnershipMsg.Write(writer,new[]{new ContainerOwnershipEntry {ContainerId="rt_11",Instance=info,HasBounds=true,BoundsSize=new(10,10,10)}});
        var reader=new NetworkReader(writer.ToArray()); reader.ReadByte();
        var roundtrip=ContainerOwnershipMsg.Read(reader)[0];
        Assert.That(roundtrip.Instance.InstanceId,Is.EqualTo(ulong.MaxValue));
        Assert.That(roundtrip.Instance.ContentResource,Is.EqualTo("Instances/Room"));
        Assert.That(roundtrip.Instance.ObservePublic,Is.True);
        Assert.That(roundtrip.BoundsSize.x,Is.EqualTo(10));
    }

    private sealed class CaptureTransport : ITransport
    {
        public readonly List<(int Peer, byte[] Data)> Messages=new();
        public string Name=>"test"; public bool IsRunning=>true; public int LocalPort=>0;
        public void Send(int peerId,Delivery delivery,ArraySegment<byte> payload)=>Messages.Add((peerId,payload.ToArray()));
        public void Dispose() {} public void Listen(int port) {} public void StartClient() {} public int Connect(string host,int port)=>0;
        public void Disconnect(int peerId) {} public bool IsConnected(int peerId)=>true; public int RoundTripMs(int peerId)=>0;
        public void Poll(Action<TransportEvent> handler) {} public void Flush() {} public void Stop() {}
    }
}
