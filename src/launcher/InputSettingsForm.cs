namespace TH12Coop;

internal sealed class InputSettingsForm : Form
{
    private readonly InputSettings original;
    private readonly BindingEditor one, two;
    private Button? capturing;
    private Action<int>? captureKey;
    internal InputSettings Settings { get; private set; }

    internal InputSettingsForm(InputSettings settings)
    {
        original = Settings = settings;
        Text = "入力設定"; Font = new Font("Yu Gothic UI", 10F);
        ClientSize = new Size(600, 660); MinimumSize = new Size(616, 699);
        StartPosition = FormStartPosition.CenterParent;
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(14), ColumnCount = 1, RowCount = 2 };
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 44));
        Controls.Add(layout);
        var tabs = new TabControl { Dock = DockStyle.Fill };
        one = new BindingEditor(this, settings.PlayerOne, new());
        two = new BindingEditor(this, settings.PlayerTwo, InputBindingProfile.Second);
        foreach (var (name, editor) in new[] { ("1P / オンラインの自分", one), ("オフライン 2P", two) })
        {
            var tab = new TabPage(name); editor.Dock = DockStyle.Fill; tab.Controls.Add(editor); tabs.TabPages.Add(tab);
        }
        layout.Controls.Add(tabs, 0, 0);
        var actions = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.RightToLeft };
        var cancel = new Button { Text = "キャンセル", AutoSize = true, DialogResult = DialogResult.Cancel };
        var save = new Button { Text = "保存", AutoSize = true };
        save.Click += (_, _) =>
        {
            try
            {
                Settings = original with { PlayerOne = one.Value(), PlayerTwo = two.Value() };
                Settings.Validate(); DialogResult = DialogResult.OK;
            }
            catch (IOException error) { MessageBox.Show(this, error.Message, Text, MessageBoxButtons.OK, MessageBoxIcon.Error); }
        };
        actions.Controls.Add(cancel); actions.Controls.Add(save); layout.Controls.Add(actions, 0, 1);
        CancelButton = cancel;
    }

    private void BeginKeyCapture(Button button, Action<int> accept)
    {
        if (capturing != null) capturing.Text = KeyName((int)capturing.Tag!);
        capturing = button; captureKey = accept;
        button.Text = "キーを押す"; button.Focus();
    }
    protected override bool ProcessCmdKey(ref Message message, Keys keyData)
    {
        if (capturing == null) return base.ProcessCmdKey(ref message, keyData);
        var key = keyData & Keys.KeyCode;
        if (key == Keys.ShiftKey) key = (Native.GetAsyncKeyState((int)Keys.RShiftKey) & 0x8000) != 0 ? Keys.RShiftKey : Keys.LShiftKey;
        if (key == Keys.ControlKey) key = (Native.GetAsyncKeyState((int)Keys.RControlKey) & 0x8000) != 0 ? Keys.RControlKey : Keys.LControlKey;
        if (key == Keys.Menu) key = (Native.GetAsyncKeyState((int)Keys.RMenu) & 0x8000) != 0 ? Keys.RMenu : Keys.LMenu;
        captureKey!((int)key); capturing = null; captureKey = null;
        return true;
    }
    private static string KeyName(int key) => key == 0 ? "なし" : ((Keys)key).ToString();

    private sealed class BindingEditor : UserControl
    {
        private readonly InputSettingsForm owner;
        private readonly InputBindingProfile defaults;
        private readonly ComboBox device = NewSelection();
        private readonly ComboBox horizontal = NewSelection(), vertical = NewSelection();
        private readonly CheckBox invertX = new() { Text = "左右反転", AutoSize = true }, invertY = new() { Text = "上下反転", AutoSize = true };
        private readonly CheckBox pov = new() { Text = "十字キー / POV", AutoSize = true };
        private readonly NumericUpDown deadZone = new() { Minimum = 1, Maximum = 90, Width = 70 };
        private readonly Button[] keys = new Button[8];
        private readonly ComboBox[] buttons = new ComboBox[8];
        private static readonly string[] XInputButtonNames = ["A", "B", "X", "Y", "LB", "RB", "Back", "Start", "L押込", "R押込", "LT", "RT"];
        private string currentDevice;

        internal BindingEditor(InputSettingsForm owner, InputBindingProfile value, InputBindingProfile defaults)
        {
            this.owner = owner; this.defaults = defaults; currentDevice = value.Device;
            AutoScroll = true;
            var layout = new TableLayoutPanel { Dock = DockStyle.Top, Padding = new Padding(8), AutoSize = true, ColumnCount = 3 };
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 110));
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
            Controls.Add(layout);
            var refresh = new Button { Text = "再検索", AutoSize = true };
            refresh.Click += (_, _) => { currentDevice = (device.SelectedItem as GamepadDevice)?.Id ?? currentDevice; RefreshDevices(); };
            Add(layout, 0, "デバイス", device, refresh);
            string[] axisNames = ["なし", "X / 左スティック X", "Y / 左スティック Y", "Z / 右スティック X", "R / 右スティック Y", "U", "V"];
            horizontal.Items.AddRange(axisNames); vertical.Items.AddRange(axisNames);
            Add(layout, 1, "左右", horizontal, invertX); Add(layout, 2, "上下", vertical, invertY);
            var deadZoneRow = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Fill };
            deadZoneRow.Controls.Add(deadZone); deadZoneRow.Controls.Add(new Label { Text = "%", AutoSize = true, Padding = new Padding(0, 5, 0, 0) });
            Add(layout, 3, "デッドゾーン", deadZoneRow, pov);
            Add(layout, 4, "操作", new Label { Text = "キーボード", AutoSize = true }, new Label { Text = "パッド", AutoSize = true });
            string[] names = ["上", "下", "左", "右", "ショット / 決定", "ボム / 戻る", "低速", "ポーズ"];
            for (var i = 0; i < 8; i++)
            {
                var keyButton = new Button { Dock = DockStyle.Fill, Height = 30 };
                keys[i] = keyButton;
                keyButton.Click += (_, _) => owner.BeginKeyCapture(keyButton, key => { keyButton.Tag = key; keyButton.Text = KeyName(key); });
                var menu = new ContextMenuStrip();
                menu.Items.Add("割当なし", null, (_, _) => { keyButton.Tag = 0; keyButton.Text = "なし"; });
                keyButton.ContextMenuStrip = menu;
                var button = buttons[i] = NewSelection();
                button.Items.Add("なし");
                for (var n = 0; n < 32; n++) button.Items.Add((n + 1).ToString());
                Add(layout, i + 5, names[i], keyButton, button);
            }
            var reset = new Button { Text = "初期設定", AutoSize = true };
            reset.Click += (_, _) => Apply(this.defaults);
            layout.Controls.Add(reset, 0, 13);
            device.SelectedIndexChanged += (_, _) => UpdateButtonNames();
            Apply(value);
        }

        private static ComboBox NewSelection() => new() { DropDownStyle = ComboBoxStyle.DropDownList, Dock = DockStyle.Fill };
        private static void Add(TableLayoutPanel layout, int row, string name, Control first, Control second)
        {
            layout.Controls.Add(new Label { Text = name, AutoSize = true, Anchor = AnchorStyles.Left }, 0, row);
            first.Margin = second.Margin = new Padding(3, 5, 3, 5);
            layout.Controls.Add(first, 1, row); layout.Controls.Add(second, 2, row);
        }
        private void RefreshDevices()
        {
            device.Items.Clear(); device.Items.Add(new GamepadDevice("", "キーボードのみ"));
            foreach (var found in GamepadDevices.Enumerate()) device.Items.Add(found);
            if (currentDevice.Length != 0 && !device.Items.Cast<GamepadDevice>().Any(item => item.Id == currentDevice))
                device.Items.Add(new GamepadDevice(currentDevice, $"未接続 · {currentDevice}"));
            device.SelectedItem = device.Items.Cast<GamepadDevice>().First(item => item.Id == currentDevice);
        }
        private void UpdateButtonNames()
        {
            var xinput = (device.SelectedItem as GamepadDevice)?.Id.StartsWith("xinput:", StringComparison.Ordinal) == true;
            foreach (var combo in buttons)
            {
                var selected = combo.SelectedIndex;
                combo.BeginUpdate();
                combo.Items.Clear(); combo.Items.Add("なし");
                var count = xinput ? XInputButtonNames.Length : 32;
                for (var n = 0; n < count; n++) combo.Items.Add(xinput ? $"{n + 1} · {XInputButtonNames[n]}" : (n + 1).ToString());
                combo.SelectedIndex = selected >= 0 && selected < combo.Items.Count ? selected : 0;
                combo.EndUpdate();
            }
        }
        private void Apply(InputBindingProfile value)
        {
            currentDevice = value.Device; RefreshDevices();
            horizontal.SelectedIndex = value.HorizontalAxis + 1; vertical.SelectedIndex = value.VerticalAxis + 1;
            invertX.Checked = value.InvertHorizontal; invertY.Checked = value.InvertVertical; pov.Checked = value.UsePov;
            deadZone.Value = value.DeadZone;
            for (var i = 0; i < 8; i++)
            {
                keys[i].Tag = value.Keys[i]; keys[i].Text = KeyName(value.Keys[i]);
                buttons[i].SelectedIndex = value.Buttons[i] + 1 < buttons[i].Items.Count ? value.Buttons[i] + 1 : 0;
            }
        }
        internal InputBindingProfile Value() => new()
        {
            Device = ((GamepadDevice)device.SelectedItem!).Id,
            Keys = keys.Select(button => (int)button.Tag!).ToArray(), Buttons = buttons.Select(combo => combo.SelectedIndex - 1).ToArray(),
            HorizontalAxis = horizontal.SelectedIndex - 1, VerticalAxis = vertical.SelectedIndex - 1,
            InvertHorizontal = invertX.Checked, InvertVertical = invertY.Checked, UsePov = pov.Checked, DeadZone = (int)deadZone.Value,
        };
    }
}
