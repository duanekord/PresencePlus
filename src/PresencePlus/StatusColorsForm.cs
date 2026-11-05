namespace PresencePlus;

public sealed class StatusColorsForm : Form
{
    private readonly Dictionary<string, Button> choices = [];
    public AppSettings Settings { get; private set; }

    public StatusColorsForm(AppSettings settings)
    {
        settings.Validate();
        Settings = settings;
        Text = "Status colors";
        ClientSize = new Size(380, 380);
        AutoScaleMode = AutoScaleMode.Dpi;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        StartPosition = FormStartPosition.CenterParent;
        MaximizeBox = false;
        MinimizeBox = false;
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(16), ColumnCount = 2 };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 60));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 40));
        foreach (var option in StatusOptions.All)
        {
            layout.Controls.Add(new Label { Text = option.Label, AutoSize = true, Anchor = AnchorStyles.Left });
            if (option.Id == "Offline")
            {
                layout.Controls.Add(new Label { Text = "Light off", AutoSize = true, Anchor = AnchorStyles.Left });
                continue;
            }
            var button = new Button
            {
                Dock = DockStyle.Fill, Height = 32, Name = $"color-{option.Id}",
                AccessibleName = $"{option.Label} color", UseVisualStyleBackColor = false
            };
            button.Click += (_, _) =>
            {
                using var picker = new ColorDialog { Color = ColorTranslator.FromHtml(Settings.ColorFor(option.Id)), FullOpen = true };
                if (picker.ShowDialog(this) != DialogResult.OK) return;
                var color = picker.Color;
                Settings = Settings.WithStatusColor(option.Id, $"#{color.R:X2}{color.G:X2}{color.B:X2}");
                RefreshColors();
            };
            choices.Add(option.Id, button);
            layout.Controls.Add(button);
        }
        var reset = new Button { Text = "Reset defaults", AutoSize = true, Name = "reset-colors" };
        reset.Click += (_, _) =>
        {
            Settings = Settings with { StatusColors = System.Collections.Immutable.ImmutableDictionary<string, string>.Empty };
            RefreshColors();
        };
        var actions = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Fill, FlowDirection = FlowDirection.RightToLeft };
        var save = new Button { Text = "Save", AutoSize = true, DialogResult = DialogResult.OK, Name = "save-colors" };
        var cancel = new Button { Text = "Cancel", AutoSize = true, DialogResult = DialogResult.Cancel, Name = "cancel-colors" };
        actions.Controls.AddRange([save, cancel, reset]);
        layout.Controls.Add(actions);
        layout.SetColumnSpan(actions, 2);
        Controls.Add(layout);
        AcceptButton = save;
        CancelButton = cancel;
        RefreshColors();
    }

    private void RefreshColors()
    {
        foreach (var (id, button) in choices)
        {
            var color = Settings.ColorFor(id);
            button.Text = color;
            button.BackColor = ColorTranslator.FromHtml(color);
            button.ForeColor = ContrastText(button.BackColor);
            button.AccessibleDescription = $"Current color {color}. Choose a new color.";
        }
    }

    private static Color ContrastText(Color color)
    {
        static double Linear(byte value)
        {
            var channel = value / 255.0;
            return channel <= 0.04045 ? channel / 12.92 : Math.Pow((channel + 0.055) / 1.055, 2.4);
        }
        var luminance = 0.2126 * Linear(color.R) + 0.7152 * Linear(color.G) + 0.0722 * Linear(color.B);
        return luminance > 0.179 ? Color.Black : Color.White;
    }
}
