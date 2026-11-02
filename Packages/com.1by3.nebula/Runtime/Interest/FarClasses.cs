using System;
using System.Collections.Generic;

namespace Nebula
{
    /// <summary>
    /// One class of the far relevance tier (<c>docs/interest-management.md</c> §16, D110): a budget, a rate, a reach and a
    /// priority shared by the far entities that name it (<see cref="NetworkIdentity.FarClass"/>). Classes keep a busy kind
    /// of far entity from crowding out another: a crowd of distant people fills its own budget and never pushes ships or
    /// buildings out of a client's far tier. Configured in <see cref="NebulaConfig.InterestFarClasses"/>.
    /// </summary>
    [Serializable]
    public struct FarClass
    {
        /// <summary>
        /// The class id an entity names (1 to 255). Id 0 is the default class every entity is in unless it names another; an
        /// entry with id 0 replaces the default class's settings.
        /// </summary>
        public byte Id;
        /// <summary>A name for logs, diagnostics and the inspector. Not sent anywhere.</summary>
        public string Name;
        /// <summary>Most entities of this class one client holds at once, nearest first. 0: none of this class are sent.</summary>
        public int MaxEntities;
        /// <summary>Far updates a second for every entity of the class (0: each entity's own <see cref="NetworkIdentity.FarUpdateRate"/>).</summary>
        public float UpdateRate;
        /// <summary>
        /// Far radius in metres for every entity of the class (0: each entity's own <see cref="NetworkIdentity.FarRelevanceRadius"/>).
        /// An entity that names a class with a radius is far relevant even when its own radius is 0.
        /// </summary>
        public float Radius;
        /// <summary>
        /// Which classes are served first when <see cref="NebulaConfig.InterestFarMaxTotal"/> caps a client's whole far tier:
        /// higher first, then lower ids. Without a total cap every class gets its own budget and the priority does not matter.
        /// </summary>
        public int Priority;
        /// <summary>
        /// The entities stand upright (people, creatures): a compact update (<see cref="FarPosesMsg"/>) carries only the way
        /// they face, in 2 bytes, and the client keeps their up axis from the last full entry.
        /// </summary>
        public bool Upright;

        public override string ToString() =>
            $"far class {Id}{(string.IsNullOrEmpty(Name) ? "" : " (" + Name + ")")}: {MaxEntities} max, " +
            $"{(UpdateRate > 0 ? UpdateRate + " Hz" : "own rate")}, {(Radius > 0 ? Radius + " m" : "own radius")}, priority {Priority}{(Upright ? ", upright" : "")}";
    }

    /// <summary>
    /// The far classes resolved from <see cref="InterestSettings"/>: a lookup by id, the default class (id 0, budget
    /// <see cref="InterestSettings.FarMaxEntities"/> unless the list replaces it) and the order classes are served in.
    /// Built once per settings and shared by the worker (rates and radii) and the gateway (budgets).
    /// An entity that names a class the table does not define is in the default class.
    /// </summary>
    public sealed class FarClassTable
    {
        private readonly FarClass[] _byId = new FarClass[256];
        private readonly bool[] _defined = new bool[256];
        private readonly int[] _rank = new int[256];
        private readonly List<byte> _ids = new List<byte>();

        /// <summary>Most far entities one client holds over every class (0: no cap beyond each class's own).</summary>
        public int MaxTotal { get; private set; }
        /// <summary>Whether any class may send a client anything: the tier is on.</summary>
        public bool AnyBudget { get; private set; }
        /// <summary>The defined classes' ids, in the order they are served (priority, then id).</summary>
        public IReadOnlyList<byte> Ids => _ids;

        /// <summary>The table for <paramref name="settings"/> (already validated).</summary>
        public static FarClassTable From(in InterestSettings settings) => new FarClassTable(settings.FarMaxEntities, settings.FarClasses, settings.FarMaxTotal);

        public FarClassTable(int defaultMaxEntities, IReadOnlyList<FarClass> classes = null, int maxTotal = 0)
        {
            _byId[0] = new FarClass { Id = 0, Name = "default", MaxEntities = Math.Max(0, defaultMaxEntities) };
            _defined[0] = true;
            if (classes != null)
                for (int i = 0; i < classes.Count; i++)
                {
                    var c = classes[i];
                    if (_defined[c.Id] && c.Id != 0) continue; // the first entry for an id wins (validation reports the rest)
                    _byId[c.Id] = c;
                    _defined[c.Id] = true;
                }
            MaxTotal = Math.Max(0, maxTotal);
            for (int id = 0; id < 256; id++) if (_defined[id]) _ids.Add((byte)id);
            _ids.Sort((a, b) => _byId[a].Priority != _byId[b].Priority ? _byId[b].Priority.CompareTo(_byId[a].Priority) : a.CompareTo(b));
            for (int i = 0; i < _ids.Count; i++)
            {
                _rank[_ids[i]] = i;
                if (_byId[_ids[i]].MaxEntities > 0) AnyBudget = true;
            }
        }

