using System;
using System.Collections.Generic;
#if NEBULA_SERVICE
using Nebula.ServicePrimitives;
#else
using UnityEngine;
#endif

namespace Nebula
{
    /// <summary>The shape of a <see cref="Portal"/>'s aperture, the opening on its face's z = 0 plane (<c>docs/portals.md</c> D3).</summary>
    public enum PortalApertureKind : byte
    {
        /// <summary>A rectangle centred on the face's origin, <c>|x| ≤ HalfWidth</c> and <c>|y| ≤ HalfHeight</c>.</summary>
        Box = 0,
    }

    /// <summary>How a portal names its far side (<c>docs/portals.md</c> D5).</summary>
    public enum PortalFarKind : byte
    {
        /// <summary>A scope that exists anyway, named by its scope key, with the far face's pose given in that scope.</summary>
        Fixed = 0,
        /// <summary>An <see cref="InstanceTemplate"/>: one far scope per party, created on demand at the near face's pose.</summary>
        Template = 1,
    }

    /// <summary>Where a party's <see cref="PortalLink"/> stands (<c>docs/portals.md</c> D9).</summary>
    public enum PortalState : byte
    {
        /// <summary>Nothing is prepared and nothing crosses. The aperture is solid.</summary>
        Closed = 0,
        /// <summary><see cref="Portal.Open"/> was called and the link is getting ready. Nothing crosses yet; <see cref="PortalLink.Wait"/> says what it waits for.</summary>
        Preparing = 1,
        /// <summary>Everything the far side needs is standing: entities cross on the tick their root passes the face.</summary>
        Open = 2,
        /// <summary><see cref="Portal.Close"/> was called and the link waits for the band to empty before it turns solid.</summary>
        Closing = 3,
    }

    /// <summary>Why a link is not <see cref="PortalState.Open"/> (<c>docs/portals.md</c> D9). <see cref="None"/> for an open link.</summary>
    public enum PortalWait : byte
    {
        /// <summary>Nothing: the link is open, or closed on purpose.</summary>
        None = 0,
        /// <summary>The far scope is not active yet, or its entry container has no owning lease.</summary>
        FarScopeActivating = 1,
        /// <summary>The far container's content is still loading on the worker that owns it (<see cref="IInstanceContentLoader"/>), or failed.</summary>
        FarWorkerContent = 2,
        /// <summary>The far scope is restoring its persisted entities.</summary>
        FarEntitiesRestoring = 3,
        /// <summary>A member client has not said it holds the far window (<see cref="PortalLink.WaitClient"/>).</summary>
        ClientWarming = 4,
        /// <summary>The far container belongs to another worker, which has not answered the preparation yet.</summary>
        FarWorkerHandshake = 5,
        /// <summary>The link is closing and a body still stands in the band.</summary>
        BandOccupied = 6,
        /// <summary>The far scope is retiring or retired, so the link reads closed.</summary>
        FarScopeRetired = 7,
        /// <summary>The near container is being reassigned to another worker.</summary>
        NearContainerMoving = 8,
    }

    /// <summary>Bit flags of a <see cref="PortalLinkRecord"/>.</summary>
    [Flags]
    public enum PortalLinkFlags : byte
    {
        None = 0,
        /// <summary>The receiving client may look through the link.</summary>
        Observer = 1,
        /// <summary>The receiving client is a member: it must hold the far window and acknowledge it with <see cref="MsgId.PortalWarm"/>.</summary>
        Member = 2,
        /// <summary>Anyone may look through the link, not only its observers.</summary>
        ObserverAnyone = 4,
    }

    /// <summary>The ids portals and their links are named by (<c>docs/portals.md</c> D6): derived, so every process computes the same one.</summary>
    public static class PortalIds
    {
        /// <summary>A portal's id: <see cref="ScopeKeys.Hash"/> of <c>containerId + "#" + id</c>, <c>id</c> unique within the near container.</summary>
        public static ulong Of(string containerId, string id) => ScopeKeys.Hash((containerId ?? "") + "#" + (id ?? ""));

        /// <summary>The hash a party travels as: parties are opaque strings (<c>docs/portals.md</c> D8) and may be a player's identity, so only this goes to clients.</summary>
        public static ulong PartyHash(string party) => ScopeKeys.Hash("party:" + (party ?? ""));

