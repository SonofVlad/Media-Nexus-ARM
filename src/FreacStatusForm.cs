using System;
using System.Drawing;
using System.Threading;
using System.Windows.Forms;

namespace DiscRipper
{
    internal sealed class FreacStatusForm : Form
    {
        private readonly FreacManager manager;
        private readonly Label installed = new Label();
        private readonly Label latest = new Label();
        private readonly Label status = new Label();
        private readonly Button install = new Button();
        private readonly ComboBox format = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 150 };

        public FreacStatusForm(FreacManager manager)
        {
            this.manager = manager;
            Text = "Media Nexus ARM - Audio Engine";
            StartPosition = FormStartPosition.CenterParent;
            Font = new Font("Segoe UI", 9F);
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false; MinimizeBox = false;
            ClientSize = new Size(450, 270);
            var root = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(16), ColumnCount = 2, RowCount = 6 };
            root.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 135)); root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            AddRow(root, 0, "Engine", new Label { Text = "fre:ac portable (stable)", AutoSize = true });
            AddRow(root, 1, "Installed", installed);
            AddRow(root, 2, "Latest stable", latest);
            AddRow(root, 3, "Status", status);
            format.Items.AddRange(new object[] { "ALAC", "FLAC", "MP3" }); format.SelectedItem = AppSettings.LoadAudioFormat().ToString();
            AddRow(root, 4, "Audio format", format);
            var buttons = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.RightToLeft, AutoSize = true, Padding = new Padding(0, 12, 0, 0) };
            var save = new Button { Text = "Save", AutoSize = true }; var close = new Button { Text = "Cancel", DialogResult = DialogResult.Cancel, AutoSize = true };
            save.Click += (s, e) => { AppSettings.SaveAudioFormat((AudioFormat)Enum.Parse(typeof(AudioFormat), Convert.ToString(format.SelectedItem))); DialogResult = DialogResult.OK; Close(); };
            install.AutoSize = true; install.Click += InstallClicked;
            buttons.Controls.Add(save); buttons.Controls.Add(close); buttons.Controls.Add(install); root.Controls.Add(buttons, 0, 5); root.SetColumnSpan(buttons, 2);
            Controls.Add(root); AcceptButton = save; CancelButton = close; ThemeSettings.Apply(this); RefreshStatus();
        }

        private static void AddRow(TableLayoutPanel panel, int row, string name, Control value)
        {
            panel.Controls.Add(new Label { Text = name, AutoSize = true, Font = new Font("Segoe UI", 9F, FontStyle.Bold) }, 0, row);
            value.AutoSize = true; panel.Controls.Add(value, 1, row);
        }

        private void RefreshStatus()
        {
            installed.Text = manager.InstalledVersion;
            latest.Text = manager.LatestStableVersion;
            bool current = string.Equals(manager.InstalledVersion, manager.LatestStableVersion, StringComparison.OrdinalIgnoreCase);
            status.Text = current ? "Up to date" : manager.InstalledVersion == "Not installed" ? "Installed automatically on first audio rip" : "Update available";
            status.ForeColor = ThemeSettings.IsDark() ? Color.White : Color.Black;
            install.Text = manager.InstalledVersion == "Not installed" ? "Install Now" : "Check / Repair";
        }

        private async void InstallClicked(object sender, EventArgs e)
        {
            install.Enabled = false; status.Text = "Checking official stable package..."; status.ForeColor = ThemeSettings.IsDark() ? Color.White : Color.Black;
            try
            {
                await manager.EnsureInstalledAsync(text => BeginInvoke(new Action(() => status.Text = text)), CancellationToken.None);
                RefreshStatus();
            }
            catch (Exception ex) { status.Text = "Error: " + ex.Message; status.ForeColor = ThemeSettings.IsDark() ? Color.White : Color.Black; }
            finally { install.Enabled = true; }
        }
    }
}
