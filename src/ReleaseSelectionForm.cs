using System;
using System.Collections.Generic;
using System.Drawing;
using System.Windows.Forms;

namespace DiscRipper
{
    internal sealed class ReleaseSelectionForm : Form
    {
        private readonly ListBox list = new ListBox();
        public MusicRelease SelectedRelease { get; private set; }
        public ReleaseSelectionForm(IList<MusicRelease> releases)
        {
            Text = "Media Nexus ARM — Select Music Release"; StartPosition = FormStartPosition.CenterParent; Font = new Font("Segoe UI", 9F); Size = new Size(720, 390);
            var root = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(12), RowCount = 3, ColumnCount = 1 };
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize)); root.RowStyles.Add(new RowStyle(SizeType.Percent, 100)); root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            root.Controls.Add(new Label { Text = "MusicBrainz found multiple releases for this disc. Select the matching edition:", AutoSize = true, Padding = new Padding(0, 0, 0, 8) });
            list.Dock = DockStyle.Fill; list.DisplayMember = "DisplayName"; foreach (MusicRelease release in releases) list.Items.Add(release); if (list.Items.Count > 0) list.SelectedIndex = 0;
            root.Controls.Add(list, 0, 1);
            var buttons = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.RightToLeft, AutoSize = true, Padding = new Padding(0, 8, 0, 0) };
            var use = new Button { Text = "Use Selected", AutoSize = true }; var pending = new Button { Text = "Rip Without Metadata", AutoSize = true, DialogResult = DialogResult.Ignore }; var cancel = new Button { Text = "Cancel", AutoSize = true, DialogResult = DialogResult.Cancel };
            use.Click += (s, e) => { SelectedRelease = list.SelectedItem as MusicRelease; if (SelectedRelease != null) { DialogResult = DialogResult.OK; Close(); } };
            buttons.Controls.Add(use); buttons.Controls.Add(pending); buttons.Controls.Add(cancel); root.Controls.Add(buttons, 0, 2); Controls.Add(root); AcceptButton = use; CancelButton = cancel;
        }
    }
}
