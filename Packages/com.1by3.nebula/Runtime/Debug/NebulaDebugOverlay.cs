using System.Text;
using UnityEngine;

namespace Nebula
{
    /// <summary>
    /// IMGUI status overlay for every role. On a client it shows the tick/RTT/lead, the local player's container and
    /// its current worker, handovers observed, and the container -> worker table. Toggle with F3.
    /// </summary>
    public sealed class NebulaDebugOverlay : MonoBehaviour
    {
        /// <summary>Toggle from game code (e.g. on F3); the overlay itself reads no input so it works with any input backend.</summary>
        public bool Visible = true;

        private static readonly Color[] WorkerColors =
        {
            new Color(0.95f, 0.35f, 0.30f), new Color(0.30f, 0.80f, 0.40f), new Color(0.30f, 0.55f, 0.95f), new Color(0.95f, 0.80f, 0.25f),
            new Color(0.80f, 0.40f, 0.90f), new Color(0.30f, 0.85f, 0.85f), new Color(0.95f, 0.55f, 0.20f), new Color(0.70f, 0.70f, 0.70f),
        };

        private readonly StringBuilder _sb = new StringBuilder(1024);
        private GUIStyle _style;

        public static Color ColorForWorker(ushort workerIndex)
        {
            if (workerIndex == ushort.MaxValue) return new Color(0.35f, 0.35f, 0.35f);
            return WorkerColors[workerIndex % WorkerColors.Length];
        }

        private void OnGUI()
        {
            if (!Visible) return;
            if (_style == null)
            {
                _style = new GUIStyle(GUI.skin.box) { alignment = TextAnchor.UpperLeft, fontSize = 13, richText = true };
                _style.normal.textColor = Color.white;
            }
            var boot = NebulaBootstrap.Instance;
            _sb.Clear();
            _sb.Append("<b>Nebula</b> roles=").Append(boot != null ? boot.Roles.ToString() : "?").Append('\n');

            var client = boot != null ? boot.Client : null;
            if (client != null)
            {
                _sb.Append($"client {client.ConnectionState} id={client.ClientId} rtt={client.RttMs}ms serverTick~{client.EstimatedServerTick:F0} predict={client.PredictedTick} lead={client.InputLeadTicks} (adaptive +{client.InputLeadAdjustTicks}, worker sees {client.LastReportedInputLead})\n");
                _sb.Append($"entities={client.EntityCount} authorityChanges={client.AuthorityChangesSeen}\n");
                var lp = client.LocalPlayer;
                if (lp != null)
                {
                    _sb.Append($"me: {(lp.Container != null ? lp.Container.ContainerId : "-")} on worker {WorkerLabel(lp.OwnerWorkerIndex)} epoch={lp.Epoch}");
                    if (lp.Predicted != null) _sb.Append($" corrections={lp.Predicted.Corrections} last={lp.Predicted.LastCorrectionMagnitude:F2}m");
                    _sb.Append('\n');
                }
            }
            var worker = boot != null ? boot.Worker : null;
            if (worker != null)
            {
                _sb.Append($"worker {worker.WorkerId} tick={worker.CurrentTick} {worker.TickMs:F2}ms auth={worker.AuthoritativeCount} ghosts={worker.GhostsHeld} players={worker.PlayerCount} bots={worker.BotCount} serverDriven={worker.ServerDrivenCount} out={worker.HandoversOut} in={worker.HandoversIn} local={worker.LocalHandovers}\n");
            }
            var gw = boot != null ? boot.Gateway : null;
            if (gw != null) _sb.Append($"gateway clients={gw.ClientCount} workers={gw.WorkerCount} entities={gw.EntityCount}\n");
            var orch = boot != null ? boot.Orchestrator : null;
            if (orch != null) _sb.Append($"orchestrator desired={orch.DesiredWorkers} rebalances={orch.Rebalances} dashboard={orch.DashboardUrl}\n");

            _sb.Append("<b>containers</b>\n");
            foreach (var c in ContainerRegistry.All)
            {
                var col = ColorForWorker(c.OwnerWorkerIndex);
                _sb.Append($"  <color=#{ColorUtility.ToHtmlStringRGB(col)}>■</color> {c.ContainerId} -> {(string.IsNullOrEmpty(c.OwnerWorkerId) ? "unassigned" : c.OwnerWorkerId)} (lease e{c.LeaseEpoch})\n");
            }
            _sb.Append("<i>F3 toggles this overlay</i>");
            var rect = new Rect(10, 10, 620, 24 + 18 * CountLines(_sb));
            // A solid backing: the default box skin is nearly transparent and unreadable over a dark scene.
            var saved = GUI.color;
            GUI.color = new Color(0f, 0f, 0f, 0.78f);
            GUI.DrawTexture(rect, Texture2D.whiteTexture);
            GUI.color = saved;
            GUI.Box(rect, _sb.ToString(), _style);
        }

        private static int CountLines(StringBuilder sb)
        {
            int n = 1;
            for (int i = 0; i < sb.Length; i++) if (sb[i] == '\n') n++;
            return n;
        }

        private static string WorkerLabel(ushort index) => index == ushort.MaxValue ? "?" : $"w{index}";
    }
}