        /// <summary>A link's id: the portal and the party it is for.</summary>
        public static ulong Link(ulong portalId, string party) => ScopeKeys.Hash(portalId.ToString(System.Globalization.CultureInfo.InvariantCulture) + "|" + (party ?? ""));
    }

    /// <summary>
    /// A portal as the network gateway and a client learn it (<see cref="MsgId.Portals"/>, protocol 28): the near face, the
    /// far side, the aperture and the sizes of the regions derived from it. The record is the portal's definition only;
    /// the state of each party's link travels separately (<see cref="PortalLinkRecord"/>).
    /// </summary>
    public struct PortalRecord
    {
        /// <summary>The derived id (<see cref="PortalIds.Of"/>).</summary>
        public ulong PortalId;
        /// <summary>The authored id, unique within the near container.</summary>
        public string Id;
        /// <summary>The near face's scope.</summary>
        public ulong NearScope;
        /// <summary>The container the near face is fixed in, by id (how a gateway matches the rows a client holds) and by reference.</summary>
        public string NearContainerId;
        public ContainerRef NearContainer;
        /// <summary>The near face, absolute in its scope.</summary>
        public Double3 NearOrigin;
        public Quaternion NearRotation;
        public PortalFarKind FarKind;
        /// <summary>A fixed far side's scope; 0 for a template, whose far scope belongs to each link.</summary>
        public ulong FarScope;
        /// <summary>A fixed far side's face, absolute in the far scope.</summary>
        public Double3 FarOrigin;
        public Quaternion FarRotation;
        /// <summary>A template far side's template id; empty for a fixed one.</summary>
        public string TemplateId;
        public PortalApertureKind ApertureKind;
        public float HalfWidth, HalfHeight;
        /// <summary>The band: the cross-section grown sideways by <see cref="BandMargin"/>, <c>|z| ≤ BandDepth</c>.</summary>
        public float BandMargin, BandDepth;
        /// <summary>The window, per side: the cross-section grown by <see cref="WindowMargin"/>, extruded <see cref="WindowDepth"/> into that side.</summary>
        public float WindowMargin, WindowDepth;
        /// <summary>Reserved flags (observation-only portals, <c>docs/portals.md</c> §17). 0 today.</summary>
        public byte Flags;

        internal void Write(NetworkWriter w)
        {
            w.WriteULong(PortalId);
            w.WriteString(Id ?? "");
            w.WriteULong(NearScope);
            w.WriteString(NearContainerId ?? "");
            NearContainer.Write(w);
            WriteDouble3(w, NearOrigin);
            WriteQuaternion(w, NearRotation);
            w.WriteByte((byte)FarKind);
            w.WriteULong(FarScope);
            WriteDouble3(w, FarOrigin);
            WriteQuaternion(w, FarRotation);
            w.WriteString(TemplateId ?? "");
            w.WriteByte((byte)ApertureKind);
            w.WriteFloat(HalfWidth); w.WriteFloat(HalfHeight);
            w.WriteFloat(BandMargin); w.WriteFloat(BandDepth);
            w.WriteFloat(WindowMargin); w.WriteFloat(WindowDepth);
            w.WriteByte(Flags);
        }

        internal static PortalRecord Read(NetworkReader r)
        {
            var p = new PortalRecord
            {
                PortalId = r.ReadULong(),
                Id = r.ReadString() ?? "",
                NearScope = r.ReadULong(),
                NearContainerId = r.ReadString() ?? "",
                NearContainer = ContainerRef.Read(r),
                NearOrigin = ReadDouble3(r),
                NearRotation = ReadQuaternion(r),
                FarKind = (PortalFarKind)r.ReadByte(),
                FarScope = r.ReadULong(),
                FarOrigin = ReadDouble3(r),
                FarRotation = ReadQuaternion(r),
                TemplateId = r.ReadString() ?? "",
                ApertureKind = (PortalApertureKind)r.ReadByte(),
                HalfWidth = r.ReadFloat(), HalfHeight = r.ReadFloat(),
                BandMargin = r.ReadFloat(), BandDepth = r.ReadFloat(),
                WindowMargin = r.ReadFloat(), WindowDepth = r.ReadFloat(),
                Flags = r.ReadByte(),
            };
            return p;
        }

