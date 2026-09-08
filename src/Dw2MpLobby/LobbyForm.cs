using System.Diagnostics;

namespace Dw2MpLobby;

/// <summary>
/// The lobby window: pick your empire, host or join, launch.
///
/// Hand-built rather than designer-generated so the whole thing is one readable file with
/// no .resx or .Designer.cs to keep in step.
/// </summary>
public sealed class LobbyForm : Form
{
    private readonly string _gamePath;
    private readonly List<RaceOption> _races;
    private readonly List<GovernmentOption> _governments;

    private readonly TextBox _playerName = new() { Text = Environment.UserName };
    private readonly TextBox _empireName = new() { Text = "New Empire" };
    private readonly ComboBox _race = new() { DropDownStyle = ComboBoxStyle.DropDownList };
    private readonly ComboBox _government = new() { DropDownStyle = ComboBoxStyle.DropDownList };
    private readonly Button _colour = new() { Text = "", FlatStyle = FlatStyle.Flat };

    private readonly RadioButton _roleHost = new() { Text = "Host a session", Checked = true };
    private readonly RadioButton _roleJoin = new() { Text = "Join a session" };

    private readonly ComboBox _mode = new() { DropDownStyle = ComboBoxStyle.DropDownList };
    private readonly NumericUpDown _stars = new() { Minimum = 10, Maximum = 200, Value = 30 };
    private readonly NumericUpDown _aiEmpires = new() { Minimum = 0, Maximum = 20, Value = 4 };
    private readonly NumericUpDown _seed = new() { Minimum = 0, Maximum = 999999, Value = 12345 };

    private readonly TextBox _address = new() { Text = "127.0.0.1" };
    private readonly NumericUpDown _port = new() { Minimum = 1024, Maximum = 65535, Value = 47800 };

    private readonly Button _launch = new() { Text = "Launch", Height = 34 };
    private readonly TextBox _status = new() { Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical, Height = 90 };

    private Color _empireColour = Color.SteelBlue;

    public LobbyForm(string gamePath)
    {
        _gamePath = gamePath;
        _races = GameData.LoadRaces(gamePath);
        _governments = GameData.LoadGovernments(gamePath);

        Text = "Distant Worlds 2 — Multiplayer Lobby";
        Width = 620;
        Height = 720;
        StartPosition = FormStartPosition.CenterScreen;
        Font = new Font("Segoe UI", 9f);

        BuildLayout();
        WireEvents();

        Log($"Game found: {gamePath}");
        Log($"{_races.Count} races, {_governments.Count} governments loaded from the game's data files.");
    }

