using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace Nebula
{
    /// <summary>A collider Nebula made for a portal: a body's kinematic copy, a copy of static geometry, or an aperture collider (<c>docs/portals.md</c> D26 to D28).</summary>
    internal sealed class ProxyBody
    {
        public GameObject Go;
        public Transform T;
        public Rigidbody Body;
        public Collider Collider;
        public PortalProxy Marker;
        public PortalShapeKey Key;
        public Scene Scene;
        /// <summary>It has been placed since it was last taken from the pool: the next move is a move, not a teleport.</summary>
        public bool Posed;

        /// <summary>
        /// Put the proxy at a pose. The first time it is a teleport (no velocity, so nothing it overlaps is pushed by a jump from a stale pose). After that, in a
        /// scene this process steps, it is moved with <see cref="Rigidbody.MovePosition"/> so contacts get its velocity (<c>docs/portals.md</c> D26).
        /// </summary>
        public void Place(Vector3 position, Quaternion rotation, bool stepped)
        {
            if (!Posed)
            {
                T.SetPositionAndRotation(position, rotation);
                if (!Go.activeSelf) Go.SetActive(true);
                Posed = true;
            }
            else if (stepped && Body != null)
            {
                Body.MovePosition(position);
                Body.MoveRotation(rotation);
            }
            else T.SetPositionAndRotation(position, rotation);
        }

        public void Destroy()
        {
            if (Go == null) return;
            if (Application.isPlaying) UnityEngine.Object.Destroy(Go);
            else UnityEngine.Object.DestroyImmediate(Go);
            Go = null;
        }
    }

    /// <summary>One body in a band, with the proxies built for it (<c>docs/portals.md</c> D26) and the aperture collider pairs it ignores (D28).</summary>
    internal sealed class Tracked
    {
        public NetworkIdentity Entity;
        /// <summary>The entity is in the near scope.</summary>
        public bool EntityNear;
        public uint Stamp;
        public uint NextAdmitCheck;
        public readonly List<Part> Parts = new List<Part>(2);
        /// <summary>The entity's colliders the aperture collider is ignored for, and the aperture collider they ignore.</summary>
        public readonly List<Collider> Ignored = new List<Collider>(2);
        public Collider IgnoredAperture;
        public bool Admitted;
        public PortalRefusal Why;
        public PortalBodies.Info Info;
    }

    /// <summary>One collider of a tracked body and the proxy standing for it on the other side.</summary>
    internal sealed class Part
    {
        public Collider Source;
        public ProxyBody Proxy;
        public Vector3 LastPosition;
        public Quaternion LastRotation;
        public bool HasLast;
        /// <summary>Where the proxy's scene had the face it was posed about (a framed side's floating origin moved: the proxy is posed again).</summary>
        public PortalFaceFrame PosedAbout;
    }

    /// <summary>A pair of bodies, one native to each side, that both have proxies and touch: where it collides, and the pairs the other side ignores (<see cref="PortalPairRule"/>).</summary>
    internal sealed class PairState
    {
        public bool InNear;
        public uint Stamp;
        public int Signature;
        public readonly List<(Collider a, Collider b)> Ignored = new List<(Collider, Collider)>();
    }

    /// <summary>The physics of one link on one process (<c>docs/portals.md</c> §6): the aperture colliders, the throat, the bodies in the band and their proxies.</summary>
    internal sealed class LinkRig
    {
        public ulong LinkId, PortalId;
        public uint Generation;
        /// <summary>The portal and link, on the worker that drives them; null on a client and on a worker that only holds the far side of a link another drives.</summary>
        public Portal Portal;
        public PortalLink Link;
        public ulong NearScope, FarScope;
        public PortalFace NearFace, FarFace;
        public PortalAperture Aperture;
        public float BandMargin, BandDepth;
        public PortalState State;
        public bool ObservationOnly;
        /// <summary>This process builds the near side's physics: it holds the near container and its scene.</summary>
        public bool HostsNear, HostsFar;
        public Scene NearScene, FarScene;
        /// <summary>The physics frame each side simulates in (a planet that is a carrier), or null when it is its scope's own space (<c>docs/portals.md</c> D26).</summary>
        public Container NearSpace, FarSpace;
        public PortalFaceFrame NearFrame, FarFrame;
        public Quaternion TurnNearToFar = Quaternion.identity, TurnFarToNear = Quaternion.identity;
        public PortalBand Band;
        public ProxyBody NearAperture, FarAperture;
        public readonly List<ProxyBody> NearThroat = new List<ProxyBody>();
        public readonly List<ProxyBody> FarThroat = new List<ProxyBody>();
        public long BuiltGeneration = -1;
        public bool ThroatActive;
        public readonly Dictionary<ulong, Tracked> Tracked = new Dictionary<ulong, Tracked>();
        public readonly Dictionary<(ulong near, ulong far), PairState> Pairs = new Dictionary<(ulong, ulong), PairState>();
        public uint Stamp;
        /// <summary>On a client: the pawn is a member of the link, so its own body is admitted once the link is open.</summary>
        public bool ClientMember;
        public int LiveProxies;
        /// <summary>Where the faces stood, in the scenes, when each side's throat was copied.</summary>
        public PortalFaceFrame ThroatNearFrame, ThroatFarFrame;
        /// <summary>The worker that drives the link (owns the near container), and the workers that own the far containers its band touches (<c>docs/portals.md</c> D30).</summary>
        public string NearOwner = "";
        public readonly List<string> FarOwners = new List<string>(2);

        public bool IsLive => State == PortalState.Open || State == PortalState.Closing;
    }

    /// <summary>
    /// Everything a process builds in physics scenes for its portals (<c>docs/portals.md</c> §6): per link, a thin aperture collider in each side's scene
    /// (D28), the other side's static geometry copied into the throat (D27), and a kinematic proxy in the other side's scene for every body in the
    /// band (D26). A worker runs one <see cref="Step"/> a tick before it steps its scenes; a client runs its own for its pawn's scope. State belongs to
    /// the instance, never to the process, because the in-process mesh the tests run holds several workers in one.
    /// </summary>
    internal sealed partial class PortalPhysics
    {
        /// <summary>How far past a band a body must be before its proxies go: the hysteresis that keeps a body at the edge from building and dropping proxies every tick.</summary>
        public const float BandHysteresis = 0.25f;
        /// <summary>The aperture collider's thickness, centred on the face's plane, in meters.</summary>
        public const float ApertureThickness = 2f * Portal.CrossDepth;
        /// <summary>The most static copies a link's throat holds on either side. Past it the rest are left out and said so once.</summary>
        public const int MaxThroatColliders = 128;
        /// <summary>How many ticks pass between two looks at whether a body in a band is admitted.</summary>
        public const uint AdmitCheckTicks = 6;

        private readonly NebulaWorker _worker;
        private readonly bool _isClient;
        private readonly Dictionary<ulong, LinkRig> _rigs = new Dictionary<ulong, LinkRig>();
        private readonly Dictionary<ulong, List<LinkRig>> _byScope = new Dictionary<ulong, List<LinkRig>>();
        private readonly List<List<LinkRig>> _spareLists = new List<List<LinkRig>>();
        private readonly List<ulong> _gone = new List<ulong>();
        private readonly PortalShapePool<ProxyBody> _pool = new PortalShapePool<ProxyBody>();
        private readonly List<int> _eligible = new List<int>(8);
        private readonly List<ProxyBody> _drain = new List<ProxyBody>();
        private readonly Dictionary<ulong, ProxyBody> _nearApertures = new Dictionary<ulong, ProxyBody>();
        private readonly List<ulong> _apertureGone = new List<ulong>();
        private readonly HashSet<ulong> _apertureSeen = new HashSet<ulong>();
        private readonly HashSet<string> _warned = new HashSet<string>();
        private uint _tick;

        /// <summary>What a process knows about the bodies near its faces.</summary>
        public readonly PortalBodies Bodies = new PortalBodies();

        /// <summary>The proxies that were built, not taken from the pool (tests, and a game watching its allocations).</summary>
        public int ProxiesBuilt => _pool.Built;
        /// <summary>The proxies taken from the pool.</summary>
        public int ProxiesReused => _pool.Reused;
        /// <summary>Proxies and static copies standing in scenes right now.</summary>
        public int LiveProxies { get; private set; }

        public PortalPhysics(NebulaWorker worker)
        {
            _worker = worker;
            _isClient = worker == null;
        }

        /// <summary>Whether this process builds portal physics at all: only in play mode, where physics scenes exist.</summary>
        public static bool Enabled => Application.isPlaying;

        /// <summary>Whether this instance holds anything in a scene.</summary>
        public bool HasRigs => _rigs.Count > 0 || _nearApertures.Count > 0;

        private bool Stepped => !_isClient;

        private void WarnOnce(string key, string message)
        {
            if (_warned.Add(key)) NebulaLog.Warn(message);
        }

        // ------------------------------------------------------------------------------------------ the rigs

        internal bool TryGetRig(ulong linkId, out LinkRig rig) => _rigs.TryGetValue(linkId, out rig);

        internal IEnumerable<LinkRig> Rigs => _rigs.Values;

        /// <summary>The rig of a link, made when it is first asked for.</summary>
        private LinkRig RigFor(ulong linkId)
        {
            if (!_rigs.TryGetValue(linkId, out var rig)) _rigs.Add(linkId, rig = new LinkRig { LinkId = linkId, BuiltGeneration = -1 });
            return rig;
        }

        /// <summary>Set a rig's geometry from the face frames and refresh the scene bindings. Returns false when this process hosts neither side.</summary>
        private bool Bind(LinkRig rig)
        {
            rig.Band = new PortalBand(rig.Aperture, rig.BandMargin, rig.BandDepth);
            // A body of one side is posed on the other by the same offset in face axes; the frames of both sides are needed for a body to be tested on its
            // own side and posed on the other's, so a side this process does not host is framed anyway. A side that simulates in a physics frame has its face
            // in that frame's coordinates, composed in double (PortalSpaces.FaceFrame), and the turn between the sides is between the faces as the scenes
            // hold them: a planet's frame is turned in its scope, and its door is turned by that as well.
            rig.NearFrame = PortalSpaces.FaceFrame(rig.NearFace, rig.NearSpace);
            rig.FarFrame = PortalSpaces.FaceFrame(rig.FarFace, rig.FarSpace);
            if (rig.NearFrame.Valid && rig.FarFrame.Valid)
            {
                rig.TurnNearToFar = rig.FarFrame.Rotation * Quaternion.Inverse(rig.NearFrame.Rotation);
                rig.TurnFarToNear = Quaternion.Inverse(rig.TurnNearToFar);
            }
            return rig.HostsNear || rig.HostsFar;
        }

        private static bool Usable(Scene scene) => PortalSpaces.Usable(scene);

        private static ulong Raw(Scene scene) => scene.handle.GetRawData();

        // ------------------------------------------------------------------------------------------ proxies

        private ProxyBody Acquire(Scene scene, in PortalShapeKey key, Collider source, LinkRig rig, NetworkIdentity authority, bool dynamic)
        {
            ProxyBody proxy;
            if (dynamic)
            {
                var captured = key;
                proxy = _pool.Acquire(Raw(scene), key, () => Build(scene, captured, source, true));
            }
            else proxy = Build(scene, key, source, false);
            proxy.Marker.Authority = authority;
            proxy.Marker.LinkId = rig.LinkId;
            proxy.Marker.Source = source;
            proxy.Marker.IsStatic = !dynamic;
            if (source != null && proxy.Collider.sharedMaterial != source.sharedMaterial) proxy.Collider.sharedMaterial = source.sharedMaterial;
            proxy.Go.name = dynamic ? "Portal proxy: " + (authority != null ? authority.name : "?") : "Portal throat: " + (source != null ? source.name : "?");
            LiveProxies++;
            rig.LiveProxies++;
            if (dynamic && rig.Portal != null) rig.Portal.ProxyTotal++;
            return proxy;
        }

        private ProxyBody Build(Scene scene, in PortalShapeKey key, Collider source, bool dynamic)
        {
            var go = new GameObject("Portal proxy") { layer = Portals.ProxyLayer };
            go.SetActive(false);
            SceneManager.MoveGameObjectToScene(go, scene);
            var proxy = new ProxyBody { Go = go, T = go.transform, Key = key, Scene = scene };
            proxy.Marker = go.AddComponent<PortalProxy>();
            proxy.Collider = PortalShapes.AddCollider(go, key, source);
            if (dynamic)
            {
                var body = go.AddComponent<Rigidbody>();
                body.isKinematic = true;
                body.useGravity = false;
                body.interpolation = RigidbodyInterpolation.None;
                proxy.Body = body;
            }
            return proxy;
        }

        private void ReleaseDynamic(LinkRig rig, ProxyBody proxy)
        {
            if (proxy == null || proxy.Go == null) return;
            UndoPairIgnoresInvolving(rig, proxy.Collider);
            proxy.Marker.Authority = null;
            proxy.Marker.Source = null;
            proxy.Posed = false;
            proxy.Go.SetActive(false);
            _pool.Release(Raw(proxy.Scene), proxy.Key, proxy);
            LiveProxies--;
            rig.LiveProxies--;
            if (rig.Portal != null) rig.Portal.ProxyTotal--;
        }

        private void DestroyStatic(LinkRig rig, ProxyBody proxy)
        {
            if (proxy == null) return;
            proxy.Destroy();
            LiveProxies--;
            rig.LiveProxies--;
        }

        private static void IgnoreCollision(Collider a, Collider b, bool ignore)
        {
            if (a != null && b != null && a.gameObject.scene == b.gameObject.scene && a.enabled && b.enabled) Physics.IgnoreCollision(a, b, ignore);
        }

        // ------------------------------------------------------------------------------------------ teardown

        /// <summary>Drop everything this instance built in scenes.</summary>
        public void Dispose()
        {
            foreach (var rig in _rigs.Values) TearDown(rig, destroyApertures: true);
            _rigs.Clear();
            foreach (var aperture in _nearApertures.Values) aperture.Destroy();
            _nearApertures.Clear();
            _drain.Clear();
            _pool.Drain(_drain);
            foreach (var proxy in _drain) proxy.Destroy();
            _drain.Clear();
            _byScope.Clear();
            Bodies.Clear();
            LiveProxies = 0;
        }

        /// <summary>Remove a link's bodies, proxies and throat; its aperture colliders too when asked.</summary>
        private void TearDown(LinkRig rig, bool destroyApertures)
        {
            DropDynamic(rig);
            DropThroat(rig);
            if (destroyApertures)
            {
                if (rig.FarAperture != null) { rig.FarAperture.Destroy(); rig.FarAperture = null; }
                rig.NearAperture = null; // the near side's belongs to the portal
            }
        }

        private void DropDynamic(LinkRig rig)
        {
            if (rig.Tracked.Count > 0)
            {
                foreach (var tracked in rig.Tracked.Values) ReleaseTracked(rig, tracked);
                rig.Tracked.Clear();
            }
            foreach (var pair in rig.Pairs.Values) UndoPair(pair);
            rig.Pairs.Clear();
        }

        private void DropThroat(LinkRig rig)
        {
            for (int i = 0; i < rig.NearThroat.Count; i++) DestroyStatic(rig, rig.NearThroat[i]);
            for (int i = 0; i < rig.FarThroat.Count; i++) DestroyStatic(rig, rig.FarThroat[i]);
            rig.NearThroat.Clear();
            rig.FarThroat.Clear();
            rig.ThroatActive = false;
            rig.BuiltGeneration = -1;
        }
    }
}
