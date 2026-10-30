using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace TH12Coop;

internal sealed class LauncherForm : Form
{
    private static readonly Color Background = Color.FromArgb(19, 22, 29), Surface = Color.FromArgb(30, 35, 45), Foreground = Color.FromArgb(236, 240, 247), Accent = Color.FromArgb(121, 205, 201);
    private readonly ComboBox mode = new() { DropDownStyle = ComboBoxStyle.DropDownList };
    private readonly ComboBox games = new() { DropDownStyle = ComboBoxStyle.DropDownList, Enabled = false };
    private readonly Button browse = new() { Text = "参照", Enabled = false };
    private readonly ComboBox characterOne = new() { DropDownStyle = ComboBoxStyle.DropDownList }, characterTwo = new() { DropDownStyle = ComboBoxStyle.DropDownList };
    private readonly Label characterOneLabel = new() { Text = "1P", AutoSize = true, Anchor = AnchorStyles.Left }, characterTwoLabel = new() { Text = "2P", AutoSize = true, Anchor = AnchorStyles.Left };
    private readonly Button inputSettingsButton = new() { Text = "入力設定", AutoSize = true }, gameSettingsButton = new() { Text = "ゲーム設定", AutoSize = true };
    private InputSettings inputSettings = new();
    private bool inputSettingsReady;
    private readonly TextBox address = new() { Text = "127.0.0.1" };
    private readonly Label addressLabel;
    private readonly NumericUpDown port = new() { Minimum = 1024, Maximum = 65535, Value = 3012 };
    private RoomIdentity room = RoomIdentity.Create();
    private readonly TextBox code = new() { MaxLength = 120 };
    private readonly NumericUpDown delay = new() { Minimum = 1, Maximum = 12, Value = 3 };
    private readonly Label delayLabel;
    private readonly Button generate = new() { Text = "再発行" }, copy = new() { Text = "コピー" };
    private readonly Button start = new() { Text = "2人で開始" }, stop = new() { Text = "終了", Enabled = false };
    private readonly Label state = new() { Text = "準備完了", AutoSize = true };
    private readonly Label networkHint = new() { AutoSize = true };
    private readonly TableLayoutPanel network = new() { ColumnCount = 2, RowCount = 4, AutoSize = true, Dock = DockStyle.Top };
    private readonly string? initialGameDirectory;
    private readonly bool autoStart;
    private CancellationTokenSource? lifetime;
    private Task? running;
    private bool closing;
    private int previousMode = -1;
    private string joinAddress = "127.0.0.1";
    private string joinCode = "";

    public LauncherForm(string? gameDirectory, bool autoStart)
    {
        initialGameDirectory = gameDirectory == null ? null : Path.GetFullPath(gameDirectory);
        this.autoStart = autoStart;
        Text = "東方星蓮船 CO-OP";
        BackColor = Background;
        ForeColor = Foreground;
        Font = new Font("Yu Gothic UI", 10F);
        ClientSize = new Size(680, 745);
        MinimumSize = new Size(696, 784);
        StartPosition = FormStartPosition.CenterScreen;
        var content = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(28), ColumnCount = 1, RowCount = 10 };
        content.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        for (var i = 0; i < 9; i++) content.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        content.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        Controls.Add(content);