    private void BuildLayout()
    {
        var root = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            Padding = new Padding(12),
            AutoScroll = true,
        };
        root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));

        _race.Items.AddRange(_races.ToArray());
        if (_races.Count > 0) _race.SelectedIndex = 0;

        _government.Items.AddRange(_governments.ToArray());
        if (_governments.Count > 0) _government.SelectedIndex = 0;

        _mode.Items.AddRange(new object[] { "Co-op — shared empire", "Competitive — separate empires" });
        _mode.SelectedIndex = 0;

        root.Controls.Add(Section("Your empire", Grid(
            ("Player name", _playerName),
            ("Empire name", _empireName),
            ("Race", _race),
            ("Government", _government),
            ("Colour", _colour))));

        root.Controls.Add(Section("Session", Grid(
            ("Role", Stack(_roleHost, _roleJoin)),
            ("Mode", _mode),
            ("Host address", _address),
            ("Port", _port))));

        root.Controls.Add(Section("Galaxy (host only)", Grid(
            ("Star systems", _stars),
            ("AI empires", _aiEmpires),
            ("Seed", _seed))));

        root.Controls.Add(_launch);
        root.Controls.Add(Section("Status", _status));

        Controls.Add(root);
    }

    private void WireEvents()
    {
        _race.SelectedIndexChanged += (_, _) =>
        {
            // Adopting the race's own colour is a better default than a fixed blue, and
            // it means a player who changes nothing still gets a sensible empire.
            if (_race.SelectedItem is RaceOption r) SetColour(r.DefaultColor);
        };

        _colour.Click += (_, _) =>
        {
            using var picker = new ColorDialog { Color = _empireColour, FullOpen = true };
            if (picker.ShowDialog(this) == DialogResult.OK) SetColour(picker.Color);
        };

        _roleHost.CheckedChanged += (_, _) => UpdateRoleEnabled();
        _launch.Click += (_, _) => Launch();

        SetColour(_races.FirstOrDefault()?.DefaultColor ?? Color.SteelBlue);
        UpdateRoleEnabled();
    }

    private void UpdateRoleEnabled()
    {
        bool host = _roleHost.Checked;

        // Galaxy settings belong to whoever generates the galaxy. Showing them as editable
        // to a joining player would imply their values matter, and they do not.
        _stars.Enabled = _aiEmpires.Enabled = _seed.Enabled = host;
        _mode.Enabled = host;
        _address.Enabled = !host;
        _launch.Text = host ? "Host and launch" : "Join and launch";
    }

    private void SetColour(Color c)
    {
        _empireColour = c;
        _colour.BackColor = c;
        _colour.ForeColor = c.GetBrightness() > 0.55f ? Color.Black : Color.White;
        _colour.Text = $"R{c.R} G{c.G} B{c.B}";
    }

    private void Launch()
    {
        try
        {
            var session = BuildSession();
            session.Save();

            Log($"Wrote {SessionDescriptor.DefaultPath}");
            Log($"Mode: {session.Mode}, slot {session.MySlot}, {session.Players.Count} player slot(s).");

            StartGame(session);

            Log("Distant Worlds 2 launched. Keep this window open for reference; you can close it once the game is up.");
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

        // NOTE: the joining player's empire is currently only known to their own machine.
        // Exchanging slots over the lobby protocol is the next piece; until then the host
        // generates using its own configuration and the client's choices apply only in
        // competitive mode once slot exchange exists.
        session.Players.Add(mine);

        return session;
    }

    private void StartGame(SessionDescriptor session)
    {
        var modDll = FindModDll();
        if (modDll is null)
            throw new FileNotFoundException(
                "Dw2Mp.dll not found. Expected it beside this launcher, or under src\\Dw2Mp\\bin\\Debug\\.");

        var psi = new ProcessStartInfo
        {
            FileName = Path.Combine(_gamePath, "DistantWorlds2.exe"),
            WorkingDirectory = _gamePath,
            UseShellExecute = false,
        };

        psi.ArgumentList.Add("--skip-splash");
        psi.ArgumentList.Add("--low-level-inject");
        psi.ArgumentList.Add(modDll);

        // The mod reads its configuration from the environment; session.json carries the
        // rest. Both are needed because the harness predates the lobby.
        psi.Environment["DW2MP_DETERMINISM"] = "1";
        psi.Environment["DW2MP_STEP_MS"] = "100";
        psi.Environment["DW2MP_PIN_BLOCKS"] = "1";
        psi.Environment["DW2MP_EXIT_WHEN_DONE"] = "0";
        psi.Environment["DW2MP_SNAPSHOT_CYCLES"] = "100000";
        psi.Environment["DW2MP_MAX_SNAPSHOTS"] = "2";
        psi.Environment["DW2MP_ROLE"] = _roleHost.Checked ? "host" : "client";
        psi.Environment["DW2MP_HOST"] = session.HostAddress;
        psi.Environment["DW2MP_PORT"] = session.Port.ToString();
        psi.Environment["DW2MP_RUN_LABEL"] = _roleHost.Checked ? "lobbyHost" : "lobbyClient";
        psi.Environment["DW2MP_SESSION"] = SessionDescriptor.DefaultPath;

        Log($"Launching: {psi.FileName} --low-level-inject \"{modDll}\"");
        Process.Start(psi);
    }

    private string FindModDll()
    {
        var here = AppContext.BaseDirectory;

        var candidates = new[]
        {
            Path.Combine(here, "Dw2Mp.dll"),
            Path.GetFullPath(Path.Combine(here, @"..\..\..\..\Dw2Mp\bin\Debug\Dw2Mp.dll")),
            Path.GetFullPath(Path.Combine(here, @"..\..\..\..\..\src\Dw2Mp\bin\Debug\Dw2Mp.dll")),
        };

        return candidates.FirstOrDefault(File.Exists);
    }

    private void Log(string line)
    {
        _status.AppendText(line + Environment.NewLine);
    }

    // ---- tiny layout helpers, so the builder above reads as structure ----

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
