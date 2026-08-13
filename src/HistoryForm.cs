using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Windows.Forms;

namespace DiscRipper
{
    internal sealed class HistoryEntry
    {
        public DateTime Date;
        public string Drive;
        public string Disc;
        public string MediaType;
        public string Result;
        public string Output;
        public string LogPath;
    }

    internal sealed class HistoryForm : Form
    {
        private readonly string logFolder;
        private readonly DataGridView grid = new DataGridView();
        private readonly Label summary = new Label { AutoSize = true, TextAlign = ContentAlignment.MiddleLeft };
        private List<HistoryEntry> entries = new List<HistoryEntry>();

        public HistoryForm(string outputRoot)
        {
            logFolder = Path.Combine(outputRoot, "Logs");
            Text = "Media Nexus ARM - History"; StartPosition = FormStartPosition.CenterParent; Font = new Font("Segoe UI", 9F);
            Size = new Size(980, 520); MinimumSize = new Size(760, 400);
            var root = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(12), ColumnCount = 1, RowCount = 3 };
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize)); root.RowStyles.Add(new RowStyle(SizeType.Percent, 100)); root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            summary.Padding = new Padding(0, 0, 0, 8); root.Controls.Add(summary, 0, 0);
            ConfigureGrid(); root.Controls.Add(grid, 0, 1);
            var buttons = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.RightToLeft, AutoSize = true, Padding = new Padding(0, 10, 0, 0) };
            var close = new Button { Text = "Close", DialogResult = DialogResult.OK, AutoSize = true };
            var viewLog = new Button { Text = "View Log", AutoSize = true };
            var openOutput = new Button { Text = "Open Output", AutoSize = true };
            var refresh = new Button { Text = "Refresh", AutoSize = true, Margin = new Padding(3, 3, 18, 3) };
            viewLog.Click += (s, e) => OpenSelectedLog(); openOutput.Click += (s, e) => OpenSelectedOutput(); refresh.Click += (s, e) => LoadHistory();
            buttons.Controls.Add(close); buttons.Controls.Add(viewLog); buttons.Controls.Add(openOutput); buttons.Controls.Add(refresh); root.Controls.Add(buttons, 0, 2);
            Controls.Add(root); AcceptButton = close; CancelButton = close; ThemeSettings.Apply(this); LoadHistory();
        }

        private void ConfigureGrid()
        {
            grid.Dock = DockStyle.Fill; grid.ReadOnly = true; grid.AllowUserToAddRows = false; grid.AllowUserToDeleteRows = false;
            grid.AllowUserToResizeRows = false; grid.RowHeadersVisible = false; grid.SelectionMode = DataGridViewSelectionMode.FullRowSelect; grid.MultiSelect = false;
            grid.AutoGenerateColumns = false; grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "Date", Width = 145 });
            grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "Drive", Width = 55 });
            grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "Disc", Width = 210 });
            grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "Media Type", Width = 100 });
            grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "Result", Width = 85 });
            grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "Output", AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill });
            grid.CellDoubleClick += (s, e) => { if (e.RowIndex >= 0) OpenSelectedOutput(); };
        }

        private void LoadHistory()
        {
            entries = HistoryReader.Read(logFolder, 100);
            grid.Rows.Clear();
            foreach (HistoryEntry entry in entries)
            {
                int row = grid.Rows.Add(entry.Date.ToString("g"), entry.Drive, entry.Disc, entry.MediaType, entry.Result, entry.Output);
                grid.Rows[row].Tag = entry;
            }
            summary.Text = entries.Count == 0 ? "No job logs were found." : "Showing " + entries.Count + " most recent job" + (entries.Count == 1 ? "." : "s.");
        }

        private HistoryEntry SelectedEntry() { return grid.SelectedRows.Count == 0 ? null : grid.SelectedRows[0].Tag as HistoryEntry; }
        private void OpenSelectedLog() { HistoryEntry entry = SelectedEntry(); if (entry != null && File.Exists(entry.LogPath)) Process.Start(entry.LogPath); }
        private void OpenSelectedOutput()
        {
            HistoryEntry entry = SelectedEntry(); if (entry == null || string.IsNullOrWhiteSpace(entry.Output)) return;
            string path = entry.Output.Trim();
            if (Directory.Exists(path)) Process.Start("explorer.exe", "\"" + path + "\"");
            else if (File.Exists(path)) Process.Start("explorer.exe", "/select,\"" + path + "\"");
            else MessageBox.Show(this, "The recorded output no longer exists:\r\n\r\n" + path, "Media Nexus ARM", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
    }

    internal static class HistoryReader
    {
        public static List<HistoryEntry> Read(string folder, int maximum)
        {
            if (!Directory.Exists(folder)) return new List<HistoryEntry>();
            return Directory.GetFiles(folder, "*.log").Select(Parse).Where(entry => entry != null).OrderByDescending(entry => entry.Date).Take(maximum).ToList();
        }

        private static HistoryEntry Parse(string path)
        {
            try
            {
                string[] lines = File.ReadAllLines(path);
                string file = Path.GetFileNameWithoutExtension(path);
                string drive = MatchValue(file, @"(?:^|_)([A-Z])(?:_|$)");
                string disc = LastValue(lines, "Disc:");
                string media = LastValue(lines, "Media type:");
                if (string.IsNullOrWhiteSpace(media)) media = LastValue(lines, "Detected type:");
                if (string.IsNullOrWhiteSpace(media)) media = InferMedia(file);
                string output = LastValue(lines, "Completed output:");
                if (string.IsNullOrWhiteSpace(output)) output = LastValue(lines, "Completed:");
                string result = LastValue(lines, "Job result:");
                if (string.IsNullOrWhiteSpace(result)) result = !string.IsNullOrWhiteSpace(output) ? "Completed" : (DateTime.Now - File.GetLastWriteTime(path)).TotalMinutes < 2 ? "In Progress" : "Incomplete";
                if (string.IsNullOrWhiteSpace(disc)) disc = media == "Music" ? ReleaseName(LastValue(lines, "Release:")) : "Unknown disc";
                return new HistoryEntry { Date = File.GetCreationTime(path), Drive = string.IsNullOrWhiteSpace(drive) ? "?" : drive + ":", Disc = disc, MediaType = media, Result = result, Output = output, LogPath = path };
            }
            catch { return null; }
        }

        private static string LastValue(IEnumerable<string> lines, string marker)
        {
            foreach (string line in lines.Reverse())
            {
                int index = line.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
                if (index >= 0) return line.Substring(index + marker.Length).Trim();
            }
            return "";
        }
        private static string MatchValue(string value, string pattern) { Match match = Regex.Match(value, pattern, RegexOptions.IgnoreCase); return match.Success ? match.Groups[1].Value.ToUpperInvariant() : ""; }
        private static string InferMedia(string file)
        {
            if (file.StartsWith("makemkv_", StringComparison.OrdinalIgnoreCase)) return "Video";
            if (file.EndsWith("_Music", StringComparison.OrdinalIgnoreCase)) return "Music";
            if (file.EndsWith("_Audiobook", StringComparison.OrdinalIgnoreCase) || file.EndsWith("_Book", StringComparison.OrdinalIgnoreCase)) return "Audiobook";
            return "Unknown";
        }
        private static string ReleaseName(string value)
        {
            if (string.IsNullOrWhiteSpace(value)) return "Unknown disc";
            int separator = value.IndexOf(" ("); return separator > 0 ? value.Substring(0, separator) : value;
        }
    }
}
