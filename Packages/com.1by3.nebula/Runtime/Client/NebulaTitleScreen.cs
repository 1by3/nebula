using UnityEngine;

namespace Nebula
{
    /// <summary>
    /// Optional IMGUI panel where a player enters the gateway address and a display name before connecting. Add it
    /// to the same GameObject as <see cref="NebulaBootstrap"/>; Nebula &gt; Set Up Active Scene adds it for you.
    /// Without it, a client started with no connection arguments stays idle until your own UI calls
    /// <see cref="NebulaClient.ConnectTo"/>. The panel stays hidden in processes without the client role and when
    /// <c>-nebula-gateway</c>, <c>-nebula-bot</c>, or <c>-nebula-connect</c> supplies the connection
    /// (<see cref="NebulaClient.ConnectsAutomatically"/>). It hides once the local player spawns; press Escape to show
    /// it again. The entered values persist in PlayerPrefs.
    /// </summary>
    public sealed class NebulaTitleScreen : MonoBehaviour
    {
        private const string PrefGateway = "nebula.gateway";
        private const string PrefName = "nebula.name";

        /// <summary>The client to connect. Leave empty to use <see cref="NebulaBootstrap.Client"/> once the bootstrap creates it.</summary>
        public NebulaClient Client;

        private string _gateway = "";
        private string _name = "";
        private string _validation = "";
        private bool _bound;
        private bool _hideInGame;
        private GUIStyle _title, _label, _field, _button, _box, _small;

        /// <summary>
        /// The bootstrap creates the client after the game scene loads, which can be after this component starts, so
        /// look for it every frame until it exists. Loads the saved gateway and name the first time.
        /// </summary>
        private bool TryBind()
        {
            if (_bound) return true;
            if (Client == null) Client = NebulaBootstrap.Instance != null ? NebulaBootstrap.Instance.Client : null;
            if (Client == null || Client.Config == null) return false;
            var cfg = Client.Config;
            _gateway = PlayerPrefs.GetString(PrefGateway, $"{cfg.GatewayAddress}:{cfg.GatewayPort}");
            _name = PlayerPrefs.GetString(PrefName, Client.PlayerName);
            _bound = true;
            return true;
        }

        private void Update()
        {
            // The panel hides itself once the player has a pawn; Escape (see OnGUI) brings it back.
            if (!TryBind() || Client.ConnectsAutomatically) return;
            if (Client.ConnectionState == NebulaClient.State.InGame && !_hideInGame && Client.LocalPlayer != null) _hideInGame = true;
            if (Client.ConnectionState != NebulaClient.State.InGame) _hideInGame = false;
        }

        private void OnGUI()
        {
            if (!TryBind() || Client.ConnectsAutomatically) return;
            // IMGUI sees the keyboard whichever input backend the project uses, so Escape is handled here.
            if (_hideInGame && Event.current.type == EventType.KeyDown && Event.current.keyCode == KeyCode.Escape) _hideInGame = false;
            if (_hideInGame) return;
            EnsureStyles();

            const float w = 420f, h = 250f;
            var rect = new Rect((Screen.width - w) / 2f, (Screen.height - h) / 2f, w, h);
            GUI.Box(rect, GUIContent.none, _box);
            GUILayout.BeginArea(new Rect(rect.x + 20, rect.y + 16, rect.width - 40, rect.height - 32));
            GUILayout.Label("Nebula", _title);

            if (Client.WantsConnection)
            {
                DrawConnecting();
            }
            else
            {
                DrawChooser();
            }
            GUILayout.EndArea();
        }

