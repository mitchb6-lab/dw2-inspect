using System.Diagnostics;

namespace Dw2MpLobby;

/// <summary>
/// The launcher. Tabbed after the FAF client's shape:
///
///   Private   — direct two-player session. This is what works today.
///   Lobby     — Phase 3 placeholder: a server listing hosted games.
///   Mods      — what is installed and enabled, because mod mismatch breaks state sync.
///   Log       — connection status and relay throughput.
///
/// Hand-built rather than designer-generated, so the whole window is one readable file
/// with no .resx or .Designer.cs to keep in step.
/// </summary>
public sealed class LobbyForm : Form
{
    private readonly string _gamePath;
    private readonly List<RaceOption> _races;
    private readonly List<GovernmentOption> _governments;
    private List<InstalledMod> _mods = new();

    // --- empire ---
    private readonly TextBox _playerName = new() { Text = Environment.UserName };
    private readonly TextBox _empireName = new() { Text = "New Empire" };
    private readonly ComboBox _race = new() { DropDownStyle = ComboBoxStyle.DropDownList };
    private readonly ComboBox _government = new() { DropDownStyle = ComboBoxStyle.DropDownList };
    private readonly Button _colour = new() { FlatStyle = FlatStyle.Flat };

    // --- session ---
    private readonly RadioButton _roleHost = new() { Text = "Host", Checked = true };
    private readonly RadioButton _roleJoin = new() { Text = "Join" };
    private readonly ComboBox _mode = new() { DropDownStyle = ComboBoxStyle.DropDownList };
    private readonly ComboBox _transport = new() { DropDownStyle = ComboBoxStyle.DropDownList };
    private readonly ComboBox _myAddress = new() { DropDownStyle = ComboBoxStyle.DropDownList };
    private readonly Button _copyAddress = new() { Text = "Copy" };
    private readonly TextBox _address = new() { Text = "127.0.0.1" };
    private readonly Button _testConnection = new() { Text = "Test" };
    private readonly NumericUpDown _port = new() { Minimum = 1024, Maximum = 65535, Value = 47800 };

    // --- galaxy ---
    private readonly NumericUpDown _stars = new() { Minimum = 10, Maximum = 200, Value = 30 };
    private readonly NumericUpDown _aiEmpires = new() { Minimum = 0, Maximum = 20, Value = 4 };
    private readonly NumericUpDown _seed = new() { Minimum = 0, Maximum = 999999, Value = 12345 };

    private readonly Button _openLobby = new() { Text = "Open lobby", Height = 32 };
    private readonly ListBox _playerList = new() { Height = 76 };
    private readonly Button _launch = new() { Text = "Start session", Height = 36, Enabled = false };
    private readonly TextBox _log = new() { Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical };
    private readonly ListBox _modList = new();
    private readonly Label _modSummary = new() { AutoSize = true };
    private readonly StatusStrip _statusStrip = new();
    private readonly ToolStripStatusLabel _statusLabel = new() { Text = "Idle" };

    private List<LocalAddress> _localAddresses = new();
    private Color _empireColour = Color.SteelBlue;
    private Relay _relay;
    private LobbySession _lobby;
    private readonly System.Windows.Forms.Timer _statusTimer = new() { Interval = 1000 };

    /// <summary>
    /// Localhost port the game connects back on — not the peer port.
    ///
    /// Assigned per launcher instance rather than fixed, because a fixed one means two
    /// launchers on the same machine collide. That is exactly the configuration used for
    /// testing here, and it would also bite anyone running host and client on one PC.
    /// </summary>
    private readonly int _localGamePort = FreeLocalPort();

    /// <summary>Asks the OS for a free port by binding one and immediately releasing it.</summary>
    private static int FreeLocalPort()
    {
        var probe = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
        probe.Start();
        int port = ((System.Net.IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        return port;
    }

    public LobbyForm(string gamePath)
    {
        _gamePath = gamePath;
        _races = GameData.LoadRaces(gamePath);
        _governments = GameData.LoadGovernments(gamePath);

        Text = "Distant Worlds 2 — Multiplayer";
        Width = 720;
        Height = 780;
        StartPosition = FormStartPosition.CenterScreen;
        Font = new Font("Segoe UI", 9f);

        BuildLayout();
        WireEvents();

        Log($"Game: {gamePath}");
        Log($"{_races.Count} races, {_governments.Count} governments loaded.");
        RefreshMods();
        RefreshLocalAddresses();
    }

    // ------------------------------------------------------------- layout

    private void BuildLayout()
    {
        var tabs = new TabControl { Dock = DockStyle.Fill };
        tabs.TabPages.Add(BuildPrivateTab());
        tabs.TabPages.Add(BuildLobbyTab());
        tabs.TabPages.Add(BuildModsTab());
        tabs.TabPages.Add(BuildLogTab());

        _statusStrip.Items.Add(_statusLabel);

        Controls.Add(tabs);
        Controls.Add(_statusStrip);
    }

    private TabPage BuildPrivateTab()
    {
        var page = new TabPage("Private") { Padding = new Padding(10) };
        var root = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, AutoScroll = true };
        root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));