        /// <summary>Whether <paramref name="id"/> is a class of its own (otherwise it is in the default class).</summary>
        public bool Defines(byte id) => _defined[id];
        /// <summary>The class an entity naming <paramref name="id"/> is counted in: itself when defined, else the default class 0.</summary>
        public byte BudgetOf(byte id) => _defined[id] ? id : (byte)0;
        /// <summary>The settings of the class an entity naming <paramref name="id"/> is in.</summary>
        public FarClass Of(byte id) => _byId[BudgetOf(id)];
        /// <summary>Where <paramref name="id"/>'s class is served: 0 first.</summary>
        public int RankOf(byte id) => _rank[BudgetOf(id)];

        /// <summary>
        /// An entity's far radius and rate once its class has had its say: the class's own when it sets them, the entity's
        /// otherwise. A radius of 0 means the entity is not far relevant.
        /// </summary>
        public void Resolve(byte farClass, float ownRadius, float ownRate, out float radius, out float rate)
        {
            var c = Of(farClass);
            radius = c.Radius > 0f ? c.Radius : ownRadius > 0f ? ownRadius : 0f;
            rate = FarRelevance.ClampRate(c.UpdateRate > 0f ? c.UpdateRate : ownRate);
        }
    }

    /// <summary>
    /// One client's choice of far entities at an evaluation (D110): every candidate in, then <see cref="Choose"/> keeps the
    /// nearest of each class up to its budget, serving classes in priority order while a total cap allows. Sorts only when
    /// some budget is exceeded. Allocation free after warm-up; one per gateway, reused for every client.
    /// </summary>
    internal sealed class FarSelection
    {
        private struct Candidate
        {
            public ulong NetId;
            public double SqrDistance;
            public int Rank;
            public byte Budget;
        }

        private readonly List<Candidate> _candidates = new List<Candidate>();
        private readonly int[] _counts = new int[256];
        private readonly List<byte> _touched = new List<byte>();
        private static readonly Comparison<Candidate> ByRankThenDistance = (a, b) => a.Rank != b.Rank ? a.Rank.CompareTo(b.Rank) : a.SqrDistance.CompareTo(b.SqrDistance);

        public int Count => _candidates.Count;

        public void Clear()
        {
            _candidates.Clear();
            for (int i = 0; i < _touched.Count; i++) _counts[_touched[i]] = 0;
            _touched.Clear();
        }

        public void Add(FarClassTable table, ulong netId, byte farClass, double sqrDistance)
        {
            byte budget = table.BudgetOf(farClass);
            if (_counts[budget]++ == 0) _touched.Add(budget);
            _candidates.Add(new Candidate { NetId = netId, SqrDistance = sqrDistance, Rank = table.RankOf(budget), Budget = budget });
        }

        /// <summary>Put the chosen net ids in <paramref name="into"/> (not cleared), and clear the candidates.</summary>
        public void Choose(FarClassTable table, HashSet<ulong> into)
        {
            bool over = table.MaxTotal > 0 && _candidates.Count > table.MaxTotal;
            for (int i = 0; i < _touched.Count && !over; i++) if (_counts[_touched[i]] > table.Of(_touched[i]).MaxEntities) over = true;
            if (!over)
            {
                for (int i = 0; i < _candidates.Count; i++) into.Add(_candidates[i].NetId);
                Clear();
                return;
            }
            _candidates.Sort(ByRankThenDistance);
            for (int i = 0; i < _touched.Count; i++) _counts[_touched[i]] = 0;
            int total = 0;
            for (int i = 0; i < _candidates.Count; i++)
            {
                if (table.MaxTotal > 0 && total >= table.MaxTotal) break;
                var c = _candidates[i];
                if (_counts[c.Budget] >= table.Of(c.Budget).MaxEntities) continue;
                _counts[c.Budget]++;
                total++;
                into.Add(c.NetId);
            }
            Clear();
        }
    }
}
