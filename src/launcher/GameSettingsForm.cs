namespace TH12Coop;

internal sealed class GameSettingsForm : Form
{
    private readonly string gameDirectory;
    private readonly int initialBgm, initialSound;
    internal ComboBox Display { get; } = new() { DropDownStyle = ComboBoxStyle.DropDownList, Dock = DockStyle.Fill };
    internal CheckBox Prompt { get; } = new() { Text = "起動時に表示方法を選ぶ", AutoSize = true };
    internal NumericUpDown Bgm { get; } = new() { Minimum = 0, Maximum = 100, Increment = 5, Dock = DockStyle.Fill };
    internal NumericUpDown Sound { get; } = new() { Minimum = 0, Maximum = 100, Increment = 5, Dock = DockStyle.Fill };

    internal GameSettingsForm(string gameDirectory, byte[] configuration)
    {
        this.gameDirectory = gameDirectory;
        Text = "ゲーム設定 · このPC";
        StartPosition = FormStartPosition.CenterParent;
        AutoScaleMode = AutoScaleMode.Dpi;
        ClientSize = new Size(420, 250);
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = MinimizeBox = false;
        if (!GameConfiguration.Valid(configuration)) throw new IOException("TH12の設定ファイルが正しくありません。");
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(20), ColumnCount = 2, RowCount = 5 };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 100));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        var display = Display;
        display.Items.AddRange(["フルスクリーン", "ウィンドウ · 640 × 480", "ウィンドウ · 960 × 720", "ウィンドウ · 1280 × 960"]);
        display.SelectedIndex = Math.Clamp((int)configuration[0x1d], 0, 3);
        var prompt = Prompt; prompt.Checked = (configuration[0x39] & 1) != 0;
        initialBgm = GameConfiguration.EffectiveVolume(configuration, true);
        initialSound = GameConfiguration.EffectiveVolume(configuration, false);
        var bgm = Bgm; bgm.Value = initialBgm;
        var sound = Sound; sound.Value = initialSound;
        void Row(int index, string label, Control control)
        {
            layout.Controls.Add(new Label { Text = label, AutoSize = true, Anchor = AnchorStyles.Left }, 0, index);
            layout.Controls.Add(control, 1, index);
        }
        Row(0, "画面", display); Row(1, "", prompt); Row(2, "BGM (%)", bgm); Row(3, "効果音 (%)", sound);
        var save = new Button { Text = "保存", Dock = DockStyle.Fill };
        Row(4, "", save);
        save.Click += async (_, _) =>
        {
            save.Enabled = false;
            try
            {
                await SaveConfiguration(CancellationToken.None);
                DialogResult = DialogResult.OK;
                Close();
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            { MessageBox.Show(this, error.Message, Text, MessageBoxButtons.OK, MessageBoxIcon.Error); }
            finally { save.Enabled = true; }
        };
        AcceptButton = save;
        Controls.Add(layout);
    }

    internal async Task SaveConfiguration(CancellationToken token)
    {
        // Reload before merging so unrelated offline settings survive.
        var current = await GameConfiguration.ReadLocal(gameDirectory, token);
        current[0x1d] = (byte)Display.SelectedIndex;
        current[0x39] = (byte)((current[0x39] & ~1) | (Prompt.Checked ? 1 : 0));
        if ((int)Bgm.Value != initialBgm) GameConfiguration.ChangeVolume(current, true, (int)Bgm.Value);
        if ((int)Sound.Value != initialSound) GameConfiguration.ChangeVolume(current, false, (int)Sound.Value);
        await GameConfiguration.Save(Path.Combine(gameDirectory, "th12.cfg"), current, token);
    }
}