        _race.Items.AddRange(_races.ToArray());
        if (_races.Count > 0) _race.SelectedIndex = 0;

        _government.Items.AddRange(_governments.ToArray());
        if (_governments.Count > 0) _government.SelectedIndex = 0;

        _mode.Items.AddRange(new object[] { "Co-op — shared empire", "Competitive — separate empires" });
        _mode.SelectedIndex = 0;

        _transport.Items.AddRange(new object[] { "Direct TCP (LAN, or Tailscale/ZeroTier)", "Steam networking (not yet available)" });
        _transport.SelectedIndex = 0;

        root.Controls.Add(Section("Your empire", Grid(
            ("Player name", _playerName),
            ("Empire name", _empireName),
            ("Race", _race),
            ("Government", _government),
            ("Colour", _colour))));

        root.Controls.Add(Section("Connection", Grid(
            ("Role", Stack(_roleHost, _roleJoin)),
            ("Mode", _mode),
            ("Transport", _transport),
            ("Your address", Stack(_myAddress, _copyAddress)),
            ("Host address", Stack(_address, _testConnection)),
            ("Port", _port))));

        root.Controls.Add(Section("Galaxy (host only)", Grid(
            ("Star systems", _stars),
            ("AI empires", _aiEmpires),
            ("Seed", _seed))));

