using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Windows.Forms;

namespace DiscRipper
{
    internal sealed class VideoNamingResult
    {
        public string Title;
        public int Year;
        public string ImdbId;
        public int Season = 1;
        public int FirstEpisode = 1;
        public readonly List<string> EpisodeNames = new List<string>();
    }

    internal sealed class VideoNamingForm : Form
    {
        private readonly MediaKind kind;
        private readonly TextBox title = new TextBox();
        private readonly NumericUpDown year = new NumericUpDown { Minimum = 0, Maximum = 2100 };
        private readonly TextBox imdb = new TextBox();
        private readonly NumericUpDown season = new NumericUpDown { Minimum = 0, Maximum = 999, Value = 1 };
        private readonly NumericUpDown episode = new NumericUpDown { Minimum = 0, Maximum = 9999, Value = 1 };
        private readonly TextBox episodeNames = new TextBox { Multiline = true, ScrollBars = ScrollBars.Vertical };
        private readonly int fileCount;
        public VideoNamingResult Result { get; private set; }

        public VideoNamingForm(MediaKind kind, string discLabel, int fileCount)
        {
            this.kind = kind; this.fileCount = fileCount; Text = "Media Nexus ARM - Name " + (kind == MediaKind.Movie ? "Movie" : "TV Episodes"); StartPosition = FormStartPosition.CenterParent; Font = new Font("Segoe UI", 9F);
            Size = kind == MediaKind.Movie ? new Size(590, 300) : new Size(650, 500); FormBorderStyle = FormBorderStyle.SizableToolWindow;
            var grid = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(14), ColumnCount = 2, RowCount = kind == MediaKind.Movie ? 5 : 8 };
            grid.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 150)); grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            title.Text = FriendlyDiscLabel(discLabel); Add(grid, 0, "Title / Show", title); Add(grid, 1, "Year (optional)", year); Add(grid, 2, "IMDb ID (optional)", imdb);
            int buttonRow;
            if (kind == MediaKind.TVSeries)
            {
                Add(grid, 3, "Season", season); Add(grid, 4, "First episode", episode);
                grid.Controls.Add(new Label { Text = "Episode names\r\n(one per line, optional)", Dock = DockStyle.Fill, TextAlign = ContentAlignment.TopLeft }, 0, 5);
                episodeNames.Dock = DockStyle.Fill; grid.Controls.Add(episodeNames, 1, 5); grid.RowStyles.Add(new RowStyle(SizeType.AutoSize)); grid.RowStyles.Add(new RowStyle(SizeType.AutoSize)); grid.RowStyles.Add(new RowStyle(SizeType.AutoSize)); grid.RowStyles.Add(new RowStyle(SizeType.AutoSize)); grid.RowStyles.Add(new RowStyle(SizeType.AutoSize)); grid.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
                var lookup = new Button { Text = "Find Episode Names with TVMaze", AutoSize = true }; lookup.Click += LookupEpisodes;
                var lookupRow = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true }; lookupRow.Controls.Add(lookup); lookupRow.Controls.Add(new Label { Text = fileCount + " selected file(s) will be numbered sequentially.", AutoSize = true, ForeColor = Color.DimGray, Padding = new Padding(8, 7, 0, 0) });
                grid.Controls.Add(lookupRow, 1, 6); buttonRow = 7;
            }
            else { grid.Controls.Add(new Label { Text = "IMDb IDs use the form tt0453467. Leave fields blank to keep the original rip folder unchanged.", AutoSize = true, ForeColor = Color.DimGray }, 1, 3); buttonRow = 4; }
            var buttons = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.RightToLeft, AutoSize = true, Padding = new Padding(0, 8, 0, 0) };
            var apply = new Button { Text = "Apply Naming", AutoSize = true }; var skip = new Button { Text = "Keep Original Names", DialogResult = DialogResult.Ignore, AutoSize = true }; var cancel = new Button { Text = "Cancel", DialogResult = DialogResult.Cancel, AutoSize = true };
            apply.Click += ApplyClicked; buttons.Controls.Add(apply); buttons.Controls.Add(skip); buttons.Controls.Add(cancel); grid.Controls.Add(buttons, 0, buttonRow); grid.SetColumnSpan(buttons, 2); Controls.Add(grid); AcceptButton = apply; CancelButton = cancel;
        }

        private static void Add(TableLayoutPanel grid, int row, string label, Control input) { grid.Controls.Add(new Label { Text = label, Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft }, 0, row); input.Dock = DockStyle.Fill; grid.Controls.Add(input, 1, row); }
        private void ApplyClicked(object sender, EventArgs e)
        {
            if (string.IsNullOrWhiteSpace(title.Text)) { MessageBox.Show(this, "Enter a title or choose Keep Original Names.", "Media Nexus ARM", MessageBoxButtons.OK, MessageBoxIcon.Information); return; }
            string imdbId = imdb.Text.Trim(); if (imdbId.Length > 0 && !System.Text.RegularExpressions.Regex.IsMatch(imdbId, @"^tt\d{7,9}$", System.Text.RegularExpressions.RegexOptions.IgnoreCase)) { MessageBox.Show(this, "IMDb ID should look like tt0453467.", "Media Nexus ARM", MessageBoxButtons.OK, MessageBoxIcon.Information); return; }
            Result = new VideoNamingResult { Title = title.Text.Trim(), Year = (int)year.Value, ImdbId = imdbId.ToLowerInvariant(), Season = (int)season.Value, FirstEpisode = (int)episode.Value };
            Result.EpisodeNames.AddRange(episodeNames.Lines.Select(x => x.Trim()).Where(x => x.Length > 0)); DialogResult = DialogResult.OK; Close();
        }
        private async void LookupEpisodes(object sender, EventArgs e)
        {
            if (string.IsNullOrWhiteSpace(title.Text)) { MessageBox.Show(this, "Enter a show title first.", "Media Nexus ARM", MessageBoxButtons.OK, MessageBoxIcon.Information); return; }
            Button button = sender as Button; if (button != null) button.Enabled = false;
            try
            {
                TvMazeLookup found = await TvMazeClient.LookupAsync(title.Text.Trim(), (int)season.Value, (int)episode.Value, fileCount, System.Threading.CancellationToken.None);
                if (found == null || found.EpisodeNames.Count == 0) { MessageBox.Show(this, "TVMaze did not return matching episodes. Check the show, season, and starting episode.", "Media Nexus ARM", MessageBoxButtons.OK, MessageBoxIcon.Information); return; }
                title.Text = found.ShowName; if (year.Value == 0 && found.PremieredYear > 0) year.Value = found.PremieredYear; if (string.IsNullOrWhiteSpace(imdb.Text)) imdb.Text = found.ImdbId; episodeNames.Lines = found.EpisodeNames.ToArray();
            }
            catch (Exception ex) { MessageBox.Show(this, "TVMaze lookup failed: " + ex.Message, "Media Nexus ARM", MessageBoxButtons.OK, MessageBoxIcon.Warning); }
            finally { if (button != null) button.Enabled = true; }
        }
        private static string FriendlyDiscLabel(string value) { if (string.IsNullOrWhiteSpace(value) || value == "LOGICAL_VOLUME_ID" || value.StartsWith("DISC_")) return ""; return value.Replace('_', ' ').Trim(); }
    }

    internal static class VideoOrganizer
    {
        public static string OrganizeMovie(string source, string outputRoot, VideoNamingResult naming, Action<string> log)
        {
            string baseName = DisplayBase(naming); string folder = UniqueFolder(Path.Combine(outputRoot, "Movies", baseName)); Directory.CreateDirectory(folder);
            string target = Path.Combine(folder, baseName + ".mkv"); SafeMove(source, target); if (log != null) log(source + " -> " + target); return folder;
        }
        public static string OrganizeTv(IList<string> sources, string outputRoot, VideoNamingResult naming, Action<string> log)
        {
            string show = DisplayBase(naming); string folder = Path.Combine(outputRoot, "TV Shows", show, "Season " + naming.Season.ToString("00")); Directory.CreateDirectory(folder);
            for (int i = 0; i < sources.Count; i++)
            {
                int number = naming.FirstEpisode + i; string episodeName = i < naming.EpisodeNames.Count ? " - " + MusicOrganizer.SafeName(naming.EpisodeNames[i]) : "";
                string target = UniqueFile(Path.Combine(folder, show + " - S" + naming.Season.ToString("00") + "E" + number.ToString("00") + episodeName + ".mkv")); SafeMove(sources[i], target); if (log != null) log(sources[i] + " -> " + target);
            }
            return folder;
        }
        private static string DisplayBase(VideoNamingResult naming) { return MusicOrganizer.SafeName(naming.Title + (naming.Year > 0 ? " (" + naming.Year + ")" : "") + (!string.IsNullOrWhiteSpace(naming.ImdbId) ? " {imdb-" + naming.ImdbId + "}" : "")); }
        private static void SafeMove(string source, string target) { if (!File.Exists(source)) throw new FileNotFoundException("Ripped MKV was not found.", source); try { File.Move(source, target); } catch (IOException) { string partial = target + ".partial"; File.Copy(source, partial, false); if (new FileInfo(partial).Length != new FileInfo(source).Length) throw new IOException("Destination verification failed."); File.Move(partial, target); File.Delete(source); } if (!File.Exists(target) || new FileInfo(target).Length == 0) throw new IOException("Final MKV verification failed."); }
        private static string UniqueFolder(string path) { if (!Directory.Exists(path)) return path; for (int i = 2; i < 1000; i++) { string candidate = path + " (" + i + ")"; if (!Directory.Exists(candidate)) return candidate; } return path + " " + Guid.NewGuid().ToString("N"); }
        private static string UniqueFile(string path) { if (!File.Exists(path)) return path; string dir = Path.GetDirectoryName(path), stem = Path.GetFileNameWithoutExtension(path); for (int i = 2; i < 1000; i++) { string candidate = Path.Combine(dir, stem + " (" + i + ").mkv"); if (!File.Exists(candidate)) return candidate; } return Path.Combine(dir, stem + " " + Guid.NewGuid().ToString("N") + ".mkv"); }
    }
}