        internal static void WriteDouble3(NetworkWriter w, Double3 v) { w.WriteDouble(v.X); w.WriteDouble(v.Y); w.WriteDouble(v.Z); }
        internal static Double3 ReadDouble3(NetworkReader r) => new Double3(r.ReadDouble(), r.ReadDouble(), r.ReadDouble());
        internal static void WriteQuaternion(NetworkWriter w, Quaternion q) { w.WriteFloat(q.x); w.WriteFloat(q.y); w.WriteFloat(q.z); w.WriteFloat(q.w); }
        internal static Quaternion ReadQuaternion(NetworkReader r) => new Quaternion(r.ReadFloat(), r.ReadFloat(), r.ReadFloat(), r.ReadFloat());
    }

    /// <summary>A change to the portals a receiver knows (<see cref="MsgId.Portals"/>): records to add or replace, and ids to forget.</summary>
    public sealed class PortalsUpdate
    {
        public List<PortalRecord> Upserts = new List<PortalRecord>();
        public List<ulong> Removes = new List<ulong>();
    }

    /// <summary><see cref="MsgId.Portals"/>: <c>[upserts:ushort]{record}[removes:ushort]{portalId}</c>, worker → network gateway → client.</summary>
    public static class PortalsMsg
    {
        public static void Write(NetworkWriter w, IList<PortalRecord> upserts, IList<ulong> removes)
        {
            w.WriteByte((byte)MsgId.Portals);
            w.WriteUShort((ushort)(upserts?.Count ?? 0));
            if (upserts != null) for (int i = 0; i < upserts.Count; i++) upserts[i].Write(w);
            w.WriteUShort((ushort)(removes?.Count ?? 0));
            if (removes != null) for (int i = 0; i < removes.Count; i++) w.WriteULong(removes[i]);
        }

        /// <summary>Read the body after the id byte.</summary>
        public static PortalsUpdate Read(NetworkReader r)
        {
            var update = new PortalsUpdate();
            int n = r.ReadUShort();
            for (int i = 0; i < n; i++) update.Upserts.Add(PortalRecord.Read(r));
            int removed = r.ReadUShort();
            for (int i = 0; i < removed; i++) update.Removes.Add(r.ReadULong());
            return update;
        }
    }

    /// <summary>
    /// One party's link through a portal, as the network gateway and a client learn it (<see cref="MsgId.PortalLinks"/>).
    /// Toward the network gateway it carries the members and the observers; toward a client both lists are empty and
    /// <see cref="Flags"/> says whether this client is a member and whether it is an observer.
    /// </summary>
    public struct PortalLinkRecord
    {
        public ulong LinkId;
        public ulong PortalId;
        /// <summary>The party as <see cref="PortalIds.PartyHash"/>.</summary>
        public ulong PartyHash;
        /// <summary>The far scope's id and the container a crossing enters (the far window of this version, <c>docs/portals.md</c> D10).</summary>
        public ulong FarScope;
        public string FarEntryId;
        public ContainerRef FarEntry;
        public PortalState State;
        public PortalWait Wait;
        /// <summary>The member client the link waits for, when <see cref="Wait"/> is <see cref="PortalWait.ClientWarming"/>; else 0.</summary>
        public ulong WaitClient;
        /// <summary>How ready the link is, 0 (nothing) to 255 (everything); <see cref="PortalLink.Progress"/> is this over 255.</summary>
        public byte Progress;
        /// <summary>Counts the link's openings, so a client's <see cref="MsgId.PortalWarm"/> for an earlier opening is recognised as stale.</summary>
        public uint Generation;
        public PortalLinkFlags Flags;
        /// <summary>Toward the network gateway only: the clients that must be warm.</summary>
        public List<ulong> Members;
        /// <summary>Toward the network gateway only: the clients that may look through (empty with <see cref="PortalLinkFlags.ObserverAnyone"/>).</summary>
        public List<ulong> Observers;

