using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;

namespace DiscRipper
{
    internal sealed class VideoSelectionForm : Form
    {
        private readonly DataGridView grid = new DataGridView();
        private readonly bool movie;
        public List<int> SelectedTitleIds { get; private set; }

        public VideoSelectionForm(MediaKind kind, IList<VideoTitleInfo> titles)
        {
            movie = kind == MediaKind.Movie;
            Text = "Media Nexus ARM - Select " + (movie ? "Movie" : "TV Episode") + " Titles";
            StartPosition = FormStartPosition.CenterParent; Font = new Font("Segoe UI", 9F); Size = new Size(1050, 500); MinimumSize = new Size(850, 400);
            var root = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(12), RowCount = 3, ColumnCount = 1 };
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize)); root.RowStyles.Add(new RowStyle(SizeType.Percent, 100)); root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            root.Controls.Add(new Label { Text = movie ? "Confirm the main feature. Composite playlists are not selected automatically." : "Confirm the individual episode playlists. Play All and likely extras should remain unchecked.", AutoSize = true, Padding = new Padding(0, 0, 0, 8) });
            ConfigureGrid(); root.Controls.Add(grid, 0, 1);
            IEnumerable<VideoTitleInfo> ordered = movie ? (IEnumerable<VideoTitleInfo>)DiscAnalyzer.RankMovieCandidates(titles) : titles.Where(t => t.DurationSeconds >= 900).OrderBy(t => t.Id);
            List<int> suggested = movie ? ordered.Where(t => !t.Composite).Take(1).Select(t => t.Id).ToList() : DiscAnalyzer.SelectTvTitles(titles);
            foreach (VideoTitleInfo title in ordered)
            {
                int row = grid.Rows.Add(suggested.Contains(title.Id), title.Id, title.DurationText, title.SizeText, title.Chapters, title.Playlist, string.Join(",", title.Segments.ToArray()), title.SelectionReason ?? (title.Composite ? "Composite playlist" : "Candidate"));
                if (title.Composite) grid.Rows[row].DefaultCellStyle.ForeColor = Color.DarkOrange;
            }
            var buttons = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.RightToLeft, AutoSize = true, Padding = new Padding(0, 8, 0, 0) };
            var rip = new Button { Text = "Rip Selected", AutoSize = true }; var cancel = new Button { Text = "Cancel", DialogResult = DialogResult.Cancel, AutoSize = true };
            rip.Click += RipClicked; buttons.Controls.Add(rip); buttons.Controls.Add(cancel); root.Controls.Add(buttons, 0, 2); Controls.Add(root); AcceptButton = rip; CancelButton = cancel;
        }

        private void ConfigureGrid()
        {
            grid.Dock = DockStyle.Fill; grid.AllowUserToAddRows = false; grid.AllowUserToDeleteRows = false; grid.RowHeadersVisible = false; grid.AutoSizeRowsMode = DataGridViewAutoSizeRowsMode.AllCells;
            grid.Columns.Add(new DataGridViewCheckBoxColumn { HeaderText = "Rip", Width = 45 });
            grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "Title", Width = 50, ReadOnly = true });
            grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "Runtime", Width = 80, ReadOnly = true });
            grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "Size", Width = 75, ReadOnly = true });
            grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "Chapters", Width = 70, ReadOnly = true });
            grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "Playlist", Width = 95, ReadOnly = true });
            grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "Segments", Width = 260, ReadOnly = true });
            grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "Assessment", AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill, ReadOnly = true });
            if (movie) grid.CellContentClick += (s, e) => { if (e.ColumnIndex != 0 || e.RowIndex < 0) return; grid.CommitEdit(DataGridViewDataErrorContexts.Commit); for (int i = 0; i < grid.Rows.Count; i++) if (i != e.RowIndex) grid.Rows[i].Cells[0].Value = false; };
        }

        private void RipClicked(object sender, EventArgs e)
        {
            SelectedTitleIds = grid.Rows.Cast<DataGridViewRow>().Where(r => Convert.ToBoolean(r.Cells[0].Value)).Select(r => Convert.ToInt32(r.Cells[1].Value)).ToList();
            if (SelectedTitleIds.Count == 0) { MessageBox.Show(this, "Select at least one title.", "Media Nexus ARM", MessageBoxButtons.OK, MessageBoxIcon.Information); return; }
            if (movie && SelectedTitleIds.Count != 1) { MessageBox.Show(this, "Select exactly one main feature.", "Media Nexus ARM", MessageBoxButtons.OK, MessageBoxIcon.Information); return; }
            DialogResult = DialogResult.OK; Close();
        }
    }
}
