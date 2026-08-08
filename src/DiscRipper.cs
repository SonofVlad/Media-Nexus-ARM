using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Media;
using Microsoft.Win32;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace DiscRipper
{
    internal enum MediaKind { Choose, Movie, TVSeries, Book, Music }

    internal sealed class OpticalDrive
    {
        public string Letter;
        public string Name;
        public string DeviceId;
        public override string ToString() { return Letter + ":  " + Name; }
    }

    internal sealed class DriveRow
    {
        public string Letter;
        public string Device;
        public Label DiscLabel;
        public ComboBox TypeBox;
        public Label StatusLabel;
        public ProgressBar ProgressBar;
        public Button EjectButton;
        public bool Present;
        public bool Busy;
        public DateTime FirstSeen;
        public CancellationTokenSource Cancellation;
    }

    internal sealed class MainForm : Form
    {
        private const int GridRowHeight = 44;
        private const int MinLengthSeconds = 600;
        private readonly Dictionary<string, DriveRow> rows = new Dictionary<string, DriveRow>();
        private readonly System.Windows.Forms.Timer pollTimer = new System.Windows.Forms.Timer();
        private readonly SemaphoreSlim audioGate = new SemaphoreSlim(1, 1);
        private readonly ConcurrentDictionary<string, int> discIndexes = new ConcurrentDictionary<string, int>();
        private readonly Label footer = new Label();
        private readonly TableLayoutPanel driveGrid;
        private readonly Panel driveGridFrame;
        private LayoutSettings layoutSettings;
        private string outputRoot;
        private readonly string makeMkv;
        private bool closing;

        public MainForm(List<OpticalDrive> selectedDrives, string selectedOutputRoot)
        {
            outputRoot = selectedOutputRoot;
            makeMkv = AppSettings.FindMakeMkv();
            Text = "Media Nexus ARM";
            Font = new Font("Segoe UI", 9F);
            StartPosition = FormStartPosition.CenterScreen;
            MinimumSize = new Size(900, 470);
            layoutSettings = LayoutSettings.Load();
            Size = new Size(layoutSettings.WindowWidth, layoutSettings.WindowHeight);
            FormClosing += OnClosing;

            var root = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(12), RowCount = 3, ColumnCount = 1 };
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            Controls.Add(root);

            var toolbar = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, WrapContents = false, Padding = new Padding(0, 0, 0, 8) };
            toolbar.Controls.Add(new Label { Text = "Change all:", AutoSize = true, Padding = new Padding(0, 8, 6, 0) });
            AddAllButton(toolbar, "Movie", MediaKind.Movie);
            AddAllButton(toolbar, "TV Series", MediaKind.TVSeries);
            AddAllButton(toolbar, "Book", MediaKind.Book);
            AddAllButton(toolbar, "Music", MediaKind.Music);
            AddAllButton(toolbar, "Clear", MediaKind.Choose);
            var configureButton = new Button { Text = "Config Drives", AutoSize = true, Margin = new Padding(20, 3, 3, 3) };
            configureButton.Click += ConfigureDrives;
            toolbar.Controls.Add(configureButton);
            var layoutButton = new Button { Text = "Edit Layout", AutoSize = true };
            layoutButton.Click += ConfigureLayout;
            toolbar.Controls.Add(layoutButton);
            var outputButton = new Button { Text = "Config Folder", AutoSize = true };
            outputButton.Click += ConfigureOutputFolder;
            toolbar.Controls.Add(outputButton);
            var openButton = new Button { Text = "Open Output", AutoSize = true };
            openButton.Click += (s, e) => OpenFolder(outputRoot);
            toolbar.Controls.Add(openButton);
            root.Controls.Add(toolbar, 0, 0);

            var gridHost = new Panel { Dock = DockStyle.Fill, AutoScroll = true };
            driveGrid = new TableLayoutPanel { Location = new Point(0, 0), Anchor = AnchorStyles.Top | AnchorStyles.Left, AutoSize = false, BackColor = Color.FromArgb(218, 218, 218), ColumnCount = 6, RowCount = 1, CellBorderStyle = TableLayoutPanelCellBorderStyle.Single, GrowStyle = TableLayoutPanelGrowStyle.FixedSize };
            driveGridFrame = new Panel { Location = new Point(0, 0), Anchor = AnchorStyles.Top | AnchorStyles.Left, BackColor = SystemColors.ControlDark, Padding = new Padding(1) };
            for (int i = 0; i < 6; i++) driveGrid.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, layoutSettings.ColumnWidths[i]));
            driveGrid.Location = new Point(1, 1);
            driveGridFrame.Controls.Add(driveGrid);
            gridHost.Controls.Add(driveGridFrame);
            root.Controls.Add(gridHost, 0, 1);
            RebuildDriveGrid(selectedDrives);

            footer.AutoSize = true;
            footer.Padding = new Padding(0, 8, 0, 0);
            footer.Text = "Select a media type before inserting discs, or select it when a disc is detected.";
            root.Controls.Add(footer, 0, 2);

            pollTimer.Interval = 2000;
            pollTimer.Tick += PollTimerOnTick;
            Shown += async (s, e) => { await RefreshMakeMkvMap(); pollTimer.Start(); PollAll(); };
        }

        private void ConfigureLayout(object sender, EventArgs e)
        {
            using (var dialog = new LayoutSettingsForm(layoutSettings, Size))
            {
                if (dialog.ShowDialog(this) != DialogResult.OK) return;
                layoutSettings = dialog.Result;
                layoutSettings.Save(); ApplyLayoutSettings();
            }
        }

        private void ConfigureOutputFolder(object sender, EventArgs e)
        {
            if (rows.Values.Any(r => r.Busy))
            {
                MessageBox.Show(this, "Wait for active rips to finish before changing the output folder.", "Media Nexus ARM", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            using (var dialog = new OutputFolderForm(outputRoot))
            {
                if (dialog.ShowDialog(this) != DialogResult.OK) return;
                outputRoot = dialog.SelectedPath; AppSettings.SaveOutputRoot(outputRoot);
                footer.Text = "Output: " + outputRoot;
            }
        }

        private void ApplyLayoutSettings()
        {
            Size = new Size(layoutSettings.WindowWidth, layoutSettings.WindowHeight);
            for (int i = 0; i < 6; i++)
            {
                driveGrid.ColumnStyles[i].SizeType = SizeType.Absolute;
                driveGrid.ColumnStyles[i].Width = layoutSettings.ColumnWidths[i];
            }
            UpdateGridBounds();
            driveGrid.PerformLayout();
        }

        private void UpdateGridBounds()
        {
            int totalWidth = layoutSettings.ColumnWidths.Sum() + 2;
            int totalHeight = (driveGrid.RowCount * GridRowHeight) + 2;
            driveGrid.Size = new Size(totalWidth, totalHeight);
            driveGridFrame.Size = new Size(totalWidth + 2, totalHeight + 2);
        }

        private void RebuildDriveGrid(List<OpticalDrive> selectedDrives)
        {
            driveGrid.SuspendLayout();
            driveGrid.Controls.Clear(); driveGrid.RowStyles.Clear(); driveGrid.RowCount = 1; rows.Clear();
            driveGrid.RowStyles.Add(new RowStyle(SizeType.Absolute, GridRowHeight));
            AddHeader(driveGrid, 0, "Drive"); AddHeader(driveGrid, 1, "Device"); AddHeader(driveGrid, 2, "Disc");
            AddHeader(driveGrid, 3, "Media type"); AddHeader(driveGrid, 4, "Status"); AddHeader(driveGrid, 5, "Action");
            foreach (var drive in selectedDrives.OrderBy(d => d.Letter)) AddDriveRow(driveGrid, drive.Letter, drive.Name);
            UpdateGridBounds();
            driveGrid.ResumeLayout();
            footer.Text = (selectedDrives.Count == 0 ? "No selected drives are currently connected. Use Configure drives." :
                "Select a media type before inserting discs, or select it when a disc is detected.") + "   Output: " + outputRoot;
        }

        private void ConfigureDrives(object sender, EventArgs e)
        {
            if (rows.Values.Any(r => r.Busy))
            {
                MessageBox.Show(this, "Wait for active rips to finish before changing the drive selection.", "Media Nexus ARM", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            var detected = DriveSettings.DiscoverOpticalDrives();
            var selectedIds = DriveSettings.LoadSelectedIds();
            using (var dialog = new DriveSelectionForm(detected, selectedIds))
            {
                if (dialog.ShowDialog(this) != DialogResult.OK) return;
                DriveSettings.SaveSelectedIds(dialog.SelectedDeviceIds);
                RebuildDriveGrid(detected.Where(d => dialog.SelectedDeviceIds.Contains(d.DeviceId, StringComparer.OrdinalIgnoreCase)).ToList());
                PollAll();
            }
        }

        private void AddAllButton(Control parent, string text, MediaKind kind)
        {
            var button = new Button { Text = text, AutoSize = true };
            button.Click += (s, e) =>
            {
                foreach (var row in rows.Values.Where(r => !r.Busy)) row.TypeBox.SelectedItem = DisplayName(kind);
                PollAll();
            };
            parent.Controls.Add(button);
        }

        private static void AddHeader(TableLayoutPanel grid, int column, string text)
        {
            grid.Controls.Add(new Label { Text = text, Dock = DockStyle.Fill, Font = new Font("Segoe UI", 9F, FontStyle.Bold), Padding = new Padding(5), AutoSize = true, TextAlign = ContentAlignment.MiddleCenter }, column, 0);
        }

        private void AddDriveRow(TableLayoutPanel grid, string letter, string device)
        {
            int rowIndex = grid.RowCount++;
            grid.RowStyles.Add(new RowStyle(SizeType.Absolute, GridRowHeight));
            var driveLabel = new Label { Text = letter + ":", Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleCenter, Font = new Font("Segoe UI", 10F, FontStyle.Bold) };
            var deviceLabel = new Label { Text = device, Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft, AutoEllipsis = true, Padding = new Padding(5, 0, 0, 0) };
            var discLabel = new Label { Text = "Empty", Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft, AutoEllipsis = true, Padding = new Padding(5, 0, 0, 0) };
            var type = new ComboBox { Dock = DockStyle.Fill, DropDownStyle = ComboBoxStyle.DropDownList, Margin = new Padding(5, 9, 5, 5) };
            type.Items.AddRange(new object[] { "Choose...", "Movie", "TV Series", "Book", "Music" });
            type.SelectedIndex = 0;
            var status = new Label { Text = "Waiting for disc", Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft, AutoEllipsis = true, Padding = new Padding(5, 0, 0, 0) };
            var progress = new ProgressBar { Dock = DockStyle.Fill, Minimum = 0, Maximum = 100, Value = 0, Style = ProgressBarStyle.Continuous, Margin = new Padding(5, 0, 5, 4) };
            var statusPanel = new TableLayoutPanel { Dock = DockStyle.Fill, BackColor = Color.Transparent, ColumnCount = 1, RowCount = 2, Margin = Padding.Empty, Padding = Padding.Empty };
            statusPanel.RowStyles.Add(new RowStyle(SizeType.Percent, 62)); statusPanel.RowStyles.Add(new RowStyle(SizeType.Percent, 38));
            statusPanel.Controls.Add(status, 0, 0); statusPanel.Controls.Add(progress, 0, 1);
            var eject = new Button { Text = "Eject", Dock = DockStyle.Fill, Margin = new Padding(5, 8, 5, 7) };
            var item = new DriveRow { Letter = letter, Device = device, DiscLabel = discLabel, TypeBox = type, StatusLabel = status, ProgressBar = progress, EjectButton = eject };
            type.SelectedIndexChanged += (s, e) => { if (!item.Busy) PollDrive(item); };
            eject.Click += (s, e) => Eject(item.Letter);
            rows[letter] = item;
            grid.Controls.Add(driveLabel, 0, rowIndex); grid.Controls.Add(deviceLabel, 1, rowIndex); grid.Controls.Add(discLabel, 2, rowIndex);
            grid.Controls.Add(type, 3, rowIndex); grid.Controls.Add(statusPanel, 4, rowIndex); grid.Controls.Add(eject, 5, rowIndex);
        }

        private async void PollTimerOnTick(object sender, EventArgs e)
        {
            pollTimer.Stop();
            try { await RefreshMakeMkvMap(); PollAll(); }
            finally { if (!closing) pollTimer.Start(); }
        }

        private void PollAll()
        {
            foreach (var row in rows.Values) PollDrive(row);
        }

        private void PollDrive(DriveRow row)
        {
            bool present = IsMediaPresent(row.Letter);
            if (!present)
            {
                row.Present = false;
                row.FirstSeen = DateTime.MinValue;
                row.DiscLabel.Text = "Empty";
                if (!row.Busy) { SetStatus(row, "Waiting for disc", Color.DimGray); SetProgress(row, 0); }
                return;
            }

            bool newlySeen = !row.Present;
            string label = GetVolumeLabel(row.Letter);
            row.DiscLabel.Text = string.IsNullOrWhiteSpace(label) ? "Audio/unknown disc" : label;
            if (newlySeen) { row.Present = true; row.FirstSeen = DateTime.Now; }
            if (row.Busy) return;

            MediaKind kind = SelectedKind(row);
            if (kind == MediaKind.Choose)
            {
                SetStatus(row, "Disc detected — choose a media type", Color.DarkOrange);
                SetProgress(row, 0);
                if (newlySeen) SystemSounds.Asterisk.Play();
                return;
            }

            row.Busy = true;
            SetProgress(row, 0);
            row.TypeBox.Enabled = false;
            row.Cancellation = new CancellationTokenSource();
            Task.Run(async () =>
            {
                bool ok = false;
                try
                {
                    ok = kind == MediaKind.Movie || kind == MediaKind.TVSeries
                        ? await RipVideo(row, kind, row.Cancellation.Token)
                        : await RipWithITunes(row, kind, row.Cancellation.Token);
                }
                catch (Exception ex) { Ui(() => SetStatus(row, "Failed: " + ex.Message, Color.DarkRed)); }
                finally
                {
                    Eject(row.Letter);
                    PlayCompletionSound(ok);
                    Ui(() =>
                    {
                        if (ok) SetProgress(row, 100);
                        SetStatus(row, ok ? "Complete — ejected (100%)" : "Failed — ejected", ok ? Color.DarkGreen : Color.DarkRed);
                        row.Busy = false;
                        row.TypeBox.Enabled = true;
                    });
                }
            });
        }

        private async Task<bool> RipVideo(DriveRow row, MediaKind kind, CancellationToken token)
        {
            int discIndex;
            if (!discIndexes.TryGetValue(row.Letter, out discIndex))
            {
                await RefreshMakeMkvMap();
                if (!discIndexes.TryGetValue(row.Letter, out discIndex)) throw new InvalidOperationException("MakeMKV could not map this drive");
            }

            string typeFolder = kind == MediaKind.Movie ? "Movies" : "TV Series";
            string discName = SafeName(GetVolumeLabel(row.Letter));
            if (string.IsNullOrWhiteSpace(discName) || discName == "UNKNOWN_DISC") discName = "DISC_" + row.Letter;
            string outDir = UniqueDiscFolder(Path.Combine(outputRoot, typeFolder), discName);
            Directory.CreateDirectory(outDir);
            string logDir = Path.Combine(outputRoot, "Logs"); Directory.CreateDirectory(logDir);
            string logPath = Path.Combine(logDir, "makemkv_" + row.Letter + "_" + DateTime.Now.ToString("yyyy-MM-dd_HH-mm-ss") + ".log");

            Ui(() => SetStatus(row, "Scanning with MakeMKV...", Color.DarkBlue));
            var titleIds = kind == MediaKind.TVSeries ? await SelectTvTitles(discIndex, token) : new List<int> { -1 };
            if (titleIds.Count == 0) titleIds.Add(-1);
            bool allOk = true;
            int completedTitles = 0;
            foreach (int title in titleIds)
            {
                token.ThrowIfCancellationRequested();
                string titleText = title < 0 ? "all titles" : "title " + title;
                Ui(() => { SetStatus(row, "Ripping " + titleText + "... 0%", Color.DarkBlue); SetProgress(row, completedTitles * 100 / titleIds.Count); });
                string target = title < 0 ? "all" : title.ToString();
                int filesBefore = Directory.GetFiles(outDir, "*.mkv").Length;
                int titlesDoneAtStart = completedTitles;
                var result = await RunProcess(makeMkv, "-r --noscan --minlength=" + MinLengthSeconds + " mkv disc:" + discIndex + " " + target + " \"" + outDir + "\"", token, percent =>
                {
                    int wholeDiscPercent = Math.Min(99, ((titlesDoneAtStart * 100) + percent) / titleIds.Count);
                    Ui(() => { SetStatus(row, "Ripping " + titleText + "... " + wholeDiscPercent + "%", Color.DarkBlue); SetProgress(row, wholeDiscPercent); });
                });
                File.AppendAllText(logPath, result.Output, Encoding.UTF8);
                bool copied = result.Output.IndexOf("Copy complete", StringComparison.OrdinalIgnoreCase) >= 0 ||
                              (result.ExitCode == 0 && Directory.GetFiles(outDir, "*.mkv").Length > filesBefore);
                if (!copied) { allOk = false; break; }
                completedTitles++;
            }
            if (allOk && File.Exists(logPath)) File.Delete(logPath);
            return allOk;
        }

        private async Task<List<int>> SelectTvTitles(int discIndex, CancellationToken token)
        {
            var result = await RunProcess(makeMkv, "-r info disc:" + discIndex, token);
            var durations = new Dictionary<int, int>();
            foreach (string line in result.Output.Split('\n'))
            {
                var id = Regex.Match(line, @"^TINFO:(\d+),");
                var time = Regex.Match(line, @"(\d+):([0-5]\d):([0-5]\d)");
                if (!id.Success || !time.Success) continue;
                int title = int.Parse(id.Groups[1].Value);
                int seconds = int.Parse(time.Groups[1].Value) * 3600 + int.Parse(time.Groups[2].Value) * 60 + int.Parse(time.Groups[3].Value);
                if (!durations.ContainsKey(title) || seconds > durations[title]) durations[title] = seconds;
            }
            var candidates = durations.Where(x => x.Value >= MinLengthSeconds).OrderBy(x => x.Value).ToList();
            if (candidates.Count < 3) return candidates.Select(x => x.Key).OrderBy(x => x).ToList();
            int[] values = candidates.Select(x => x.Value).OrderBy(x => x).ToArray();
            int median = values.Length % 2 == 1 ? values[values.Length / 2] : (values[values.Length / 2 - 1] + values[values.Length / 2]) / 2;
            var longest = candidates[candidates.Count - 1]; var second = candidates[candidates.Count - 2];
            bool playAll = longest.Value >= median * 1.8 && longest.Value >= second.Value * 1.4;
            return candidates.Where(x => !playAll || x.Key != longest.Key).Select(x => x.Key).OrderBy(x => x).ToList();
        }

        private async Task<bool> RipWithITunes(DriveRow row, MediaKind kind, CancellationToken token)
        {
            await audioGate.WaitAsync(token);
            try
            {
                DateTime started = DateTime.Now.AddSeconds(-5);
                Ui(() => SetStatus(row, "Opening iTunes — waiting for import and eject...", Color.Purple));
                Process.Start(new ProcessStartInfo { FileName = "iTunes.exe", UseShellExecute = true });
                DateTime deadline = DateTime.Now.AddHours(3);
                while (DateTime.Now < deadline)
                {
                    token.ThrowIfCancellationRequested();
                    await Task.Delay(2000, token);
                    if (!IsMediaPresent(row.Letter))
                    {
                        await CopyRecentITunesMp3s(row, kind, started);
                        return true;
                    }
                }
                return false;
            }
            finally { audioGate.Release(); }
        }

        private async Task CopyRecentITunesMp3s(DriveRow row, MediaKind kind, DateTime started)
        {
            await Task.Run(() =>
            {
                string music = Environment.GetFolderPath(Environment.SpecialFolder.MyMusic);
                string[] roots = { Path.Combine(music, "iTunes", "iTunes Media"), Path.Combine(music, "iTunes", "iTunes Music") };
                var recent = roots.Where(Directory.Exists).SelectMany(r => Directory.GetFiles(r, "*.mp3", SearchOption.AllDirectories))
                    .Select(p => new FileInfo(p)).Where(f => f.LastWriteTime >= started).ToList();
                if (recent.Count == 0) return;
                string typeFolder = kind == MediaKind.Music ? "Music" : "Audiobooks";
                string name = (kind == MediaKind.Music ? "MUSIC_" : "BOOK_") + row.Letter;
                string destination = UniqueDiscFolder(Path.Combine(outputRoot, typeFolder), name);
                Directory.CreateDirectory(destination);
                foreach (var file in recent)
                {
                    string target = Path.Combine(destination, file.Name); int n = 2;
                    while (File.Exists(target)) target = Path.Combine(destination, Path.GetFileNameWithoutExtension(file.Name) + "_" + n++ + file.Extension);
                    File.Copy(file.FullName, target, false);
                }
            });
        }

        private async Task RefreshMakeMkvMap()
        {
            if (string.IsNullOrWhiteSpace(makeMkv) || !File.Exists(makeMkv)) { Ui(() => footer.Text = "MakeMKV was not found. Install MakeMKV in its standard Program Files location."); return; }
            try
            {
                var result = await RunProcess(makeMkv, "-r --cache=1 info disc:9999", CancellationToken.None);
                foreach (string line in result.Output.Split('\n'))
                {
                    var match = Regex.Match(line, "^DRV:(\\d+),.*?,\\\"([A-Z]):\\\"\\s*$");
                    if (match.Success) discIndexes[match.Groups[2].Value] = int.Parse(match.Groups[1].Value);
                }
            }
            catch { }
        }

        private sealed class ProcessResult { public int ExitCode; public string Output; }
        private static async Task<ProcessResult> RunProcess(string file, string arguments, CancellationToken token, Action<int> progress = null)
        {
            var output = new StringBuilder();
            using (var process = new Process())
            {
                process.StartInfo = new ProcessStartInfo(file, arguments) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
                Action<string> handleLine = line =>
                {
                    if (line == null) return;
                    lock (output) output.AppendLine(line);
                    if (progress == null) return;
                    var match = Regex.Match(line, @"^PRGV:(\d+),(\d+),(\d+)");
                    if (!match.Success) return;
                    long current = long.Parse(match.Groups[1].Value), total = long.Parse(match.Groups[2].Value), maximum = long.Parse(match.Groups[3].Value);
                    if (maximum <= 0) return;
                    long value = total > 0 ? total : current;
                    progress((int)Math.Max(0, Math.Min(100, value * 100 / maximum)));
                };
                process.OutputDataReceived += (s, e) => handleLine(e.Data);
                process.ErrorDataReceived += (s, e) => handleLine(e.Data);
                process.Start(); process.BeginOutputReadLine(); process.BeginErrorReadLine();
                using (token.Register(() => { try { if (!process.HasExited) process.Kill(); } catch { } }))
                    await Task.Run(() => process.WaitForExit(), token);
                return new ProcessResult { ExitCode = process.ExitCode, Output = output.ToString() };
            }
        }

        private static MediaKind SelectedKind(DriveRow row)
        {
            switch (Convert.ToString(row.TypeBox.SelectedItem))
            {
                case "Movie": return MediaKind.Movie; case "TV Series": return MediaKind.TVSeries;
                case "Book": return MediaKind.Book; case "Music": return MediaKind.Music; default: return MediaKind.Choose;
            }
        }
        private static string DisplayName(MediaKind kind) { return kind == MediaKind.TVSeries ? "TV Series" : kind == MediaKind.Choose ? "Choose..." : kind.ToString(); }
        private void SetStatus(DriveRow row, string text, Color color) { row.StatusLabel.Text = text; row.StatusLabel.ForeColor = color; }
        private static void SetProgress(DriveRow row, int value)
        {
            row.ProgressBar.Style = ProgressBarStyle.Continuous;
            row.ProgressBar.Value = Math.Max(row.ProgressBar.Minimum, Math.Min(row.ProgressBar.Maximum, value));
        }
        private void Ui(Action action) { if (closing || IsDisposed) return; if (InvokeRequired) BeginInvoke(action); else action(); }
        private static void PlayCompletionSound(bool success) { if (success) SystemSounds.Asterisk.Play(); else SystemSounds.Hand.Play(); }
        private static string GetVolumeLabel(string letter) { try { return new DriveInfo(letter + @":\").VolumeLabel; } catch { return ""; } }
        private static string SafeName(string value)
        {
            if (string.IsNullOrWhiteSpace(value)) return "UNKNOWN_DISC";
            foreach (char c in Path.GetInvalidFileNameChars()) value = value.Replace(c, '_');
            value = value.Trim().TrimEnd('.'); return value.Length > 80 ? value.Substring(0, 80) : value;
        }
        private static string UniqueDiscFolder(string parent, string name)
        {
            string stamp = DateTime.Now.ToString("yyyy-MM-dd_HH-mm-ss"); return Path.Combine(parent, SafeName(name) + "_" + stamp);
        }
        private static void OpenFolder(string path) { try { Directory.CreateDirectory(path); Process.Start(new ProcessStartInfo { FileName = path, UseShellExecute = true }); } catch { } }

        private void OnClosing(object sender, FormClosingEventArgs e)
        {
            closing = true; pollTimer.Stop(); foreach (var row in rows.Values) if (row.Cancellation != null) row.Cancellation.Cancel();
        }

        private const uint GENERIC_READ = 0x80000000, FILE_SHARE_READ = 1, FILE_SHARE_WRITE = 2, OPEN_EXISTING = 3;
        private const uint IOCTL_STORAGE_CHECK_VERIFY2 = 0x002D0800, IOCTL_STORAGE_EJECT_MEDIA = 0x002D4808;
        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Auto)] private static extern IntPtr CreateFile(string name, uint access, uint share, IntPtr security, uint creation, uint flags, IntPtr template);
        [DllImport("kernel32.dll", SetLastError = true)] private static extern bool DeviceIoControl(IntPtr device, uint control, IntPtr input, uint inputSize, IntPtr output, uint outputSize, out uint returned, IntPtr overlapped);
        [DllImport("kernel32.dll", SetLastError = true)] private static extern bool CloseHandle(IntPtr handle);
        private static bool IsMediaPresent(string letter)
        {
            IntPtr h = CreateFile(@"\\.\" + letter + ":", 0, FILE_SHARE_READ | FILE_SHARE_WRITE, IntPtr.Zero, OPEN_EXISTING, 0, IntPtr.Zero);
            if (h == new IntPtr(-1)) return false;
            try { uint returned; return DeviceIoControl(h, IOCTL_STORAGE_CHECK_VERIFY2, IntPtr.Zero, 0, IntPtr.Zero, 0, out returned, IntPtr.Zero); }
            finally { CloseHandle(h); }
        }
        private static void Eject(string letter)
        {
            IntPtr h = CreateFile(@"\\.\" + letter + ":", GENERIC_READ, FILE_SHARE_READ | FILE_SHARE_WRITE, IntPtr.Zero, OPEN_EXISTING, 0, IntPtr.Zero);
            if (h == new IntPtr(-1)) return;
            try { uint returned; DeviceIoControl(h, IOCTL_STORAGE_EJECT_MEDIA, IntPtr.Zero, 0, IntPtr.Zero, 0, out returned, IntPtr.Zero); }
            finally { CloseHandle(h); }
        }
    }

    internal sealed class LayoutSettings
    {
        private const string RegistryPath = @"Software\DiscRipper";
        public int WindowWidth = 1060;
        public int WindowHeight = 520;
        public int[] ColumnWidths = { 60, 280, 185, 145, 300, 75 };

        public static LayoutSettings Load()
        {
            var settings = new LayoutSettings();
            try
            {
                using (var key = Registry.CurrentUser.OpenSubKey(RegistryPath))
                {
                    if (key == null) return settings;
                    settings.WindowWidth = ReadInt(key, "WindowWidth", settings.WindowWidth, 900, 7680);
                    settings.WindowHeight = ReadInt(key, "WindowHeight", settings.WindowHeight, 470, 4320);
                    for (int i = 0; i < 6; i++) settings.ColumnWidths[i] = ReadInt(key, "ColumnWidth" + i, settings.ColumnWidths[i], 45, 2000);
                }
            }
            catch { }
            return settings;
        }

        private static int ReadInt(RegistryKey key, string name, int fallback, int minimum, int maximum)
        {
            object value = key.GetValue(name); int parsed;
            return value != null && int.TryParse(value.ToString(), out parsed) ? Math.Max(minimum, Math.Min(maximum, parsed)) : fallback;
        }

        public void Save()
        {
            using (var key = Registry.CurrentUser.CreateSubKey(RegistryPath))
            {
                key.SetValue("WindowWidth", WindowWidth, RegistryValueKind.DWord);
                key.SetValue("WindowHeight", WindowHeight, RegistryValueKind.DWord);
                for (int i = 0; i < 6; i++) key.SetValue("ColumnWidth" + i, ColumnWidths[i], RegistryValueKind.DWord);
            }
        }

        public LayoutSettings Copy()
        {
            return new LayoutSettings { WindowWidth = WindowWidth, WindowHeight = WindowHeight, ColumnWidths = (int[])ColumnWidths.Clone() };
        }
    }

    internal sealed class LayoutSettingsForm : Form
    {
        private readonly NumericUpDown windowWidth = NewNumber(900, 7680);
        private readonly NumericUpDown windowHeight = NewNumber(470, 4320);
        private readonly NumericUpDown[] columns = new NumericUpDown[6];
        public LayoutSettings Result { get; private set; }

        public LayoutSettingsForm(LayoutSettings current, Size currentSize)
        {
            Result = current.Copy();
            Text = "Media Nexus ARM Layout"; StartPosition = FormStartPosition.CenterParent;
            Font = new Font("Segoe UI", 9F); FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false; MinimizeBox = false; ClientSize = new Size(410, 430);
            var grid = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(14), ColumnCount = 2, RowCount = 11 };
            grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 62)); grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 38));
            grid.Controls.Add(new Label { Text = "Window size (pixels)", Font = new Font("Segoe UI", 9F, FontStyle.Bold), AutoSize = true, Padding = new Padding(0, 2, 0, 8) }, 0, 0);
            grid.SetColumnSpan(grid.GetControlFromPosition(0, 0), 2);
            AddSetting(grid, 1, "Window width", windowWidth); AddSetting(grid, 2, "Window height", windowHeight);
            windowWidth.Value = Math.Max(windowWidth.Minimum, Math.Min(windowWidth.Maximum, currentSize.Width));
            windowHeight.Value = Math.Max(windowHeight.Minimum, Math.Min(windowHeight.Maximum, currentSize.Height));
            grid.Controls.Add(new Label { Text = "Column widths (pixels)", Font = new Font("Segoe UI", 9F, FontStyle.Bold), AutoSize = true, Padding = new Padding(0, 12, 0, 8) }, 0, 3);
            grid.SetColumnSpan(grid.GetControlFromPosition(0, 3), 2);
            string[] names = { "Drive", "Device", "Disc", "Media type", "Status", "Action" };
            for (int i = 0; i < 6; i++)
            {
                columns[i] = NewNumber(45, 2000); columns[i].Value = current.ColumnWidths[i]; AddSetting(grid, 4 + i, names[i], columns[i]);
            }
            var buttons = new FlowLayoutPanel { FlowDirection = FlowDirection.RightToLeft, Dock = DockStyle.Fill, AutoSize = true, Padding = new Padding(0, 10, 0, 0) };
            var save = new Button { Text = "Apply", AutoSize = true }; var cancel = new Button { Text = "Cancel", DialogResult = DialogResult.Cancel, AutoSize = true };
            var reset = new Button { Text = "Defaults", AutoSize = true, Margin = new Padding(3, 3, 18, 3) };
            save.Click += SaveClicked; reset.Click += ResetClicked; buttons.Controls.Add(save); buttons.Controls.Add(cancel); buttons.Controls.Add(reset);
            grid.Controls.Add(buttons, 0, 10); grid.SetColumnSpan(buttons, 2); Controls.Add(grid); AcceptButton = save; CancelButton = cancel;
        }

        private static NumericUpDown NewNumber(decimal minimum, decimal maximum) { return new NumericUpDown { Minimum = minimum, Maximum = maximum, Increment = 10, Dock = DockStyle.Fill, ThousandsSeparator = true }; }
        private static void AddSetting(TableLayoutPanel grid, int row, string label, Control input)
        {
            grid.Controls.Add(new Label { Text = label, Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft }, 0, row); grid.Controls.Add(input, 1, row);
        }
        private void SaveClicked(object sender, EventArgs e)
        {
            Result.WindowWidth = (int)windowWidth.Value; Result.WindowHeight = (int)windowHeight.Value;
            for (int i = 0; i < 6; i++) Result.ColumnWidths[i] = (int)columns[i].Value;
            DialogResult = DialogResult.OK; Close();
        }
        private void ResetClicked(object sender, EventArgs e)
        {
            var defaults = new LayoutSettings(); windowWidth.Value = defaults.WindowWidth; windowHeight.Value = defaults.WindowHeight;
            for (int i = 0; i < 6; i++) columns[i].Value = defaults.ColumnWidths[i];
        }
    }

    internal static class DriveSettings
    {
        private const string RegistryPath = @"Software\DiscRipper";
        private const string ValueName = "SelectedOpticalDrives";

        public static List<OpticalDrive> DiscoverOpticalDrives()
        {
            var result = new List<OpticalDrive>();
            try
            {
                using (var searcher = new System.Management.ManagementObjectSearcher("SELECT Drive, Name, PNPDeviceID FROM Win32_CDROMDrive"))
                    foreach (System.Management.ManagementObject item in searcher.Get())
                    {
                        string drive = Convert.ToString(item["Drive"]).TrimEnd(':').ToUpperInvariant();
                        string id = Convert.ToString(item["PNPDeviceID"]);
                        if (!string.IsNullOrWhiteSpace(drive) && !string.IsNullOrWhiteSpace(id))
                            result.Add(new OpticalDrive { Letter = drive, Name = Convert.ToString(item["Name"]), DeviceId = id });
                    }
            }
            catch { }
            return result.OrderBy(x => x.Letter).ToList();
        }

        public static string[] LoadSelectedIds()
        {
            try
            {
                using (var key = Registry.CurrentUser.OpenSubKey(RegistryPath))
                {
                    if (key == null) return new string[0];
                    var multi = key.GetValue(ValueName) as string[];
                    return multi ?? new string[0];
                }
            }
            catch { return new string[0]; }
        }

        public static void SaveSelectedIds(IEnumerable<string> ids)
        {
            using (var key = Registry.CurrentUser.CreateSubKey(RegistryPath))
                key.SetValue(ValueName, ids.Distinct(StringComparer.OrdinalIgnoreCase).ToArray(), RegistryValueKind.MultiString);
        }
    }

    internal static class AppSettings
    {
        private const string RegistryPath = @"Software\DiscRipper";
        private const string OutputValue = "OutputRoot";
        public static string LoadOutputRoot()
        {
            try
            {
                using (var key = Registry.CurrentUser.OpenSubKey(RegistryPath)) return key == null ? "" : Convert.ToString(key.GetValue(OutputValue));
            }
            catch { return ""; }
        }
        public static void SaveOutputRoot(string path)
        {
            using (var key = Registry.CurrentUser.CreateSubKey(RegistryPath)) key.SetValue(OutputValue, path, RegistryValueKind.String);
        }
        public static string FindMakeMkv()
        {
            string[] roots = { Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles) };
            string[] names = { "makemkvcon64.exe", "makemkvcon.exe" };
            foreach (string root in roots.Where(r => !string.IsNullOrWhiteSpace(r)).Distinct(StringComparer.OrdinalIgnoreCase))
                foreach (string name in names)
                {
                    string candidate = Path.Combine(root, "MakeMKV", name);
                    if (File.Exists(candidate)) return candidate;
                }
            return "";
        }
    }

    internal sealed class OutputFolderForm : Form
    {
        private readonly TextBox pathBox = new TextBox();
        public string SelectedPath { get; private set; }
        public OutputFolderForm(string currentPath)
        {
            Text = "Media Nexus ARM — Output Folder"; StartPosition = FormStartPosition.CenterParent;
            Font = new Font("Segoe UI", 9F); FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false; MinimizeBox = false; ClientSize = new Size(620, 175);
            var root = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(14), RowCount = 3, ColumnCount = 1 };
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize)); root.RowStyles.Add(new RowStyle(SizeType.AutoSize)); root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            root.Controls.Add(new Label { Text = "Choose the root folder for Movies, TV Series, Music, Audiobooks, and Logs. Local, external, mapped, and UNC network paths are supported.", AutoSize = true, MaximumSize = new Size(580, 0), Padding = new Padding(0, 0, 0, 10) });
            var pathRow = new TableLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, ColumnCount = 2 };
            pathRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100)); pathRow.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            pathBox.Text = currentPath ?? ""; pathBox.Dock = DockStyle.Fill;
            var browse = new Button { Text = "Browse...", AutoSize = true, Margin = new Padding(8, 0, 0, 0) };
            browse.Click += BrowseClicked; pathRow.Controls.Add(pathBox, 0, 0); pathRow.Controls.Add(browse, 1, 0); root.Controls.Add(pathRow, 0, 1);
            var buttons = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.RightToLeft, AutoSize = true, Padding = new Padding(0, 12, 0, 0) };
            var save = new Button { Text = "Save", AutoSize = true }; var cancel = new Button { Text = "Cancel", DialogResult = DialogResult.Cancel, AutoSize = true };
            save.Click += SaveClicked; buttons.Controls.Add(save); buttons.Controls.Add(cancel); root.Controls.Add(buttons, 0, 2);
            Controls.Add(root); AcceptButton = save; CancelButton = cancel;
        }
        private void BrowseClicked(object sender, EventArgs e)
        {
            using (var picker = new FolderBrowserDialog { Description = "Choose the Media Nexus ARM output folder", ShowNewFolderButton = true, SelectedPath = Directory.Exists(pathBox.Text) ? pathBox.Text : "" })
                if (picker.ShowDialog(this) == DialogResult.OK) pathBox.Text = picker.SelectedPath;
        }
        private void SaveClicked(object sender, EventArgs e)
        {
            string path = Environment.ExpandEnvironmentVariables(pathBox.Text.Trim().TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
            if (string.IsNullOrWhiteSpace(path))
            {
                MessageBox.Show(this, "Choose an output folder.", "Media Nexus ARM", MessageBoxButtons.OK, MessageBoxIcon.Information); return;
            }
            try { Directory.CreateDirectory(path); }
            catch (Exception ex)
            {
                MessageBox.Show(this, "The output folder could not be opened or created:\r\n\r\n" + ex.Message, "Media Nexus ARM", MessageBoxButtons.OK, MessageBoxIcon.Error); return;
            }
            SelectedPath = path; DialogResult = DialogResult.OK; Close();
        }
    }

    internal sealed class DriveSelectionForm : Form
    {
        private readonly CheckedListBox list = new CheckedListBox();
        private readonly List<OpticalDrive> drives;
        public string[] SelectedDeviceIds { get; private set; }

        public DriveSelectionForm(List<OpticalDrive> available, IEnumerable<string> selectedIds)
        {
            drives = available;
            Text = "Media Nexus ARM — Choose Optical Drives"; StartPosition = FormStartPosition.CenterParent;
            Font = new Font("Segoe UI", 9F); Size = new Size(610, 420); MinimumSize = new Size(500, 330);
            var root = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(12), RowCount = 3, ColumnCount = 1 };
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize)); root.RowStyles.Add(new RowStyle(SizeType.Percent, 100)); root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            root.Controls.Add(new Label { Text = "Select the optical drives Disc Ripper should manage. The physical drives are remembered even if Windows changes their letters.", AutoSize = true, MaximumSize = new Size(560, 0), Padding = new Padding(0, 0, 0, 8) });
            list.Dock = DockStyle.Fill; list.CheckOnClick = true;
            var selected = new HashSet<string>(selectedIds ?? Enumerable.Empty<string>(), StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < drives.Count; i++) list.Items.Add(drives[i], selected.Contains(drives[i].DeviceId));
            root.Controls.Add(list, 0, 1);
            var buttons = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.RightToLeft, AutoSize = true, Padding = new Padding(0, 8, 0, 0) };
            var save = new Button { Text = "Save", DialogResult = DialogResult.None, AutoSize = true };
            var cancel = new Button { Text = "Cancel", DialogResult = DialogResult.Cancel, AutoSize = true };
            var all = new Button { Text = "Select all", AutoSize = true, Margin = new Padding(3, 3, 20, 3) };
            save.Click += SaveClicked; all.Click += (s, e) => { for (int i = 0; i < list.Items.Count; i++) list.SetItemChecked(i, true); };
            buttons.Controls.Add(save); buttons.Controls.Add(cancel); buttons.Controls.Add(all); root.Controls.Add(buttons, 0, 2);
            Controls.Add(root); AcceptButton = save; CancelButton = cancel;
        }

        private void SaveClicked(object sender, EventArgs e)
        {
            if (list.CheckedIndices.Count == 0)
            {
                MessageBox.Show(this, "Select at least one optical drive.", "Media Nexus ARM", MessageBoxButtons.OK, MessageBoxIcon.Information); return;
            }
            SelectedDeviceIds = list.CheckedIndices.Cast<int>().Select(i => drives[i].DeviceId).ToArray();
            DialogResult = DialogResult.OK; Close();
        }
    }

    internal static class Program
    {
        [STAThread]
        private static void Main()
        {
            Application.EnableVisualStyles(); Application.SetCompatibleTextRenderingDefault(false);
            var detected = DriveSettings.DiscoverOpticalDrives();
            var selectedIds = DriveSettings.LoadSelectedIds();
            if (selectedIds.Length == 0)
            {
                using (var dialog = new DriveSelectionForm(detected, new string[0]))
                {
                    if (dialog.ShowDialog() != DialogResult.OK) return;
                    selectedIds = dialog.SelectedDeviceIds; DriveSettings.SaveSelectedIds(selectedIds);
                }
            }
            var selected = detected.Where(d => selectedIds.Contains(d.DeviceId, StringComparer.OrdinalIgnoreCase)).ToList();
            string outputRoot = AppSettings.LoadOutputRoot();
            if (string.IsNullOrWhiteSpace(outputRoot))
            {
                using (var dialog = new OutputFolderForm(""))
                {
                    if (dialog.ShowDialog() != DialogResult.OK) return;
                    outputRoot = dialog.SelectedPath; AppSettings.SaveOutputRoot(outputRoot);
                }
            }
            Application.Run(new MainForm(selected, outputRoot));
        }
    }
}