        internal void Write(NetworkWriter w)
        {
            w.WriteULong(LinkId);
            w.WriteULong(PortalId);
            w.WriteULong(PartyHash);
            w.WriteULong(FarScope);
            w.WriteString(FarEntryId ?? "");
            FarEntry.Write(w);
            w.WriteByte((byte)State);
            w.WriteByte((byte)Wait);
            w.WriteULong(WaitClient);
            w.WriteByte(Progress);
            w.WriteUInt(Generation);
            w.WriteByte((byte)Flags);
            WriteList(w, Members);
            WriteList(w, Observers);
        }

        internal static PortalLinkRecord Read(NetworkReader r)
        {
            var l = new PortalLinkRecord
            {
                LinkId = r.ReadULong(),
                PortalId = r.ReadULong(),
                PartyHash = r.ReadULong(),
                FarScope = r.ReadULong(),
                FarEntryId = r.ReadString() ?? "",
                FarEntry = ContainerRef.Read(r),
                State = (PortalState)r.ReadByte(),
                Wait = (PortalWait)r.ReadByte(),
                WaitClient = r.ReadULong(),
                Progress = r.ReadByte(),
                Generation = r.ReadUInt(),
                Flags = (PortalLinkFlags)r.ReadByte(),
            };
            l.Members = ReadList(r);
            l.Observers = ReadList(r);
            return l;
        }

        private static void WriteList(NetworkWriter w, List<ulong> list)
        {
            w.WriteUShort((ushort)(list?.Count ?? 0));
            if (list != null) for (int i = 0; i < list.Count; i++) w.WriteULong(list[i]);
        }

        private static List<ulong> ReadList(NetworkReader r)
        {
            int n = r.ReadUShort();
            var list = new List<ulong>(n);
            for (int i = 0; i < n; i++) list.Add(r.ReadULong());
            return list;
        }

        /// <summary>Whether a client with these flags may look through the link.</summary>
        public bool IsObserver => (Flags & (PortalLinkFlags.Observer | PortalLinkFlags.ObserverAnyone)) != 0;
    }

    /// <summary>A change to the links a receiver knows (<see cref="MsgId.PortalLinks"/>).</summary>
    public sealed class PortalLinksUpdate
    {
        public List<PortalLinkRecord> Upserts = new List<PortalLinkRecord>();
        public List<ulong> Removes = new List<ulong>();
    }

    /// <summary><see cref="MsgId.PortalLinks"/>: <c>[upserts:ushort]{record}[removes:ushort]{linkId}</c>, worker → network gateway → client.</summary>
    public static class PortalLinksMsg
    {
        public static void Write(NetworkWriter w, IList<PortalLinkRecord> upserts, IList<ulong> removes)
        {
            w.WriteByte((byte)MsgId.PortalLinks);
            w.WriteUShort((ushort)(upserts?.Count ?? 0));
            if (upserts != null) for (int i = 0; i < upserts.Count; i++) upserts[i].Write(w);
            w.WriteUShort((ushort)(removes?.Count ?? 0));
            if (removes != null) for (int i = 0; i < removes.Count; i++) w.WriteULong(removes[i]);
        }

        /// <summary>Read the body after the id byte.</summary>
        public static PortalLinksUpdate Read(NetworkReader r)
        {
            var update = new PortalLinksUpdate();
            int n = r.ReadUShort();
            for (int i = 0; i < n; i++) update.Upserts.Add(PortalLinkRecord.Read(r));
            int removed = r.ReadUShort();
            for (int i = 0; i < removed; i++) update.Removes.Add(r.ReadULong());
            return update;
        }
    }

    /// <summary>
    /// <see cref="MsgId.PortalWarm"/>: a member client holds the far window of a link and its content is ready. The client
    /// sends it with a zero <see cref="ClientId"/>; the network gateway stamps the client's id before it forwards it to the worker.
    /// </summary>
    public struct PortalWarmMsg
    {
        public ulong LinkId;
        /// <summary>The <see cref="PortalLinkRecord.Generation"/> the client saw.</summary>
        public uint Generation;
        public ulong ClientId;

        public void Write(NetworkWriter w)
        {
            w.WriteByte((byte)MsgId.PortalWarm);
            w.WriteULong(LinkId);
            w.WriteUInt(Generation);
            w.WriteULong(ClientId);
        }

        public static PortalWarmMsg Read(NetworkReader r) => new PortalWarmMsg { LinkId = r.ReadULong(), Generation = r.ReadUInt(), ClientId = r.ReadULong() };
    }
}
