using Nebula;
using UnityEngine;

namespace NebulaSamples
{
    /// <summary>
    /// The driving seat of a <see cref="Vehicle"/>, on the worker: it hands the car's controls to the player whose pawn
    /// sits in it and takes them back when the pawn leaves. It is a <b>sample</b>: how a player gets into a seat (a
    /// prompt, a trigger, an animation) is the game's. Put it on the car's root next to the <see cref="Vehicle"/> and a
    /// <see cref="Container"/>: the box is where the seats are, so a pawn inside it rides with the car, on every client
    /// and across worker seams.
    /// </summary>
    [RequireComponent(typeof(Vehicle))]
    public sealed class DriverSeat : NetworkBehaviour
    {
        private NetworkIdentity _pawn;

        /// <summary>Worker: the pawn in the driving seat, or null.</summary>
        public NetworkIdentity Occupant => _pawn;

        /// <summary>
        /// Worker, on the vehicle's authority: seat <paramref name="pawn"/> at the wheel. The pawn's own controller keeps
        /// it at the seat inside the car's box. False when the seat is taken, or when the pawn belongs to no player.
        /// </summary>
        public bool TrySit(NetworkIdentity pawn)
        {
            if (!HasAuthority || pawn == null || pawn.OwnerClientId == 0 || _pawn != null) return false;
            if (!Identity.SetDriver(pawn.OwnerClientId)) return false;
            _pawn = pawn;
            return true;
        }

        /// <summary>Worker: whoever is in the seat gets out, and the car goes back to its own input.</summary>
        public void Leave()
        {
            if (!HasAuthority) return;
            _pawn = null;
            _absentTicks = 0;
            Identity.ClearDriver();
        }

        [Tooltip("Ticks the driver's pawn may be missing from the car's box before the seat is freed: after a handover the pawn arrives just after the car.")]
        public int GraceTicks = 30;
        private int _absentTicks;

        public override void NetworkTick(uint tick, float deltaTime)
        {
            if (!HasAuthority || Identity.DriverClientId == 0) { _absentTicks = 0; return; }
            // After a handover this worker's seat has not met the pawn yet: it is the rider in the box the driver owns.
            if (_pawn == null || !_pawn.IsSpawned) _pawn = FindDriversPawn();
            if (_pawn != null && _pawn.Container == Identity.Carried) { _absentTicks = 0; return; }
            // The driver's pawn got out of the car (despawned, or out of the box): free the seat.
            if (++_absentTicks > GraceTicks) Leave();
        }

        private NetworkIdentity FindDriversPawn()
        {
            var box = Identity.Carried;
            if (box == null) return null;
            foreach (var rider in box.Entities)
                if (rider != null && rider.OwnerClientId == Identity.DriverClientId) return rider;
            return null;
        }
    }
}
