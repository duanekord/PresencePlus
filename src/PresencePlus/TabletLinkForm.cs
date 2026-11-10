namespace PresencePlus;

public sealed class TabletLinkForm : Form
{
    public TabletLinkForm(IReadOnlyList<string> links)
    {
        Text = "Read-only tablet display";
        StartPosition = FormStartPosition.CenterParent;
        AutoScaleMode = AutoScaleMode.Dpi;
        ClientSize = new Size(640, 310);
        MinimumSize = new Size(480, 340);
        var layout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill, Padding = new Padding(16), ColumnCount = 1, RowCount = 4
        };
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.Controls.Add(new Label
        {
            Text = "On the tablet, open one of these complete links in its browser. Use the PC address reachable on the same trusted Wi-Fi/LAN.",
            AutoSize = true, MaximumSize = new Size(590, 0)
        });
        var addresses = new ListBox
        {
            Dock = DockStyle.Fill, HorizontalScrollbar = true, AccessibleName = "Tablet display links"
        };
        addresses.Items.AddRange(links.Cast<object>().ToArray());
        if (links.Count > 0) addresses.SelectedIndex = 0;
        layout.Controls.Add(addresses);
        layout.Controls.Add(new Label
        {
            Text = links.Count == 0
                ? "No private IPv4 address found. Connect the PC to a private Wi-Fi or Ethernet network, then reopen this dialog."
                : "Bookmark the complete link. It survives app restarts; reserve the PC's IP address in your router. Keep it private: HTTP is unencrypted. Allow TCP 47831 only on a trusted private network; never port-forward it.",
            AutoSize = true, MaximumSize = new Size(590, 0)
        });
        var actions = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Fill };
        var copy = new Button { Text = "Copy selected link", AutoSize = true, Enabled = links.Count > 0 };
        copy.Click += (_, _) =>
        {
            if (addresses.SelectedItem is not string link) return;
            try { Clipboard.SetText(link); }
            catch (System.Runtime.InteropServices.ExternalException)
            {
                MessageBox.Show(this, "The clipboard is busy. Try copying the link again.", "PresencePlus",
                    MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        };
        var close = new Button { Text = "Close", AutoSize = true, DialogResult = DialogResult.Cancel };
        actions.Controls.AddRange([copy, close]);
        layout.Controls.Add(actions);
        CancelButton = close;
        Controls.Add(layout);
    }
}