        content.Controls.Add(new Label { Text = "東方星蓮船", AutoSize = true, Font = new Font(Font.FontFamily, 22, FontStyle.Bold), Margin = new Padding(0, 0, 0, 2) });
        content.Controls.Add(new Label { Text = "CO-OP", AutoSize = true, ForeColor = Accent, Font = new Font(Font.FontFamily, 14, FontStyle.Bold), Margin = new Padding(1, 0, 0, 22) });
        var gameRow = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, ColumnCount = 3, Margin = new Padding(0, 0, 0, 14) };
        gameRow.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 72));
        gameRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        gameRow.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 72));
        gameRow.Controls.Add(new Label { Text = "ゲーム", AutoSize = true, Anchor = AnchorStyles.Left }, 0, 0);
        games.Dock = browse.Dock = DockStyle.Fill;
        gameRow.Controls.Add(games, 1, 0); gameRow.Controls.Add(browse, 2, 0);
        content.Controls.Add(gameRow);
        mode.Items.AddRange(["オフライン · このPCで2人", "オンライン · 部屋を作成", "オンライン · 部屋に参加"]);
        mode.SelectedIndex = 0;
        mode.Dock = DockStyle.Top;
        mode.Margin = new Padding(0, 0, 0, 18);
        content.Controls.Add(mode);

        network.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 112));
        network.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        addressLabel = AddField(network, 0, "ホストIP", address);
        AddField(network, 1, "ポート", port);
        var codeRow = new TableLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, ColumnCount = 3, Margin = Padding.Empty };
        codeRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        codeRow.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 66));
        codeRow.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 66));
        code.Font = new Font("Consolas", 10F);
        code.Dock = DockStyle.Fill;
        generate.Dock = copy.Dock = DockStyle.Fill;
        codeRow.Controls.Add(code, 0, 0); codeRow.Controls.Add(copy, 1, 0); codeRow.Controls.Add(generate, 2, 0);
        AddField(network, 2, "ルームコード", codeRow);
        delayLabel = AddField(network, 3, "遅延フレーム", delay);
        network.Margin = new Padding(0, 0, 0, 12);
        content.Controls.Add(network);
        networkHint.MaximumSize = new Size(620, 0);
        networkHint.Margin = new Padding(0, 0, 0, 20);
        networkHint.ForeColor = Color.FromArgb(173, 183, 199);
        content.Controls.Add(networkHint);

        var players = new TableLayoutPanel { AutoSize = true, ColumnCount = 2, Dock = DockStyle.Top, Padding = new Padding(12), BackColor = Surface, Margin = new Padding(0, 0, 0, 22) };
        players.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 72));
        players.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        foreach (var character in new[] { characterOne, characterTwo })
        {
            character.Items.AddRange(["霊夢 A", "霊夢 B", "魔理沙 A", "魔理沙 B", "早苗 A", "早苗 B"]);
            character.SelectedIndex = 0; character.Dock = DockStyle.Fill;
            character.Margin = new Padding(0, 3, 0, 9);
        }
        players.Controls.Add(characterOneLabel, 0, 0); players.Controls.Add(characterOne, 1, 0);
        players.Controls.Add(characterTwoLabel, 0, 1); players.Controls.Add(characterTwo, 1, 1);
        var settingsRow = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Fill, Margin = new Padding(0, 6, 0, 0) };
        settingsRow.Controls.Add(inputSettingsButton); settingsRow.Controls.Add(gameSettingsButton);
        players.Controls.Add(settingsRow, 1, 2);
        content.Controls.Add(players);
        var buttons = new TableLayoutPanel { ColumnCount = 2, Dock = DockStyle.Top, Height = 45, Margin = new Padding(0, 0, 0, 14) };
        buttons.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 72));
        buttons.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 28));
        start.Dock = stop.Dock = DockStyle.Fill;
        start.BackColor = Accent; start.ForeColor = Background;
        buttons.Controls.Add(start, 0, 0); buttons.Controls.Add(stop, 1, 0);
        content.Controls.Add(buttons);
        state.MaximumSize = new Size(615, 0);
        state.ForeColor = Accent;
        content.Controls.Add(state);

        StyleControls(this);
        start.BackColor = Accent; start.ForeColor = Background;
        mode.SelectedIndexChanged += (_, _) => SetMode();
        generate.Click += (_, _) => { room.Dispose(); room = RoomIdentity.Create(); code.Text = room.Invite; };
        copy.Click += (_, _) => Clipboard.SetText(code.Text);
        start.Click += async (_, _) => await StartSession();
        browse.Click += async (_, _) => await BrowseGame();
        inputSettingsButton.Click += (_, _) => EditInputSettings();
        gameSettingsButton.Click += async (_, _) => await EditGameSettings();
        stop.Click += (_, _) => lifetime?.Cancel();
        FormClosing += OnClosing;
        SetMode();
        start.Enabled = false;
        Shown += async (_, _) =>
        {
            string? settingsError = null;
            try
            {
                inputSettings = InputSettingsStore.Load();
                characterOne.SelectedIndex = inputSettings.CharacterOne;
                characterTwo.SelectedIndex = inputSettings.CharacterTwo;
                inputSettingsReady = true;
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException) { settingsError = error.Message; }
            try
            {
                SetStatus("ゲームを確認しています");
                foreach (var choice in await GameLibrary.Discover(initialGameDirectory, CancellationToken.None)) games.Items.Add(choice);
                if (games.Items.Count > 0) games.SelectedIndex = 0;
                SetStatus(settingsError ?? (games.Items.Count == 0 ? "参照からTH12のフォルダーを選択してください" : "準備完了"));
            }
            catch (Exception error) { SetStatus(error.Message); }
            finally { games.Enabled = browse.Enabled = start.Enabled = true; }
            if (this.autoStart && inputSettingsReady && games.SelectedItem != null) await StartSession();
        };
    }

    private void EditInputSettings()
    {
        using var dialog = new InputSettingsForm(inputSettings);
        if (dialog.ShowDialog(this) != DialogResult.OK) return;
        try { InputSettingsStore.Save(dialog.Settings); inputSettings = dialog.Settings; inputSettingsReady = true; SetStatus("入力設定を保存しました"); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { SetStatus(error.Message); }
    }

    private async Task EditGameSettings()
    {
        if (games.SelectedItem is not GameChoice choice) { SetStatus("TH12のフォルダーを選択してください。"); return; }
        start.Enabled = mode.Enabled = games.Enabled = browse.Enabled = gameSettingsButton.Enabled = false;
        try
        {
            var directory = await GameLibrary.Prepare(choice, CancellationToken.None);
            using var dialog = new GameSettingsForm(directory);
            dialog.ShowDialog(this);
        }
        catch (Exception error) { SetStatus(error.Message); }
        finally { start.Enabled = mode.Enabled = games.Enabled = browse.Enabled = gameSettingsButton.Enabled = true; }
    }

    private async Task BrowseGame()
    {
        using var dialog = new FolderBrowserDialog { Description = "th12.exeがあるフォルダーを選択", UseDescriptionForTitle = true };
        if (dialog.ShowDialog(this) != DialogResult.OK) return;
        try
        {
            var directory = Path.GetFullPath(dialog.SelectedPath);
            var choice = new GameChoice(directory, await GameBuild.Identify(directory, CancellationToken.None));
            var existing = games.Items.Cast<GameChoice>().FirstOrDefault(item => item.Build.Id == choice.Build.Id);
            if (existing != null) games.Items.Remove(existing);
            games.Items.Add(choice); games.SelectedItem = choice;
            SetStatus("準備完了");
        }
        catch (Exception error) { SetStatus(error.Message); }
    }

    private static Label AddField(TableLayoutPanel parent, int row, string label, Control control)
    {
        var fieldLabel = new Label { Text = label, AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(0, 7, 0, 12) };
        parent.Controls.Add(fieldLabel, 0, row);
        control.Dock = DockStyle.Fill;
        control.Margin = new Padding(0, 3, 0, 9);
        parent.Controls.Add(control, 1, row);
        return fieldLabel;
    }

    private static void StyleControls(Control root)
    {
        foreach (Control control in root.Controls)
        {
            if (control is Button button)
            { button.FlatStyle = FlatStyle.Flat; button.FlatAppearance.BorderSize = 0; button.BackColor = Surface; button.ForeColor = Foreground; button.Cursor = Cursors.Hand; }
            if (control is TextBox or ComboBox or NumericUpDown) { control.BackColor = Surface; control.ForeColor = Foreground; }
            StyleControls(control);
        }
    }

    private void SetMode()
    {
        if (previousMode == 2) { joinAddress = address.Text; joinCode = code.Text; }
        var hosting = mode.SelectedIndex == 1;
        var online = mode.SelectedIndex != 0;
        characterOneLabel.Text = online ? "自分" : "1P";
        characterTwo.Visible = characterTwoLabel.Visible = !online;
        network.Visible = online;
        address.ReadOnly = mode.SelectedIndex != 2;
        addressLabel.Text = hosting ? "LAN IP" : "ホストIP";
        if (hosting) address.Text = LanAddresses();
        else if (mode.SelectedIndex == 2) address.Text = joinAddress;
        code.ReadOnly = hosting;
        if (hosting) code.Text = room.Invite;
        else if (mode.SelectedIndex == 2) code.Text = joinCode;
        generate.Visible = copy.Visible = hosting;
        delay.Visible = delayLabel.Visible = hosting;
        previousMode = mode.SelectedIndex;
        start.Text = mode.SelectedIndex switch { 0 => "2人で開始", 1 => "部屋を作成", _ => "参加" };
        networkHint.Text = mode.SelectedIndex switch
        {
            0 => "自機を2機にして同じステージを進みます。メニューとポーズは1Pが操作します。",
            1 => "相手に接続先IPとルームコードを伝えてください。双方のPCでゲームを実行します。",
            _ => "2人とも同じバージョンのゲームとMODを使用してください。"
        };
    }

    private static string LanAddresses()
    {
        try
        {
            var addresses = NetworkInterface.GetAllNetworkInterfaces()
                .Where(adapter => adapter.OperationalStatus == OperationalStatus.Up && adapter.NetworkInterfaceType != NetworkInterfaceType.Loopback)
                .Select(adapter => adapter.GetIPProperties())
                .OrderByDescending(properties => properties.GatewayAddresses.Any(gateway => gateway.Address.AddressFamily == AddressFamily.InterNetwork))
                .SelectMany(properties => properties.UnicastAddresses)
                .Select(unicast => unicast.Address)
                .Where(ip => ip.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(ip))
                .Where(ip => { var bytes = ip.GetAddressBytes(); return bytes[0] != 0 && bytes[0] < 224 && !(bytes[0] == 169 && bytes[1] == 254); })
                .Select(ip => ip.ToString()).Distinct().ToArray();
            return addresses.Length == 0 ? "LAN IPが見つかりません" : string.Join("  /  ", addresses);
        }
        catch (NetworkInformationException) { return "LAN IPを取得できません"; }
    }

    private void SetStatus(string text)
    {
        if (IsDisposed || Disposing) return;
        if (InvokeRequired) { try { BeginInvoke(() => SetStatus(text)); } catch (InvalidOperationException) { } return; }
        state.Text = text;
    }

    private async Task StartSession()
    {
        if (running != null) return;
        if (!inputSettingsReady) { SetStatus("入力設定を開き、割り当てを確認して保存してください。"); return; }
        if (games.SelectedItem is not GameChoice choice) { SetStatus("TH12のフォルダーを選択してください。"); return; }
        var selected = mode.SelectedIndex;
        var players = new PlayerSelection(characterOne.SelectedIndex, characterTwo.SelectedIndex);
        try
        {
            inputSettings = inputSettings with { CharacterOne = players.PlayerOne, CharacterTwo = players.PlayerTwo };
            InputSettingsStore.Save(inputSettings);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { SetStatus(error.Message); return; }
        var connectionCode = code.Text;
        var host = address.Text.Trim();
        if (selected != 0)
        {
            try { var parsed = RoomIdentity.Parse(connectionCode); System.Security.Cryptography.CryptographicOperations.ZeroMemory(parsed.Capability); }
            catch (ArgumentException e) { SetStatus(e.Message); return; }
            if (selected == 2 && string.IsNullOrWhiteSpace(host)) { SetStatus("ホストIPを入力してください。"); return; }
        }
        lifetime = new CancellationTokenSource();
        start.Enabled = mode.Enabled = games.Enabled = browse.Enabled = port.Enabled = generate.Enabled = delay.Enabled = false;
        characterOne.Enabled = characterTwo.Enabled = inputSettingsButton.Enabled = gameSettingsButton.Enabled = false;
        address.ReadOnly = code.ReadOnly = true;
        stop.Enabled = true;
        SetStatus(selected == 2 ? "接続しています" : "起動しています");
        try
        {
            running = RunSelected();
            async Task RunSelected()
            {
                SetStatus("MOD用のゲームを準備しています");
                var directory = await GameLibrary.Prepare(choice, lifetime.Token);
                switch (selected)
                {
                    case 0: await RunOffline(directory, players, lifetime.Token); break;
                    case 1: await OnlineSession.Host(directory, (int)port.Value, room, (int)delay.Value, SetStatus, lifetime.Token, localCharacter: players.PlayerOne); break;
                    default: await OnlineSession.Join(directory, host, (int)port.Value, connectionCode, SetStatus, lifetime.Token, localCharacter: players.PlayerOne); break;
                }
            }
            await running;
            SetStatus("ゲームを終了しました");
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { SetStatus("終了しました"); }
        catch (Exception e)
        {
            SetStatus(selected == 0 ? e.Message : "接続を終了しました。" + e.Message);
        }
        finally
        {
            lifetime.Dispose(); lifetime = null; running = null;
            if (selected == 1) { room.Dispose(); room = RoomIdentity.Create(); }
            start.Enabled = mode.Enabled = games.Enabled = browse.Enabled = port.Enabled = generate.Enabled = delay.Enabled = true; stop.Enabled = false;
            characterOne.Enabled = characterTwo.Enabled = inputSettingsButton.Enabled = gameSettingsButton.Enabled = true;
            SetMode();
            if (closing) { FormClosing -= OnClosing; Close(); }
        }
    }

    private async Task RunOffline(string gameDirectory, PlayerSelection players, CancellationToken token)
    {
        using var game = await GameSession.Start(gameDirectory, false, token, startupStatus: SetStatus, players: players);
        SetStatus("オフライン2人プレイ中");
        Native.SetForegroundWindow(game.Window);
        try { await game.Process.WaitForExitAsync(token); }
        finally { await game.StopGameAsync(); }
    }

    private void OnClosing(object? sender, FormClosingEventArgs e)
    {
        if (running == null) return;
        e.Cancel = true;
        closing = true;
        lifetime?.Cancel();
        SetStatus("終了しています");
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) room.Dispose();
        base.Dispose(disposing);
    }
}