        private void DrawChooser()
        {
            GUILayout.Label("Gateway (address:port)", _label);
            GUI.SetNextControlName("gateway");
            _gateway = GUILayout.TextField(_gateway, _field);

            GUILayout.Space(6);
            GUILayout.Label("Name", _label);
            _name = GUILayout.TextField(_name, _field);

            GUILayout.Space(10);
            if (!string.IsNullOrEmpty(_validation)) GUILayout.Label(_validation, _small);
            if (!string.IsNullOrEmpty(Client.LastError)) GUILayout.Label(Client.LastError, _small);

            bool enter = Event.current.type == EventType.KeyDown && (Event.current.keyCode == KeyCode.Return || Event.current.keyCode == KeyCode.KeypadEnter);
            if (GUILayout.Button("Connect", _button, GUILayout.Height(34)) || enter)
            {
                TryConnect();
                if (enter) Event.current.Use();
            }
        }

        private void DrawConnecting()
        {
            string state = Client.ConnectionState switch
            {
                NebulaClient.State.Connecting => "connecting...",
                NebulaClient.State.Connected => "connected, waiting for the world...",
                // A mesh that has scaled to zero holds the join while a worker boots; say so instead of "waiting".
                NebulaClient.State.InGame when Client.Join == JoinState.Starting =>
                    "world starting" + (Client.JoinEstimatedSeconds > 0 ? $", about {Client.JoinEstimatedSeconds} s..." : "..."),
                NebulaClient.State.InGame => "in game" + (Client.LocalPlayer == null ? ", waiting for a pawn..." : ""),
                _ => "reconnecting...",
            };
            GUILayout.Label($"{Client.Config.GatewayAddress}:{Client.Config.GatewayPort}  as  {Client.PlayerName}", _label);
            GUILayout.Label(state, _label);
            if (Client.RttMs >= 0) GUILayout.Label($"rtt {Client.RttMs} ms", _small);
            if (Client.Identity.Length > 0) GUILayout.Label($"identity {Client.Identity.Substring(0, 12)}", _small);
            if (!string.IsNullOrEmpty(Client.LastError)) GUILayout.Label(Client.LastError, _small);
            GUILayout.FlexibleSpace();
            GUILayout.BeginHorizontal();
            if (Client.ConnectionState == NebulaClient.State.InGame && GUILayout.Button("Back to game", _button, GUILayout.Height(30))) _hideInGame = true;
            if (GUILayout.Button("Disconnect", _button, GUILayout.Height(30))) Client.Disconnect();
            GUILayout.EndHorizontal();
        }

        private void TryConnect()
        {
            if (!TryParse(_gateway, out string address, out ushort port))
            {
                _validation = "enter an address like 178.156.196.147:7000 or 127.0.0.1:7000";
                return;
            }
            _validation = "";
            PlayerPrefs.SetString(PrefGateway, _gateway.Trim());
            PlayerPrefs.SetString(PrefName, _name.Trim());
            PlayerPrefs.Save();
            Client.ConnectTo(address, port, _name);
        }

        public static bool TryParse(string text, out string address, out ushort port)
        {
            address = "";
            port = 7000;
            if (string.IsNullOrWhiteSpace(text)) return false;
            text = text.Trim();
            int colon = text.LastIndexOf(':');
            if (colon > 0)
            {
                if (!ushort.TryParse(text.Substring(colon + 1), out port) || port == 0) return false;
                address = text.Substring(0, colon);
            }
            else address = text;
            return address.Length > 0 && address.IndexOf(' ') < 0;
        }

        private void EnsureStyles()
        {
            if (_title != null) return;
            _box = new GUIStyle(GUI.skin.box);
            _box.normal.background = Texture2D.grayTexture;
            _title = new GUIStyle(GUI.skin.label) { fontSize = 26, fontStyle = FontStyle.Bold };
            _title.normal.textColor = Color.white;
            _label = new GUIStyle(GUI.skin.label) { fontSize = 14 };
            _label.normal.textColor = Color.white;
            _small = new GUIStyle(GUI.skin.label) { fontSize = 11, wordWrap = true };
            _small.normal.textColor = new Color(0.85f, 0.85f, 0.85f);
            _field = new GUIStyle(GUI.skin.textField) { fontSize = 15, fixedHeight = 28 };
            _button = new GUIStyle(GUI.skin.button) { fontSize = 13 };
        }
    }
}
