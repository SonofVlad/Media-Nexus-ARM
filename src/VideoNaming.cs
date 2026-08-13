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
    }

    internal sealed class VideoNamingForm : Form
    {
        private readonly MediaKind kind;
        private readonly TextBox title = new TextBox();
        private readonly NumericUpDown year = new NumericUpDown { Minimum = 0, Maximum = 2100 };
        public VideoNamingResult Result { get; private set; }

        public VideoNamingForm(MediaKind kind, string driveLetter, string discLabel, int fileCount)
        {
            string drive = driveLetter.TrimEnd(':') + ":";
            this.kind = kind; Text = "Media Nexus ARM - Drive " + drive + " - Name Movie"; StartPosition = FormStartPosition.CenterParent; Font = new Font("Segoe UI", 9F);
            Size = new Size(590, 270); FormBorderStyle = FormBorderStyle.SizableToolWindow;
            var grid = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(14), ColumnCount = 2, RowCount = 5 };
            grid.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 150)); grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            title.Text = FriendlyDiscLabel(discLabel); Add(grid, 0, "Drive", new Label { Text = drive, TextAlign = ContentAlignment.MiddleLeft }); Add(grid, 1, "Title / Show", title); Add(grid, 2, "Year (optional)", year);
            grid.Controls.Add(new Label { Text = "The movie folder and MKV will use this name.", AutoSize = true, ForeColor = Color.DimGray }, 1, 3); int buttonRow = 4;
            var buttons = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.RightToLeft, AutoSize = true, Padding = new Padding(0, 8, 0, 0) };
            var apply = new Button { Text = "Apply Naming", AutoSize = true }; var skip = new Button { Text = "Keep Original Names", DialogResult = DialogResult.Ignore, AutoSize = true }; var cancel = new Button { Text = "Cancel", DialogResult = DialogResult.Cancel, AutoSize = true };
            apply.Click += ApplyClicked; buttons.Controls.Add(apply); buttons.Controls.Add(skip); buttons.Controls.Add(cancel); grid.Controls.Add(buttons, 0, buttonRow); grid.SetColumnSpan(buttons, 2); Controls.Add(grid); AcceptButton = apply; CancelButton = cancel; ThemeSettings.Apply(this);
        }

        private static void Add(TableLayoutPanel grid, int row, string label, Control input) { grid.Controls.Add(new Label { Text = label, Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft }, 0, row); input.Dock = DockStyle.Fill; grid.Controls.Add(input, 1, row); }
        private void ApplyClicked(object sender, EventArgs e)
        {
            if (string.IsNullOrWhiteSpace(title.Text)) { MessageBox.Show(this, "Enter a title or choose Keep Original Names.", "Media Nexus ARM", MessageBoxButtons.OK, MessageBoxIcon.Information); return; }
            Result = new VideoNamingResult { Title = title.Text.Trim(), Year = (int)year.Value };
            DialogResult = DialogResult.OK; Close();
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
        public static string OrganizeMovieFromDiscName(string source, string outputRoot, string discName, Action<string> log)
        {
            string baseName = MusicOrganizer.SafeName(discName); string folder = UniqueFolder(Path.Combine(outputRoot, "Movies", baseName)); Directory.CreateDirectory(folder);
            string target = Path.Combine(folder, baseName + ".mkv"); SafeMove(source, target); if (log != null) log(source + " -> " + target); return folder;
        }
        public static string OrganizeTvOriginalNames(IList<string> sources, string outputRoot, string discName, Action<string> log)
        {
            string folder = UniqueFolder(Path.Combine(outputRoot, "TV Shows", MusicOrganizer.SafeName(discName))); Directory.CreateDirectory(folder);
            foreach (string source in sources)
            {
                string target = UniqueFile(Path.Combine(folder, Path.GetFileName(source))); SafeMove(source, target); if (log != null) log(source + " -> " + target);
            }
            return folder;
        }
        private static string DisplayBase(VideoNamingResult naming) { return MusicOrganizer.SafeName(naming.Title + (naming.Year > 0 ? " (" + naming.Year + ")" : "")); }
        private static void SafeMove(string source, string target) { if (!File.Exists(source)) throw new FileNotFoundException("Ripped MKV was not found.", source); try { File.Move(source, target); } catch (IOException) { string partial = target + ".partial"; File.Copy(source, partial, false); if (new FileInfo(partial).Length != new FileInfo(source).Length) throw new IOException("Destination verification failed."); File.Move(partial, target); File.Delete(source); } if (!File.Exists(target) || new FileInfo(target).Length == 0) throw new IOException("Final MKV verification failed."); }
        private static string UniqueFolder(string path) { if (!Directory.Exists(path)) return path; for (int i = 2; i < 1000; i++) { string candidate = path + " (" + i + ")"; if (!Directory.Exists(candidate)) return candidate; } return path + " " + Guid.NewGuid().ToString("N"); }
        private static string UniqueFile(string path) { if (!File.Exists(path)) return path; string dir = Path.GetDirectoryName(path), stem = Path.GetFileNameWithoutExtension(path); for (int i = 2; i < 1000; i++) { string candidate = Path.Combine(dir, stem + " (" + i + ").mkv"); if (!File.Exists(candidate)) return candidate; } return Path.Combine(dir, stem + " " + Guid.NewGuid().ToString("N") + ".mkv"); }
    }
}
