using System.Collections.Generic;

namespace Nebula
{
    /// <summary>
    /// Which workers the orchestrator has declared dead, as a mirror of the control plane can tell: a worker row that
    /// was in the last document observed and is missing from this one, when both are the same document
    /// (<see cref="IControlPlane.DocumentId"/>). A row missing from a replacement document is a control plane that
    /// came back empty, not a verdict (<c>docs/control-plane-availability.md</c> D1b), so it is never reported.
    /// <para>
    /// Gateways and workers use it to stop believing in a process the mesh has given up on, even while its socket
    /// is still up: its containers are about to be restored elsewhere, and a client must not be shown the old copy
    /// beside the restored one (<c>docs/persistence-durability.md</c> D11).
    /// </para>
    /// </summary>
    public sealed class WorkerRoster
    {
        private HashSet<string> _listed = new HashSet<string>();
        private HashSet<string> _next = new HashSet<string>();
        private string _document;

        /// <summary>
        /// Read the current document and add to <paramref name="declaredDead"/> (which is not cleared) the id of every
        /// worker the previous observation listed and this one does not. Call on every control-plane change.
        /// </summary>
        public void Observe(IControlPlane controlPlane, List<string> declaredDead)
        {
            if (controlPlane == null) return;
            string document = controlPlane.DocumentId ?? "";
            _next.Clear();
            foreach (var w in controlPlane.Workers)
                if (w != null && !string.IsNullOrEmpty(w.WorkerId) && w.Status != WorkerStatus.Dead) _next.Add(w.WorkerId);
            if (_document == document && declaredDead != null)
                foreach (var id in _listed) if (!_next.Contains(id)) declaredDead.Add(id);
            _document = document;
            var swap = _listed;
            _listed = _next;
            _next = swap;
        }
    }
}
