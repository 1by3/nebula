using System;

namespace Nebula
{
    /// <summary>Why somebody is asking to enter a target (<see cref="AdmissionRequest.Kind"/>).</summary>
    public enum AdmissionKind : byte
    {
        /// <summary>A client is being placed for the first time on this gateway: its <c>Hello</c> named the scope.</summary>
        Join = 0,
        /// <summary>An entity already in the mesh is crossing into the target (<see cref="NebulaWorker.PrepareTransfer"/>).</summary>
        Transfer = 1,
    }

    /// <summary>What the admission hook may answer (<see cref="AdmissionDecision"/>).</summary>
    public enum AdmissionAction : byte
    {
        /// <summary>Let this one in anyway. The mesh does not split the target and does not change what it costs; the game has decided the arrival is worth it.</summary>
        Admit = 0,
        /// <summary>Refuse with a typed rejection the caller can act on (a queue, another instance, a message).</summary>
        Reject = 1,
        /// <summary>Neither: keep the client waiting and ask again shortly. Only a join can be held; a transfer reads a hold as a rejection.</summary>
        Hold = 2,
    }

    /// <summary>
    /// What the game is told when something asks to enter a target that is at capacity. Everything here is derived
    /// from rows the mesh already keeps, so a policy is a pure function of its argument and can be unit tested.
    /// See <c>docs/capacity-admission.md</c>.
    /// </summary>
    public struct AdmissionRequest
    {
        /// <summary>Whether this is a client being placed or an entity crossing in.</summary>
        public AdmissionKind Kind;
        /// <summary>The scope being entered (<see cref="EntityLocation.ScopeKey"/>); empty for the public world.</summary>
        public string ScopeKey;
        /// <summary>The container the arrival would land in. Empty when the answer is about a whole scope and no part has been picked yet.</summary>
        public string ContainerId;
        /// <summary>How saturated the target is, and in what (<see cref="NebulaCapacity"/>).</summary>
        public CapacityInfo Capacity;
        /// <summary>The gateway's id for this client, or 0 for a transfer of a server-driven entity.</summary>
        public ulong ClientId;
        /// <summary>The player's identity across sessions (<see cref="PlayerIdentity"/>), empty when unauthenticated or for a transfer.</summary>
        public string Identity;
        /// <summary>The name the client gave in its <c>Hello</c>.</summary>
        public string Name;
        /// <summary>The client is a bot (a load generator or an AI player).</summary>
        public bool IsBot;
        /// <summary>The game's own tags on this client (<see cref="NebulaGateway.SetClientTag"/> / <c>SetClientTags</c>): the natural place for "party 7" or "staff".</summary>
        public byte Team;
        public ulong Tags;
        /// <summary>The entity crossing in, for <see cref="AdmissionKind.Transfer"/>; 0 otherwise.</summary>
        public ulong EntityNetId;
        /// <summary>The client that owns that entity, for <see cref="AdmissionKind.Transfer"/>; 0 for a server-driven entity.</summary>
        public ulong OwnerClientId;
    }

    /// <summary>The game's answer to <see cref="AdmissionRequest"/>. Build one with <see cref="Admit"/>, <see cref="Reject"/> or <see cref="Hold"/>.</summary>
    public struct AdmissionDecision
    {
        public AdmissionAction Action;
        /// <summary>
        /// What the arriving client is told, fit to show a player. Empty takes Nebula's own sentence, which names
        /// the dominant component and the saturation.
        /// </summary>
        public string Reason;

        /// <summary>Let this one in although the target is at capacity.</summary>
        public static AdmissionDecision Admit() => new AdmissionDecision { Action = AdmissionAction.Admit };
        /// <summary>Refuse, with an optional sentence for the player.</summary>
        public static AdmissionDecision Reject(string reason = null) => new AdmissionDecision { Action = AdmissionAction.Reject, Reason = reason };
        /// <summary>Keep the client waiting (<see cref="JoinHoldReason.AtCapacity"/>) and ask again shortly. A transfer reads this as a rejection.</summary>
        public static AdmissionDecision Hold(string reason = null) => new AdmissionDecision { Action = AdmissionAction.Hold, Reason = reason };
    }

    /// <summary>The game's admission policy. See <see cref="NebulaAdmission.Decide"/>.</summary>
    public delegate AdmissionDecision AdmissionPolicy(in AdmissionRequest request);

    /// <summary>
    /// The hook that decides what happens when an interaction domain is full. Nebula measures and reports; whether
    /// <i>this</i> player gets in anyway - a party member of somebody inside, a staff account, the last member of a
    /// raid group - is the game's business and nothing the mesh can guess.
    /// <para>
    /// A static, because there is one answer per process and the decision must not depend on which gateway object a
    /// game happened to reach; the same reasoning as <see cref="ScopeLifecycle.ShouldRetire"/>. Consulted on every
    /// join and every prepared transfer into a target whose capacity says at-capacity, and on every one of them when
    /// <see cref="AlwaysConsult"/> is set. A policy that throws is logged and read as
    /// <see cref="AdmissionAction.Reject"/>: the failure mode of a wrong answer here is a collapsed tick for
    /// everyone already inside. Design record: <c>docs/capacity-admission.md</c>.
    /// </para>
    /// </summary>
    public static class NebulaAdmission
    {
        /// <summary>The policy. Defaults to <see cref="RejectWhenAtCapacity"/>; setting it to null restores that.</summary>
        public static AdmissionPolicy Decide = RejectWhenAtCapacity;

        /// <summary>
        /// Consult <see cref="Decide"/> for every join and transfer, not only for the ones into a saturated target.
        /// Off by default: a hook that is asked about every arrival is a hook on the hot path of every join, and a
        /// game that only wants to say "let staff into a full station" should not pay for it.
        /// </summary>
        public static bool AlwaysConsult;

        /// <summary>Exceptions thrown by <see cref="Decide"/> since this process started. Anything but zero means the game is not deciding what it thinks it is.</summary>
        public static int PolicyErrors { get; internal set; }

        /// <summary>The default: refuse an arrival into a target that is at capacity, and admit everything else.</summary>
        public static AdmissionDecision RejectWhenAtCapacity(in AdmissionRequest request) =>
            request.Capacity.AtCapacity ? AdmissionDecision.Reject() : AdmissionDecision.Admit();

        /// <summary>
        /// Ask the policy, catching whatever it throws. <paramref name="context"/> names the caller in the log line.
        /// Returns the decision; a target that is not at capacity and <see cref="AlwaysConsult"/> off is admitted
        /// without calling anything.
        /// </summary>
        public static AdmissionDecision Ask(in AdmissionRequest request, string context = null)
        {
            if (!request.Capacity.AtCapacity && !AlwaysConsult) return AdmissionDecision.Admit();
            var policy = Decide ?? RejectWhenAtCapacity;
            try { return policy(request); }
            catch (Exception e)
            {
                PolicyErrors++;
                NebulaLog.Error($"the admission policy threw for {context ?? "an arrival"}: {e.Message}; refusing the arrival");
                return AdmissionDecision.Reject();
            }
        }

        /// <summary>Nebula's own sentence for a refusal, used when the policy gives none.</summary>
        public static string DefaultReason(in CapacityInfo capacity) =>
            capacity.Known
                ? $"this destination is at capacity ({capacity.DominantName} is at {capacity.Saturation * 100f:0} % of its budget)"
                : "this destination is at capacity";

        /// <summary>Restore the defaults. For tests and for a process that reloads game code.</summary>
        public static void Reset()
        {
            Decide = RejectWhenAtCapacity;
            AlwaysConsult = false;
            PolicyErrors = 0;
        }
    }
}