        root.Controls.Add(Section("Players", Stack2(_openLobby, _playerList, _launch)));
        page.Controls.Add(root);
        return page;
    }

    /// <summary>Vertical stack that fills width — for the players panel.</summary>
    private static Control Stack2(params Control[] controls)
    {
        var panel = new TableLayoutPanel { ColumnCount = 1, AutoSize = true, Dock = DockStyle.Top };
        panel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        foreach (var c in controls) { c.Dock = DockStyle.Top; panel.Controls.Add(c); }
        return panel;
    }

    /// <summary>
    /// Phase 3 placeholder. Present and explicit rather than absent, so the shape of where
    /// this is heading is visible — and so nobody wonders whether it exists and is broken.
    /// </summary>
    private static TabPage BuildLobbyTab()
    {
        var page = new TabPage("Lobby") { Padding = new Padding(10) };

        var list = new ListView
        {
            Dock = DockStyle.Fill,
            View = View.Details,
            FullRowSelect = true,
            Enabled = false,
        };
        list.Columns.Add("Host", 150);
        list.Columns.Add("Mode", 120);
        list.Columns.Add("Players", 70);
        list.Columns.Add("Mods", 120);
        list.Columns.Add("Galaxy", 120);

        var note = new Label
        {
            Dock = DockStyle.Top,
            Height = 96,
            Text =
                "Public lobby — not built yet (Phase 3).\r\n\r\n" +
                "This will list sessions other people are hosting, so you can join without " +
                "exchanging an address. It needs a small always-on server to hold the list; " +
                "the game connections themselves stay peer-to-peer.\r\n\r\n" +
                "Until then, use the Private tab and send someone your address directly.",
        };

        page.Controls.Add(list);
        page.Controls.Add(note);
        return page;
    }

    private TabPage BuildModsTab()
    {
        var page = new TabPage("Mods") { Padding = new Padding(10) };

        _modList.Dock = DockStyle.Fill;

        var note = new Label
        {
            Dock = DockStyle.Top,
            Height = 110,
            Text =
                "Both players must have the SAME mods enabled.\r\n\r\n" +
                "State transfer sends the game's own serialised galaxy, and mods change the " +
                "data it is built from — components, races, governments. A mismatch corrupts " +
                "the transfer exactly the way a version mismatch does, so the enabled set is " +
                "checked during the handshake.\r\n\r\n" +
                "Vanilla is the supported configuration for now. A shared mod vault would come " +
                "with the public lobby.",
        };

        var refresh = new Button { Text = "Refresh", Dock = DockStyle.Bottom, Height = 28 };
        refresh.Click += (_, _) => RefreshMods();

        page.Controls.Add(_modList);
        page.Controls.Add(_modSummary);
        page.Controls.Add(note);
        page.Controls.Add(refresh);
        return page;
    }

    private TabPage BuildLogTab()
    {
        var page = new TabPage("Log") { Padding = new Padding(10) };
        _log.Dock = DockStyle.Fill;
        page.Controls.Add(_log);
        return page;
    }

    // ------------------------------------------------------------- events

    private void WireEvents()
    {
        _race.SelectedIndexChanged += (_, _) =>
        {
            // The race's own colour is a better default than a fixed blue: a player who
            // changes nothing still ends up with a coherent empire.
            if (_race.SelectedItem is RaceOption r) SetColour(r.DefaultColor);
        };

        _colour.Click += (_, _) =>
        {
            using var picker = new ColorDialog { Color = _empireColour, FullOpen = true };
            if (picker.ShowDialog(this) == DialogResult.OK) SetColour(picker.Color);
        };

        _copyAddress.Click += (_, _) =>
        {
            if (_myAddress.SelectedItem is not LocalAddress a) return;
            Clipboard.SetText(a.Ip.ToString());
            Log($"Copied {a.Ip} — send this to whoever is joining.");
        };

        _testConnection.Click += async (_, _) =>
        {
            _testConnection.Enabled = false;
            Log($"Testing {_address.Text.Trim()}:{(int)_port.Value} ...");
            Log(await NetworkDiscovery.TestConnection(_address.Text.Trim(), (int)_port.Value));
            _testConnection.Enabled = true;
        };

        _transport.SelectedIndexChanged += (_, _) =>
        {
            if (_transport.SelectedIndex != 1) return;

            MessageBox.Show(this, SteamTransport.NotImplementedMessage,
                "Steam networking", MessageBoxButtons.OK, MessageBoxIcon.Information);
            _transport.SelectedIndex = 0;
        };

        _roleHost.CheckedChanged += (_, _) => UpdateRoleEnabled();
        _openLobby.Click += async (_, _) => await OpenLobby();
        _launch.Click += (_, _) => StartSession();

        _statusTimer.Tick += (_, _) => UpdateStatus();
        _statusTimer.Start();

        FormClosing += (_, _) => { _relay?.Dispose(); _lobby?.Dispose(); };

        SetColour(_races.FirstOrDefault()?.DefaultColor ?? Color.SteelBlue);
        UpdateRoleEnabled();
    }

    private void RefreshMods()
    {
        _mods = ModDetection.FindMods(_gamePath);

        _modList.Items.Clear();
        foreach (var m in _mods) _modList.Items.Add(m);

        int on = _mods.Count(m => m.Enabled);
        _modSummary.Text = on == 0
            ? $"Vanilla — {_mods.Count} mod(s) installed, none enabled."
            : $"{on} of {_mods.Count} mod(s) enabled. The other player must match exactly.";

        Log(on == 0
            ? "Mods: running vanilla (nothing enabled)."
            : $"Mods: {on} enabled — the other player must have the same set.");
    }

    private void RefreshLocalAddresses()
    {
        _localAddresses = NetworkDiscovery.FindLocalAddresses();

        _myAddress.Items.Clear();
        foreach (var a in _localAddresses) _myAddress.Items.Add(a);
        if (_myAddress.Items.Count > 0) _myAddress.SelectedIndex = 0;

        foreach (var a in _localAddresses.Where(a => a.Kind == AddressKind.FullTunnelVpn))
            Log($"WARNING: {a.Adapter} is connected. A full-tunnel VPN breaks peer connections — turn it off.");

        int internetCapable = _localAddresses.Count(a => a.ReachableOverInternet && a.Kind != AddressKind.OtherVirtual);

        Log(internetCapable > 0
            ? $"{internetCapable} internet-capable address(es) found — no port forwarding needed."
            : "No virtual-LAN adapter. LAN works as-is; for internet play install Tailscale or ZeroTier on both machines.");
    }

    private void UpdateRoleEnabled()
    {
        bool host = _roleHost.Checked;

        _stars.Enabled = _aiEmpires.Enabled = _seed.Enabled = host;
        _mode.Enabled = host;
        _myAddress.Enabled = _copyAddress.Enabled = host;
        _address.Enabled = _testConnection.Enabled = !host;
        _openLobby.Text = host ? "Open lobby" : "Connect to host";
    }

    private void UpdateStatus()
    {
        if (_relay is null) { _statusLabel.Text = "Idle"; return; }

        var s = _relay.Stats;
        _statusLabel.Text =
            $"Game: {(s.GameConnected ? "connected" : "waiting")}   " +
            $"Peer: {(s.PeerConnected ? "connected" : "waiting")}   " +
            $"{s.Frames} frames   ↑{s.ToPeer / 1024 / 1024.0:F1} MB   ↓{s.ToGame / 1024 / 1024.0:F1} MB";
    }

    private void SetColour(Color c)
    {
        _empireColour = c;
        _colour.BackColor = c;
        _colour.ForeColor = c.GetBrightness() > 0.55f ? Color.Black : Color.White;
        _colour.Text = $"R{c.R} G{c.G} B{c.B}";
    }

    // ------------------------------------------------------------- launch

    /// <summary>
    /// Stage one: negotiate. Both players configure an empire and agree a session BEFORE
    /// either game starts — which is the whole point, since a galaxy generated before the
    /// joiner's choices arrive cannot contain their empire.
    /// </summary>
    private async Task OpenLobby()
    {
        try
        {
            _openLobby.Enabled = false;
            _lobby = new LobbySession((int)_port.Value, Log);
            _lobby.SessionChanged += () => BeginInvoke(RefreshPlayerList);

            if (_roleHost.Checked)
            {
                // The host's own slot is slot 0 and exists before anyone joins.
                await _lobby.HostAsync(BuildSession(), CancellationToken.None);
            }
            else
            {
                _lobby.StartRequested += () => BeginInvoke(() => LaunchAgreedSession());
                await _lobby.JoinAsync(_address.Text.Trim(), BuildSession().Players[0], CancellationToken.None);
            }

            RefreshPlayerList();
        }
        catch (Exception ex)
        {
            _openLobby.Enabled = true;
            Log("Lobby failed: " + ex.Message);
            MessageBox.Show(this, ex.Message, "Lobby failed", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private void RefreshPlayerList()
    {
        _playerList.Items.Clear();

        var session = _lobby?.Session;
        if (session is null) return;

        foreach (var p in session.Players)
        {
            var race = _races.FirstOrDefault(r => r.Id == p.Empire.RaceId)?.Name ?? $"race {p.Empire.RaceId}";
            var gov = _governments.FirstOrDefault(g => g.Id == p.Empire.GovernmentId)?.Name ?? "";
            var me = p.Slot == session.MySlot ? "  <- you" : "";
            _playerList.Items.Add($"{p.Slot}: {p.Name} — \"{p.Empire.Name}\" ({race}, {gov}){me}");
        }

        // Only the host starts, and only with someone to play with.
        _launch.Enabled = _lobby.IsHost && session.Players.Count > 1;
        _launch.Text = _lobby.IsHost
            ? (session.Players.Count > 1 ? "Start session" : "Waiting for a player...")
            : "Waiting for the host to start...";
    }

    /// <summary>Stage two: host presses Start. Both sides launch from the same descriptor.</summary>
    private async void StartSession()
    {
        try
        {
            _launch.Enabled = false;
            await _lobby.StartAsync();
            LaunchAgreedSession();
        }
        catch (Exception ex)
        {
            Log("Start failed: " + ex.Message);
        }
    }

    private void LaunchAgreedSession()
    {
        try
        {
            var session = _lobby.Session ?? BuildSession();
            session.Save();
            Log($"Wrote {SessionDescriptor.DefaultPath} (slot {session.MySlot} of {session.Players.Count})");

            // Reuse the lobby's connection rather than reconnecting: the peers are already
            // linked and already agree, and a second listen/dial cycle would race.
            _relay = new Relay(
                _localGamePort,
                _lobby.HandOverTransport(),
                _lobby.IsHost,
                _address.Text.Trim(),
                Log);

            _relay.Start();

            StartGame(session);
            _launch.Enabled = false;
            _openLobby.Enabled = false;

            Log($"Launched. The game connects back on 127.0.0.1:{_localGamePort}.");
        }
        catch (Exception ex)
        {
            Log("FAILED: " + ex.Message);
            MessageBox.Show(this, ex.Message, "Launch failed", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private SessionDescriptor BuildSession()
    {
        bool host = _roleHost.Checked;

        var mine = new PlayerSlot
        {
            Slot = host ? 0 : 1,
            Name = _playerName.Text.Trim(),
            Empire = new EmpireConfig
            {
                Name = _empireName.Text.Trim(),
                RaceId = (_race.SelectedItem as RaceOption)?.Id ?? 0,
                GovernmentId = (_government.SelectedItem as GovernmentOption)?.Id ?? 0,
                R = _empireColour.R,
                G = _empireColour.G,
                B = _empireColour.B,
            },
        };

        var session = new SessionDescriptor
        {
            Mode = _mode.SelectedIndex == 1 ? "competitive" : "coop-shared",
            HostAddress = host ? "127.0.0.1" : _address.Text.Trim(),
            Port = (int)_port.Value,
            MySlot = mine.Slot,
            Galaxy = new GalaxySettings
            {
                Stars = (int)_stars.Value,
                AiEmpires = (int)_aiEmpires.Value,
                Seed = (int)_seed.Value,
            },
        };

        session.Players.Add(mine);
        return session;
    }

    private void StartGame(SessionDescriptor session)
    {
        var modDll = FindModDll()
            ?? throw new FileNotFoundException(
                "Dw2Mp.dll not found. Expected it beside this launcher, or under src\\Dw2Mp\\bin\\Debug\\.");

        var psi = new ProcessStartInfo
        {
            FileName = Path.Combine(_gamePath, "DistantWorlds2.exe"),
            WorkingDirectory = _gamePath,
            UseShellExecute = false,
        };

        psi.ArgumentList.Add("--skip-splash");
        psi.ArgumentList.Add("--continue");
        psi.ArgumentList.Add("--low-level-inject");
        psi.ArgumentList.Add(modDll);

        psi.Environment["DW2MP_DETERMINISM"] = "1";
        psi.Environment["DW2MP_STEP_MS"] = "100";
        psi.Environment["DW2MP_PIN_BLOCKS"] = "1";
        psi.Environment["DW2MP_EXIT_WHEN_DONE"] = "0";
        psi.Environment["DW2MP_SNAPSHOT_CYCLES"] = "100000";
        psi.Environment["DW2MP_MAX_SNAPSHOTS"] = "2";
        psi.Environment["DW2MP_ROLE"] = _roleHost.Checked ? "host" : "client";
        psi.Environment["DW2MP_MODE"] = session.Mode;
        psi.Environment["DW2MP_RUN_LABEL"] = _roleHost.Checked ? "lobbyHost" : "lobbyClient";
        psi.Environment["DW2MP_SESSION"] = SessionDescriptor.DefaultPath;

        // Phase 1: the game talks ONLY to us, on localhost. It is not told the peer
        // address or the peer port at all.
        psi.Environment["DW2MP_LOCAL_PORT"] = _localGamePort.ToString();

        Log($"Launching {psi.FileName}");
        Process.Start(psi);
    }

    private string FindModDll()
    {
        var here = AppContext.BaseDirectory;

        return new[]
        {
            Path.Combine(here, "Dw2Mp.dll"),
            Path.GetFullPath(Path.Combine(here, @"..\..\..\..\Dw2Mp\bin\Debug\Dw2Mp.dll")),
            Path.GetFullPath(Path.Combine(here, @"..\..\..\..\..\src\Dw2Mp\bin\Debug\Dw2Mp.dll")),
        }.FirstOrDefault(File.Exists);
    }

    private void Log(string line)
    {
        if (_log.InvokeRequired) { _log.BeginInvoke(() => Log(line)); return; }
        _log.AppendText(line + Environment.NewLine);
    }

    // ---- layout helpers, so the builders above read as structure ----

    private static Control Section(string title, Control content)
    {
        var box = new GroupBox { Text = title, Dock = DockStyle.Top, AutoSize = true, Padding = new Padding(8) };
        content.Dock = DockStyle.Top;
        box.Controls.Add(content);
        return box;
    }

    private static TableLayoutPanel Grid(params (string Label, Control Field)[] rows)
    {
        var grid = new TableLayoutPanel { ColumnCount = 2, AutoSize = true, Dock = DockStyle.Top };
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 120));
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));

        foreach (var (label, field) in rows)
        {
            field.Dock = DockStyle.Fill;
            if (field is TextBox or ComboBox or Button) field.Height = 24;

            grid.Controls.Add(new Label { Text = label, TextAlign = ContentAlignment.MiddleLeft, Dock = DockStyle.Fill });
            grid.Controls.Add(field);
        }

        return grid;
    }

    private static Control Stack(params Control[] controls)
    {
        var panel = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Fill, WrapContents = false };
        foreach (var c in controls) { c.AutoSize = true; panel.Controls.Add(c); }
        return panel;
    }
}
