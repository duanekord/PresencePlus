namespace PresencePlus;

public sealed class PairingForm : Form
{
    private readonly TextBox setupCode = new() { UseSystemPasswordChar = true, Dock = DockStyle.Fill, AccessibleName = "Matter setup code" };
    private readonly TextBox ssid = new() { Dock = DockStyle.Fill, AccessibleName = "2.4 GHz Wi-Fi network name" };
    private readonly TextBox password = new() { UseSystemPasswordChar = true, Dock = DockStyle.Fill, AccessibleName = "Wi-Fi password" };
    private readonly CheckBox confirmation = new() { AutoSize = true, Text = "My light is in Matter commissioning mode." };
    private readonly Button pair = new() { Text = "Pair light", AutoSize = true, Enabled = false };
    private readonly Label result = new() { AutoSize = true, MaximumSize = new Size(420, 0) };
    private readonly CancellationTokenSource cancellation;

    public PairingForm(IMatterOutput output, CancellationToken shutdown)
    {
        cancellation = CancellationTokenSource.CreateLinkedTokenSource(shutdown);
        Text = "Pair Matter light";
        ClientSize = new Size(480, 400);
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        StartPosition = FormStartPosition.CenterParent;
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(20), AutoScroll = true, ColumnCount = 1 };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        layout.Controls.Add(new Label {
            Text = "Use the printed Matter code and 2.4 GHz Wi-Fi details. This does not reset your light. Pairing can take up to three minutes.",
            AutoSize = true, MaximumSize = new Size(420, 0)
        });
        foreach (var (label, field) in new[] {
            ("Matter setup code", setupCode), ("2.4 GHz Wi-Fi network name", ssid), ("Wi-Fi password", password)
        })
        {
            layout.Controls.Add(new Label { Text = label, AutoSize = true });
            layout.Controls.Add(field);
        }
        confirmation.CheckedChanged += (_, _) => pair.Enabled = confirmation.Checked;
        layout.Controls.Add(confirmation);
        layout.Controls.Add(result);
        var cancel = new Button { Text = "Cancel", AutoSize = true };
        cancel.Click += (_, _) => Close();
        var actions = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Fill };
        actions.Controls.AddRange([pair, cancel]);
        layout.Controls.Add(actions);
        Controls.Add(layout);
        AcceptButton = pair;
        CancelButton = cancel;
        pair.Click += async (_, _) =>
        {
            pair.Enabled = confirmation.Enabled = setupCode.Enabled = ssid.Enabled = password.Enabled = false;
            result.Text = "Pairing locally. Keep the light powered and Bluetooth enabled...";
            try
            {
                await output.PairAsync(setupCode.Text, ssid.Text, password.Text, cancellation.Token);
                if (!IsDisposed) { DialogResult = DialogResult.OK; Close(); }
            }
            catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
            catch (MatterException exception)
            {
                if (!IsDisposed) result.Text = exception.Message;
            }
            finally
            {
                if (!IsDisposed)
                {
                    pair.Enabled = confirmation.Checked;
                    confirmation.Enabled = setupCode.Enabled = ssid.Enabled = password.Enabled = true;
                    setupCode.Clear();
                    password.Clear();
                }
            }
        };
        FormClosing += (_, _) => cancellation.Cancel();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing && !IsDisposed)
        {
            cancellation.Cancel();
            setupCode.Clear();
            password.Clear();
            cancellation.Dispose();
        }
        base.Dispose(disposing);
    }
}
