using System;
using System.Collections.Generic;

namespace Nebula
{
    /// <summary>
    /// Portal adjacency (<c>docs/portals.md</c> D30): a separate relation between the container a portal's near face is fixed in and the containers of the far
    /// scope its band touches, one edge for each link that is not closed. The ghost band consults it, with the band box, to decide that a body in the band of one
    /// face is ghosted to the worker that owns the other side; nothing else does. <see cref="ContainerRegistry"/> keeps linking neighbours only inside one scope
    /// (<c>docs/scoped-chunk-grids.md</c> D11), so two scopes' boxes that overlap exactly are still not neighbours anywhere but here. Pure data: it resolves no
    /// container and knows no worker.
    /// </summary>
    internal sealed class PortalAdjacency
    {
        /// <summary>One link's edge.</summary>
        public sealed class Edge
        {
            public ulong LinkId;
            public ContainerRef Near;
            public readonly List<ContainerRef> Far = new List<ContainerRef>(2);
            /// <summary>The worker that drives the link, or null for the worker's own.</summary>
            public string Driver;
        }

        private readonly Dictionary<ulong, Edge> _edges = new Dictionary<ulong, Edge>();

        /// <summary>How many links have an edge.</summary>
        public int Count => _edges.Count;

        public IEnumerable<Edge> Edges => _edges.Values;

        /// <summary>Set (or replace) a link's edge: the near face's container and the far containers its band touches.</summary>
        public Edge Set(ulong linkId, ContainerRef near, IEnumerable<ContainerRef> far, string driver = null)
        {
            if (!_edges.TryGetValue(linkId, out var edge)) _edges.Add(linkId, edge = new Edge { LinkId = linkId });
            edge.Near = near;
            edge.Driver = driver;
            edge.Far.Clear();
            if (far != null) foreach (var c in far) if (!edge.Far.Contains(c)) edge.Far.Add(c);
            return edge;
        }

        public bool Remove(ulong linkId) => _edges.Remove(linkId);

        public bool TryGet(ulong linkId, out Edge edge) => _edges.TryGetValue(linkId, out edge);

        /// <summary>The far containers whose band is touched by the band of a face fixed in <paramref name="near"/>, over every link.</summary>
        public void FarOf(ContainerRef near, List<ContainerRef> into)
        {
            foreach (var edge in _edges.Values)
            {
                if (edge.Near != near) continue;
                foreach (var c in edge.Far) if (!into.Contains(c)) into.Add(c);
            }
        }

        /// <summary>The near containers whose face's band touches <paramref name="far"/>, over every link.</summary>
        public void NearOf(ContainerRef far, List<ContainerRef> into)
        {
            foreach (var edge in _edges.Values)
                if (edge.Far.Contains(far) && !into.Contains(edge.Near)) into.Add(edge.Near);
        }

        /// <summary>Whether a link has an edge that relates these two containers.</summary>
        public bool Relates(ContainerRef near, ContainerRef far)
        {
            foreach (var edge in _edges.Values) if (edge.Near == near && edge.Far.Contains(far)) return true;
            return false;
        }

        public void Clear() => _edges.Clear();
    }
}
