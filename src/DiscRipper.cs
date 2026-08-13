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
using System.Reflection;
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
        public TextBox DiscLabel;
        public ComboBox TypeBox;
        public Label StatusLabel;
        public ProgressBar ProgressBar;
        public Button EjectButton;
        public Button StopButton;
        public bool Present;
        public bool Busy;
        public bool AwaitingChoice;
        public bool SuppressTypeChange;
        public bool ManualTypeSelected;
        public int LastRenderedProgress = -1;
        public int LastQueuedProgress = -1;
        public DateTime LastProgressQueued = DateTime.MinValue;
        public readonly object ProgressSync = new object();
        public DateTime FirstSeen;
        public CancellationTokenSource Cancellation;
        public bool StopRequested;
    }

    internal sealed class MainForm : Form
    {
        private const int GridRowHeight = 44;
        private const int MinLengthSeconds = 600;
        private readonly Dictionary<string, DriveRow> rows = new Dictionary<string, DriveRow>();
        private readonly System.Windows.Forms.Timer pollTimer = new System.Windows.Forms.Timer();
        private readonly FreacManager freac = new FreacManager();
        private readonly SemaphoreSlim makeMkvMapGate = new SemaphoreSlim(1, 1);
        private readonly ConcurrentDictionary<string, int> discIndexes = new ConcurrentDictionary<string, int>();
        private readonly Label footer = new Label();
        private readonly FlowLayoutPanel toolbar = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, WrapContents = false, Padding = new Padding(0, 0, 0, 8) };
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
            this.Icon = System.Drawing.Icon.ExtractAssociatedIcon(Application.ExecutablePath);
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

            BuildToolbar();
            root.Controls.Add(toolbar, 0, 0);

            var gridHost = new Panel { Dock = DockStyle.Fill, AutoScroll = true };
            driveGrid = new ThickBorderTableLayoutPanel { BorderThickness = 3, Location = new Point(0, 0), Anchor = AnchorStyles.Top | AnchorStyles.Left, AutoSize = false, BackColor = Color.FromArgb(218, 218, 218), ColumnCount = 6, RowCount = 1, CellBorderStyle = TableLayoutPanelCellBorderStyle.None, GrowStyle = TableLayoutPanelGrowStyle.FixedSize };
            driveGridFrame = new Panel { Location = new Point(0, 0), Anchor = AnchorStyles.Top | AnchorStyles.Left, BackColor = SystemColors.ControlDark, Padding = new Padding(1) };
            for (int i = 0; i < 6; i++) driveGrid.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, layoutSettings.ColumnWidths[i]));
            driveGrid.Location = new Point(1, 1);
            driveGridFrame.Controls.Add(driveGrid);
            gridHost.Controls.Add(driveGridFrame);
            root.Controls.Add(gridHost, 0, 1);
            RebuildDriveGrid(selectedDrives);

            footer.AutoSize = true;
            footer.Padding = new Padding(0, 8, 0, 0);
            footer.Text = "Select a media type for each inserted disc. No rip starts while Media Type is selected.";
            root.Controls.Add(footer, 0, 2);

            pollTimer.Interval = 3000;
            pollTimer.Tick += PollTimerOnTick;
            Shown += (s, e) => { pollTimer.Start(); PollAll(); };
            ThemeSettings.Apply(this);
            AppSettings.CleanOldLogs(outputRoot, 30);
        }

        private void OpenSettings(object sender, EventArgs e)
        {
            using (var dialog = new SettingsForm(ConfigureDrives, ConfigureOutputFolder, ConfigureLayout, ConfigureMediaTypes, ConfigureAudioEngine, ConfigureTheme, ConfigureBehavior, ShowDiagnostics, OpenLogs, ResetSettings))
                dialog.ShowDialog(this);
        }

        private void BuildToolbar()
        {
            toolbar.SuspendLayout(); toolbar.Controls.Clear();
            toolbar.Controls.Add(new Label { Text = "Change all:", AutoSize = true, Padding = new Padding(0, 8, 6, 0) });
            HashSet<MediaKind> enabled = AppSettings.LoadEnabledMediaTypes();
            foreach (MediaKind kind in new[] { MediaKind.Movie, MediaKind.TVSeries, MediaKind.Music, MediaKind.Book })
                if (enabled.Contains(kind)) AddAllButton(toolbar, DisplayName(kind), kind);
            AddAllButton(toolbar, "Clear", MediaKind.Choose);
            var settingsButton = new Button { Text = "Settings", AutoSize = true, Margin = new Padding(20, 3, 3, 3) };
            settingsButton.Click += OpenSettings; toolbar.Controls.Add(settingsButton);
            var openButton = new Button { Text = "Open Output", AutoSize = true };
            openButton.Click += (s, e) => OpenFolder(outputRoot); toolbar.Controls.Add(openButton);
            var historyButton = new Button { Text = "History", AutoSize = true };
            historyButton.Click += ShowHistory; toolbar.Controls.Add(historyButton);
            toolbar.ResumeLayout(); ThemeSettings.Apply(toolbar);
        }

        private void ConfigureMediaTypes(object sender, EventArgs e)
        {
            if (rows.Values.Any(r => r.Busy))
            {
                MessageBox.Show(this, "Wait for active rips to finish before changing available media types.", "Media Nexus ARM", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            using (var dialog = new MediaTypeSettingsForm(AppSettings.LoadEnabledMediaTypes()))
            {
                if (dialog.ShowDialog(DialogOwner(sender)) != DialogResult.OK) return;
                AppSettings.SaveEnabledMediaTypes(dialog.EnabledKinds);
                BuildToolbar();
                foreach (DriveRow row in rows.Values) PopulateMediaTypes(row.TypeBox, row);
            }
        }

        private void ConfigureBehavior(object sender, EventArgs e)
        {
            using (var dialog = new BehaviorSettingsForm(AppSettings.LoadEjectMode(), AppSettings.LoadSoundsEnabled()))
            {
                if (dialog.ShowDialog(DialogOwner(sender)) != DialogResult.OK) return;
                AppSettings.SaveEjectMode(dialog.EjectMode); AppSettings.SaveSoundsEnabled(dialog.SoundsEnabled);
            }
        }

        private void ShowDiagnostics(object sender, EventArgs e) { using (var dialog = new DiagnosticsForm(outputRoot, makeMkv, freac, rows.Values.ToList())) dialog.ShowDialog(DialogOwner(sender)); }
        private void ShowHistory(object sender, EventArgs e) { using (var dialog = new HistoryForm(outputRoot)) dialog.ShowDialog(DialogOwner(sender)); }
        private void OpenLogs(object sender, EventArgs e) { OpenFolder(Path.Combine(outputRoot, "Logs")); }
        private void ResetSettings(object sender, EventArgs e)
        {
            if (MessageBox.Show(DialogOwner(sender), "Reset Media Nexus ARM settings to defaults? The application must be restarted afterward.", "Media Nexus ARM", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
            AppSettings.ResetAll(); MessageBox.Show(DialogOwner(sender), "Settings were reset. Restart Media Nexus ARM to apply all defaults.", "Media Nexus ARM", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }

        private void ConfigureTheme(object sender, EventArgs e)
        {
            using (var dialog = new ThemeSettingsForm(ThemeSettings.IsDark()))
            {
                if (dialog.ShowDialog(DialogOwner(sender)) != DialogResult.OK) return;
                ThemeSettings.Save(dialog.DarkMode);
                ThemeSettings.Apply(this);
                foreach (Form open in Application.OpenForms) ThemeSettings.Apply(open);
            }
        }

        private void ConfigureLayout(object sender, EventArgs e)
        {
            using (var dialog = new LayoutSettingsForm(layoutSettings, Size))
            {
                if (dialog.ShowDialog(DialogOwner(sender)) != DialogResult.OK) return;
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
                if (dialog.ShowDialog(DialogOwner(sender)) != DialogResult.OK) return;
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
            discIndexes.Clear();
            driveGrid.SuspendLayout();
            driveGrid.Controls.Clear(); driveGrid.RowStyles.Clear(); driveGrid.RowCount = 1; rows.Clear();
            driveGrid.RowStyles.Add(new RowStyle(SizeType.Absolute, GridRowHeight));
            AddHeader(driveGrid, 0, "Drive"); AddHeader(driveGrid, 1, "Device"); AddHeader(driveGrid, 2, "Disc");
            AddHeader(driveGrid, 3, "Media Type"); AddHeader(driveGrid, 4, "Status"); AddHeader(driveGrid, 5, "Action");
            foreach (var drive in selectedDrives.OrderBy(d => d.Letter)) AddDriveRow(driveGrid, drive.Letter, drive.Name);
            ApplyGridGutters();
            UpdateGridBounds();
            driveGrid.ResumeLayout();
            footer.Text = (selectedDrives.Count == 0 ? "No selected drives are currently connected. Use Configure drives." :
                "Select a media type for each inserted disc. No rip starts while Media Type is selected.") + "   Output: " + outputRoot;
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
                if (dialog.ShowDialog(DialogOwner(sender)) != DialogResult.OK) return;
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
            var discLabel = new TextBox { Text = "Empty", Anchor = AnchorStyles.Left | AnchorStyles.Right, Margin = new Padding(5, 0, 5, 0) };
            var type = new ComboBox { Anchor = AnchorStyles.Left | AnchorStyles.Right, DropDownStyle = ComboBoxStyle.DropDownList, Margin = new Padding(5, 0, 5, 0) };
            PopulateMediaTypes(type, null);
            var status = new Label { Text = "Waiting for disc", Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft, AutoEllipsis = true, Padding = new Padding(5, 0, 0, 0) };
            var progress = new ProgressBar { Dock = DockStyle.Fill, Minimum = 0, Maximum = 100, Value = 0, Style = ProgressBarStyle.Continuous, Margin = new Padding(5, 0, 5, 4) };
            var statusPanel = new TableLayoutPanel { Anchor = AnchorStyles.Left | AnchorStyles.Right, Height = 36, BackColor = Color.Transparent, ColumnCount = 1, RowCount = 2, Margin = Padding.Empty, Padding = Padding.Empty };
            statusPanel.RowStyles.Add(new RowStyle(SizeType.Percent, 62)); statusPanel.RowStyles.Add(new RowStyle(SizeType.Percent, 38));
            statusPanel.Controls.Add(status, 0, 0); statusPanel.Controls.Add(progress, 0, 1);
            var actionPanel = new TableLayoutPanel { Anchor = AnchorStyles.Left | AnchorStyles.Right, Height = 34, ColumnCount = 2, RowCount = 1, Margin = Padding.Empty, Padding = new Padding(3, 2, 3, 2) };
            actionPanel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50)); actionPanel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
            var stop = new ThemedButton { Text = "Stop", Dock = DockStyle.Fill, Margin = new Padding(2), Enabled = false };
            var eject = new Button { Text = "Eject", Dock = DockStyle.Fill, Margin = new Padding(2) };
            var item = new DriveRow { Letter = letter, Device = device, DiscLabel = discLabel, TypeBox = type, StatusLabel = status, ProgressBar = progress, EjectButton = eject, StopButton = stop };
            type.SelectedIndexChanged += (s, e) =>
            {
                if (item.SuppressTypeChange || item.Busy) return;
                item.AwaitingChoice = false;
                item.ManualTypeSelected = SelectedKind(item) != MediaKind.Choose;
                PollDrive(item);
            };
            discLabel.KeyDown += (s, e) =>
            {
                if (e.KeyCode != Keys.Up && e.KeyCode != Keys.Down) return;
                MoveDiscEditor(item, e.KeyCode == Keys.Up ? -1 : 1);
                e.Handled = true; e.SuppressKeyPress = true;
            };
            stop.Click += (s, e) => StopRip(item);
            eject.Click += (s, e) => Eject(item.Letter);
            rows[letter] = item;
            grid.Controls.Add(driveLabel, 0, rowIndex); grid.Controls.Add(deviceLabel, 1, rowIndex); grid.Controls.Add(discLabel, 2, rowIndex);
            actionPanel.Controls.Add(stop, 0, 0); actionPanel.Controls.Add(eject, 1, 0);
            grid.Controls.Add(type, 3, rowIndex); grid.Controls.Add(statusPanel, 4, rowIndex); grid.Controls.Add(actionPanel, 5, rowIndex);
        }

        private void MoveDiscEditor(DriveRow current, int direction)
        {
            List<DriveRow> ordered = rows.Values.OrderBy(r => r.Letter, StringComparer.OrdinalIgnoreCase).ToList();
            int index = ordered.IndexOf(current), target = index + direction;
            if (index < 0 || target < 0 || target >= ordered.Count) return;
            ordered[target].DiscLabel.Focus();
            ordered[target].DiscLabel.SelectAll();
        }

        private static void PopulateMediaTypes(ComboBox box, DriveRow row)
        {
            string current = Convert.ToString(box.SelectedItem);
            HashSet<MediaKind> enabled = AppSettings.LoadEnabledMediaTypes();
            if (row != null) row.SuppressTypeChange = true;
            try
            {
                box.Items.Clear(); box.Items.Add("Media Type");
                foreach (MediaKind kind in new[] { MediaKind.Book, MediaKind.Movie, MediaKind.Music, MediaKind.TVSeries })
                    if (enabled.Contains(kind)) box.Items.Add(DisplayName(kind));
                box.SelectedItem = box.Items.Contains(current) ? current : "Media Type";
                if (row != null && Convert.ToString(box.SelectedItem) == "Media Type") row.ManualTypeSelected = false;
            }
            finally { if (row != null) row.SuppressTypeChange = false; }
        }

        private void ApplyGridGutters()
        {
            const int line = 3;
            foreach (Control control in driveGrid.Controls)
            {
                TableLayoutPanelCellPosition position = driveGrid.GetPositionFromControl(control);
                control.Margin = new Padding(line, line, position.Column == driveGrid.ColumnCount - 1 ? line : 0, position.Row == driveGrid.RowCount - 1 ? line : 0);
                if (control is TextBox || control is ComboBox)
                    control.Anchor = AnchorStyles.Left | AnchorStyles.Right;
            }
        }

        private void StopRip(DriveRow row)
        {
            if (!row.Busy || row.Cancellation == null) return;
            row.StopRequested = true;
            row.StopButton.Enabled = false;
            SetStatus(row, "Stopping...", Color.DarkOrange);
            row.Cancellation.Cancel();
        }

        private void PollTimerOnTick(object sender, EventArgs e)
        {
            pollTimer.Stop();
            try { PollAll(); }
            finally { if (!closing) pollTimer.Start(); }
        }

        private void PollAll()
        {
            foreach (var row in rows.Values) PollDrive(row);
        }

        private void PollDrive(DriveRow row)
        {
            // Active extraction owns the drive. Polling it from the UI thread adds
            // contention and can make a multi-drive session visibly unresponsive.
            if (row.Busy) return;
            bool present = IsMediaPresent(row.Letter);
            if (!present)
            {
                row.Present = false;
                row.AwaitingChoice = false;
                row.ManualTypeSelected = false;
                row.FirstSeen = DateTime.MinValue;
                row.DiscLabel.Text = "Empty";
                if (!row.Busy) { SetType(row, MediaKind.Choose); SetStatus(row, "Waiting for disc", Color.DimGray); SetProgress(row, 0); }
                return;
            }

            bool newlySeen = !row.Present;
            if (newlySeen || row.DiscLabel.Text == "Empty")
            {
                string label = GetVolumeLabel(row.Letter);
                row.DiscLabel.Text = string.IsNullOrWhiteSpace(label) ? "Audio/unknown disc" : label;
            }
            if (newlySeen) { row.Present = true; row.FirstSeen = DateTime.Now; }
            if (row.AwaitingChoice) return;
            if ((DateTime.Now - row.FirstSeen).TotalSeconds < 4)
            {
                SetStatus(row, "Disc detected - waiting for drive to settle...", Color.DarkBlue);
                return;
            }

            MediaKind kind = SelectedKind(row);
            if (kind == MediaKind.Choose || !row.ManualTypeSelected)
            {
                SetStatus(row, "Disc detected - select a media type", Color.DarkOrange);
                SetProgress(row, 0);
                if (newlySeen) SystemSounds.Asterisk.Play();
                return;
            }

            string outputError = AppSettings.CheckOutput(outputRoot);
            if (outputError != null) { SetStatus(row, outputError, Color.DarkRed); return; }

            row.Busy = true;
            row.StopRequested = false;
            SetProgress(row, 0);
            row.TypeBox.Enabled = false;
            row.StopButton.Enabled = true;
            row.Cancellation = new CancellationTokenSource();
            DateTime jobStarted = DateTime.Now;
            Task.Run(async () =>
            {
                bool ok = false;
                try
                {
                    ok = await AnalyzeAndRip(row, kind, row.Cancellation.Token);
                }
                catch (OperationCanceledException) { if (!row.StopRequested) Ui(() => SetStatus(row, "Cancelled", Color.DarkOrange)); }
                catch (Exception ex) { Ui(() => SetStatus(row, "Failed: " + ex.Message, Color.DarkRed)); }
                finally
                {
                    bool stopped = row.StopRequested;
                    RecordJobResult(row, kind, jobStarted, stopped ? "Stopped" : ok ? "Completed" : "Failed");
                    string ejectMode = AppSettings.LoadEjectMode();
                    bool autoEject = !stopped && (ejectMode == "Always" || (ejectMode == "Success" && ok));
                    if (autoEject) Eject(row.Letter);
                    if (!stopped) PlayCompletionSound(ok);
                    Ui(() =>
                    {
                        if (row.AwaitingChoice) { row.Busy = false; row.TypeBox.Enabled = true; return; }
                        if (stopped)
                        {
                            SetProgress(row, 0); SetStatus(row, "Stopped - disc remains inserted", Color.DarkOrange);
                            row.Busy = false; row.TypeBox.Enabled = true; row.StopButton.Enabled = false;
                            row.ManualTypeSelected = false; SetType(row, MediaKind.Choose); row.StopRequested = false; return;
                        }
                        if (ok) SetProgress(row, 100);
                        SetStatus(row, ok ? (autoEject ? "Complete - ejected" : "Complete - disc remains inserted") : (autoEject ? "Failed - ejected" : "Failed - disc remains inserted"), ok ? Color.DarkGreen : Color.DarkRed);
                        row.Busy = false;
                        row.TypeBox.Enabled = true;
                        row.StopButton.Enabled = false;
                        row.ManualTypeSelected = false;
                        SetType(row, MediaKind.Choose);
                    });
                }
            });
        }

        private void RecordJobResult(DriveRow row, MediaKind kind, DateTime started, string result)
        {
            try
            {
                string folder = Path.Combine(outputRoot, "Logs");
                if (!Directory.Exists(folder)) return;
                string driveToken = "_" + row.Letter + "_";
                string log = Directory.GetFiles(folder, "*.log").Where(path => Path.GetFileName(path).IndexOf(driveToken, StringComparison.OrdinalIgnoreCase) >= 0 && File.GetCreationTime(path) >= started.AddSeconds(-3)).OrderByDescending(File.GetCreationTime).FirstOrDefault();
                if (log == null) return;
                File.AppendAllText(log, DateTime.Now.ToString("O") + "  Disc: " + SafeName(row.DiscLabel.Text) + Environment.NewLine + DateTime.Now.ToString("O") + "  Media type: " + DisplayName(kind) + Environment.NewLine + DateTime.Now.ToString("O") + "  Job result: " + result + Environment.NewLine, Encoding.UTF8);
            }
            catch { }
        }

        private async Task<bool> AnalyzeAndRip(DriveRow row, MediaKind requested, CancellationToken token)
        {
            if (!row.ManualTypeSelected || requested == MediaKind.Choose)
                throw new InvalidOperationException("A media type must be selected manually before a rip can start.");
            DiscAnalysis analysis = null;
            int discIndex = -1;

            if (requested == MediaKind.Music || requested == MediaKind.Book)
            {
                DiscToc toc = analysis == null ? null : analysis.AudioToc;
                if (toc == null) toc = await WaitForAudioToc(row, token);
                if (toc == null) throw new InvalidOperationException("This does not appear to be an audio CD.");
                return await RipAudio(row, requested, toc, token);
            }

            if (analysis == null)
            {
                discIndex = await GetMakeMkvDiscIndex(row.Letter);
                ProcessResult info = await RunMakeMkvInfo(discIndex, row.Letter, token);
                analysis = DiscAnalyzer.AnalyzeVideo(info.Output);
                analysis.Kind = requested;
                List<int> selected;
                int automaticTitle; string automaticReason;
                if (requested == MediaKind.Movie && DiscAnalyzer.TrySelectHighConfidenceMovie(analysis.VideoTitles, out automaticTitle, out automaticReason))
                {
                    selected = new List<int> { automaticTitle };
                    WriteProbeLog(row.Letter, "Movie title selected automatically: title " + automaticTitle + " (" + automaticReason + ").", info.Output);
                    Ui(() => SetStatus(row, "High-confidence movie title found - starting rip...", Color.DarkGreen));
                }
                else selected = await SelectVideoTitles(row.Letter, requested, analysis.VideoTitles);
                if (selected == null) throw new OperationCanceledException("Title selection was cancelled.");
                analysis.SelectedTitleIds.Clear(); analysis.SelectedTitleIds.AddRange(selected);
            }
            return await RipVideo(row, requested, analysis, discIndex, token);
        }

        private Task<List<int>> SelectVideoTitles(string driveLetter, MediaKind kind, IList<VideoTitleInfo> titles)
        {
            var completion = new TaskCompletionSource<List<int>>();
            Ui(() =>
            {
                using (var dialog = new VideoSelectionForm(kind, driveLetter, titles))
                {
                    DialogResult result = dialog.ShowDialog(this);
                    completion.SetResult(result == DialogResult.OK ? dialog.SelectedTitleIds : null);
                }
            });
            return completion.Task;
        }

        private async Task<DiscToc> WaitForAudioToc(DriveRow row, CancellationToken token)
        {
            for (int attempt = 1; attempt <= 5; attempt++)
            {
                token.ThrowIfCancellationRequested();
                DiscToc toc = NativeDisc.TryReadAudioToc(row.Letter);
                if (toc != null) return toc;
                if (attempt < 5)
                {
                    Ui(() => SetStatus(row, "Checking for audio CD (" + attempt + " of 5)...", Color.Purple));
                    await Task.Delay(1500, token);
                }
            }
            return null;
        }

        private void ConfigureAudioEngine(object sender, EventArgs e)
        {
            if (rows.Values.Any(r => r.Busy))
            {
                MessageBox.Show(this, "Wait for active rips to finish before installing or updating the audio engine.", "Media Nexus ARM", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            using (var dialog = new FreacStatusForm(freac)) dialog.ShowDialog(DialogOwner(sender));
        }

        private IWin32Window DialogOwner(object sender)
        {
            Control control = sender as Control;
            return control == null ? (IWin32Window)this : control.FindForm();
        }

        private async Task<int> GetMakeMkvDiscIndex(string letter)
        {
            int index;
            if (discIndexes.TryGetValue(letter, out index)) return index;
            await makeMkvMapGate.WaitAsync();
            try
            {
                if (discIndexes.TryGetValue(letter, out index)) return index;
                await RefreshMakeMkvMap();
                if (!discIndexes.TryGetValue(letter, out index)) throw new InvalidOperationException("MakeMKV could not map this drive");
            }
            finally { makeMkvMapGate.Release(); }
            return index;
        }

        private async Task<ProcessResult> RunMakeMkvInfo(int discIndex, string letter, CancellationToken token)
        {
            ProcessResult last = null;
            for (int attempt = 1; attempt <= 3; attempt++)
            {
                token.ThrowIfCancellationRequested();
                if (!IsMediaPresent(letter)) throw new InvalidOperationException("The disc is no longer available.");
                last = await RunProcess(makeMkv, "-r --noscan --cache=1 info disc:" + discIndex, token);
                bool usable = last.ExitCode == 0 && last.Output.IndexOf("TINFO:", StringComparison.OrdinalIgnoreCase) >= 0;
                if (usable) { WriteProbeLog(letter, "MakeMKV disc analysis.", last.Output); return last; }
                if (attempt < 3) await Task.Delay(2000, token);
            }
            WriteProbeLog(letter, "MakeMKV disc-info failed after three attempts.", last == null ? "No MakeMKV output." : last.Output);
            throw new InvalidOperationException("MakeMKV could not read the disc after three attempts. The disc remains inserted. " + (last == null ? "" : FirstMakeMkvError(last.Output)));
        }

        private void WriteProbeLog(string letter, string heading, string output)
        {
            try
            {
                string folder = Path.Combine(outputRoot, "Logs"); Directory.CreateDirectory(folder);
                string path = Path.Combine(folder, "makemkv_probe_" + letter + "_" + DateTime.Now.ToString("yyyy-MM-dd_HH-mm-ss") + ".log");
                File.WriteAllText(path, heading + Environment.NewLine + output, Encoding.UTF8);
            }
            catch { }
        }

        private static string FirstMakeMkvError(string output)
        {
            foreach (string line in (output ?? "").Split('\n'))
                if (line.IndexOf("error", StringComparison.OrdinalIgnoreCase) >= 0 || line.IndexOf("failed", StringComparison.OrdinalIgnoreCase) >= 0) return line.Trim();
            return "See the MakeMKV job log for details.";
        }

        private async Task<bool> RipVideo(DriveRow row, MediaKind kind, DiscAnalysis analysis, int discIndex, CancellationToken token)
        {
            if (discIndex < 0) discIndex = await GetMakeMkvDiscIndex(row.Letter);

            string typeFolder = kind == MediaKind.Movie ? "Movies" : "TV Series";
            string discName = SafeName(GetVolumeLabel(row.Letter));
            if (string.IsNullOrWhiteSpace(discName) || discName == "UNKNOWN_DISC") discName = "DISC_" + row.Letter;
            string outDir = UniqueDiscFolder(Path.Combine(outputRoot, typeFolder), discName);
            Directory.CreateDirectory(outDir);
            string logDir = Path.Combine(outputRoot, "Logs"); Directory.CreateDirectory(logDir);
            string logPath = Path.Combine(logDir, "makemkv_" + row.Letter + "_" + DateTime.Now.ToString("yyyy-MM-dd_HH-mm-ss") + ".log");
            File.AppendAllText(logPath, "Drive: " + row.Letter + ":" + Environment.NewLine + "Disc: " + discName + Environment.NewLine + "Detected type: " + DisplayName(kind) + Environment.NewLine + "Selected MakeMKV titles: " + string.Join(",", analysis.SelectedTitleIds) + Environment.NewLine, Encoding.UTF8);

            var titleIds = analysis.SelectedTitleIds.Distinct().ToList();
            if (titleIds.Count == 0) throw new InvalidOperationException(kind == MediaKind.TVSeries ? "No high-confidence episode set was found." : "No probable main feature was found.");
            bool allOk = true;
            int completedTitles = 0;
            var rippedFiles = new List<string>();
            foreach (int title in titleIds.OrderBy(x => x))
            {
                token.ThrowIfCancellationRequested();
                string titleText = "title " + title;
                Ui(() => { SetStatus(row, "Ripping " + titleText + "...", Color.DarkBlue); SetProgress(row, completedTitles * 100 / titleIds.Count); });
                string target = title.ToString();
                var filesBefore = new HashSet<string>(Directory.GetFiles(outDir, "*.mkv"), StringComparer.OrdinalIgnoreCase);
                int titlesDoneAtStart = completedTitles;
                VideoTitleInfo selectedTitle = analysis.VideoTitles.FirstOrDefault(item => item.Id == title);
                long expectedMovieBytes = kind == MediaKind.Movie && selectedTitle != null ? selectedTitle.SizeBytes : 0;
                Func<long> movieBytesWritten = expectedMovieBytes > 0 ? (Func<long>)(() =>
                    Directory.GetFiles(outDir, "*.mkv").Where(path => !filesBefore.Contains(path)).Sum(path =>
                    {
                        try { return new FileInfo(path).Length; }
                        catch { return 0L; }
                    })) : null;
                var result = await RunProcess(makeMkv, "-r --noscan --minlength=" + MinLengthSeconds + " mkv disc:" + discIndex + " " + target + " \"" + outDir + "\"", token, percent =>
                {
                    int wholeDiscPercent = Math.Min(99, ((titlesDoneAtStart * 100) + percent) / titleIds.Count);
                    QueueProgress(row, wholeDiscPercent);
                }, true, movieBytesWritten, expectedMovieBytes);
                File.AppendAllText(logPath, result.Output, Encoding.UTF8);
                bool copied = result.Output.IndexOf("Copy complete", StringComparison.OrdinalIgnoreCase) >= 0 ||
                              (result.ExitCode == 0 && Directory.GetFiles(outDir, "*.mkv").Length > filesBefore.Count);
                if (!copied) { allOk = false; break; }
                string created = Directory.GetFiles(outDir, "*.mkv").Where(path => !filesBefore.Contains(path)).OrderByDescending(path => new FileInfo(path).LastWriteTimeUtc).FirstOrDefault();
                if (created != null) { rippedFiles.Add(created); File.AppendAllText(logPath, "MakeMKV title " + title + " -> " + created + Environment.NewLine, Encoding.UTF8); }
                completedTitles++;
            }
            if (allOk)
            {
                string final = await NameVideoOutput(row, kind, discName, rippedFiles, logPath);
                File.AppendAllText(logPath, "Completed output: " + final + Environment.NewLine, Encoding.UTF8);
            }
            return allOk;
        }

        private async Task<string> NameVideoOutput(DriveRow row, MediaKind kind, string discName, IList<string> rippedFiles, string logPath)
        {
            if (rippedFiles.Count == 0) return Path.GetDirectoryName(logPath);
            string editedName = await ReadDiscName(row);
            if (!string.IsNullOrWhiteSpace(editedName) && editedName != "UNKNOWN_DISC" && !string.Equals(editedName, "Audio_unknown disc", StringComparison.OrdinalIgnoreCase)) discName = editedName;
            File.AppendAllText(logPath, "User-edited disc name: " + discName + Environment.NewLine, Encoding.UTF8);
            Action<string> log = message => File.AppendAllText(logPath, message + Environment.NewLine, Encoding.UTF8);
            if (kind == MediaKind.TVSeries)
            {
                string tvFinal = VideoOrganizer.OrganizeTvOriginalNames(rippedFiles, outputRoot, discName, log);
                string tvOriginal = Path.GetDirectoryName(rippedFiles[0]);
                try { if (Directory.Exists(tvOriginal) && !Directory.EnumerateFileSystemEntries(tvOriginal).Any()) Directory.Delete(tvOriginal); } catch { }
                return tvFinal;
            }
            string final = VideoOrganizer.OrganizeMovieFromDiscName(rippedFiles[0], outputRoot, discName, log);
            string original = Path.GetDirectoryName(rippedFiles[0]);
            try { if (Directory.Exists(original) && !Directory.EnumerateFileSystemEntries(original).Any()) Directory.Delete(original); } catch { }
            return final;
        }

        private Task<string> ReadDiscName(DriveRow row)
        {
            var completion = new TaskCompletionSource<string>();
            Ui(() => completion.SetResult(SafeName(row.DiscLabel.Text)));
            return completion.Task;
        }

        private async Task<bool> RipAudio(DriveRow row, MediaKind kind, DiscToc toc, CancellationToken token)
        {
            using (var log = new JobLog(outputRoot, row.Letter, DisplayName(kind)))
            {
                log.Write("Audio CD: " + toc.DiscId + " / " + toc.TrackOffsets.Count + " tracks");
                MusicRelease release = null;
                byte[] cover = null;
                if (kind == MediaKind.Music)
                {
                    Ui(() => SetStatus(row, "Looking up MusicBrainz metadata...", Color.Purple));
                    var client = new MusicBrainzClient();
                    List<MusicRelease> releases;
                    try { releases = await client.LookupDiscAsync(toc, token); }
                    catch (Exception ex) { releases = new List<MusicRelease>(); log.Write("MusicBrainz unavailable: " + ex.Message); }
                    if (releases.Count == 1) release = releases[0];
                    else if (releases.Count > 1) release = await SelectRelease(releases);
                    if (release != null) cover = await client.TryDownloadCoverAsync(release.Id, token);
                    log.Write(release == null ? "No release selected; preserving in Pending Metadata." : "Release: " + release.DisplayName);
                }

                await freac.EnsureInstalledAsync(message => Ui(() => SetStatus(row, message, Color.Purple)), token);
                string staging = Path.Combine(outputRoot, "Staging", "Audio", row.Letter + "_" + DateTime.Now.ToString("yyyy-MM-dd_HH-mm-ss"));
                FreacRipResult rip = await freac.RipAlacAsync(row.Letter, toc, staging, null, p =>
                {
                    int track = Math.Min(toc.TrackOffsets.Count, (p * toc.TrackOffsets.Count / 100) + 1);
                    Ui(() => SetStatus(row, "Ripping ALAC track " + track + " of " + toc.TrackOffsets.Count, Color.Purple));
                    QueueProgress(row, p);
                }, token);
                log.Write(rip.Output);
                if (!rip.Success) throw new InvalidOperationException("fre:ac did not produce the expected number of ALAC tracks. See " + log.PathName);

                string result;
                if (kind == MediaKind.Music && release != null) result = MusicOrganizer.TagAndOrganize(rip.Files, release, toc, cover, outputRoot, log.Write);
                else if (kind == MediaKind.Music) result = MusicOrganizer.MoveToPending(rip.Files, toc, outputRoot, log.Write);
                else
                {
                    result = UniqueDiscFolder(Path.Combine(outputRoot, "Audiobooks"), SafeName(GetVolumeLabel(row.Letter)));
                    Directory.CreateDirectory(result);
                    for (int i = 0; i < rip.Files.Length; i++) File.Copy(rip.Files[i], Path.Combine(result, string.Format("{0:00}.m4a", i + 1)), false);
                }
                log.Write("Completed: " + result);
                try { Directory.Delete(staging, true); } catch { }
                return true;
            }
        }

        private Task<MusicRelease> SelectRelease(IList<MusicRelease> releases)
        {
            var completion = new TaskCompletionSource<MusicRelease>();
            Ui(() =>
            {
                using (var dialog = new ReleaseSelectionForm(releases))
                {
                    DialogResult result = dialog.ShowDialog(this);
                    completion.SetResult(result == DialogResult.OK ? dialog.SelectedRelease : null);
                }
            });
            return completion.Task;
        }

        private async Task RefreshMakeMkvMap()
        {
            if (string.IsNullOrWhiteSpace(makeMkv) || !File.Exists(makeMkv)) { Ui(() => footer.Text = "MakeMKV was not found. Install MakeMKV in its standard Program Files location."); return; }
            try
            {
                var result = await RunProcess(makeMkv, "-r --noscan --cache=1 info disc:9999", CancellationToken.None);
                var discovered = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
                foreach (string line in result.Output.Split('\n'))
                {
                    var match = Regex.Match(line, "^DRV:(\\d+),.*?,\\\"([A-Z]):\\\"\\s*$");
                    if (match.Success) discovered[match.Groups[2].Value] = int.Parse(match.Groups[1].Value);
                }
                if (discovered.Count == 0) throw new InvalidOperationException("MakeMKV returned no optical-drive mappings.");
                discIndexes.Clear();
                foreach (var pair in discovered) discIndexes[pair.Key] = pair.Value;
            }
            catch { }
        }

        private sealed class ProcessResult { public int ExitCode; public string Output; }
        private static async Task<ProcessResult> RunProcess(string file, string arguments, CancellationToken token, Action<int> progress = null, bool useCurrentProgress = false, Func<long> observedBytes = null, long expectedBytes = 0)
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
                    if (observedBytes != null && expectedBytes > 0) return;
                    var match = Regex.Match(line.Trim(), @"^PRGV:(\d+),(\d+),(\d+)");
                    if (!match.Success) return;
                    long current = long.Parse(match.Groups[1].Value), total = long.Parse(match.Groups[2].Value), maximum = long.Parse(match.Groups[3].Value);
                    if (maximum <= 0) return;
                    long value = useCurrentProgress ? current : (total > 0 ? total : current);
                    progress((int)Math.Max(0, Math.Min(100, value * 100 / maximum)));
                };
                process.OutputDataReceived += (s, e) => handleLine(e.Data);
                process.ErrorDataReceived += (s, e) => handleLine(e.Data);
                process.Start(); process.BeginOutputReadLine(); process.BeginErrorReadLine();
                Task fileProgress = observedBytes == null || expectedBytes <= 0 ? Task.FromResult(0) : Task.Run(async () =>
                {
                    while (!process.HasExited)
                    {
                        try
                        {
                            long written = observedBytes();
                            progress((int)Math.Max(0, Math.Min(98, written * 100 / expectedBytes)));
                        }
                        catch { }
                        await Task.Delay(750);
                    }
                });
                using (token.Register(() => { try { if (!process.HasExited) process.Kill(); } catch { } }))
                    await Task.Run(() => process.WaitForExit(), token);
                await fileProgress;
                return new ProcessResult { ExitCode = process.ExitCode, Output = output.ToString() };
            }
        }

        private static MediaKind SelectedKind(DriveRow row)
        {
            switch (Convert.ToString(row.TypeBox.SelectedItem))
            {
                case "Movie": return MediaKind.Movie; case "TV Series": return MediaKind.TVSeries;
                case "Audiobook": return MediaKind.Book; case "Music": return MediaKind.Music; default: return MediaKind.Choose;
            }
        }
        private static string DisplayName(MediaKind kind) { return kind == MediaKind.TVSeries ? "TV Series" : kind == MediaKind.Book ? "Audiobook" : kind == MediaKind.Choose ? "Media Type" : kind.ToString(); }
        private static void SetType(DriveRow row, MediaKind kind)
        {
            row.SuppressTypeChange = true;
            try { row.TypeBox.SelectedItem = DisplayName(kind); }
            finally { row.SuppressTypeChange = false; }
        }
        private void SetStatus(DriveRow row, string text, Color color)
        {
            if (row.StatusLabel.Text != text) row.StatusLabel.Text = text;
            Color themedText = ThemeSettings.IsDark() ? Color.White : Color.Black;
            if (row.StatusLabel.ForeColor != themedText) row.StatusLabel.ForeColor = themedText;
        }
        private void QueueProgress(DriveRow row, int value)
        {
            value = Math.Max(0, Math.Min(100, value));
            lock (row.ProgressSync)
            {
                DateTime now = DateTime.UtcNow;
                if (value == row.LastQueuedProgress) return;
                if (value < 99 && (now - row.LastProgressQueued).TotalMilliseconds < 250) return;
                row.LastQueuedProgress = value;
                row.LastProgressQueued = now;
            }
            Ui(() => SetProgress(row, value));
        }
        private static void SetProgress(DriveRow row, int value)
        {
            value = Math.Max(row.ProgressBar.Minimum, Math.Min(row.ProgressBar.Maximum, value));
            if (row.LastRenderedProgress == value) return;
            row.ProgressBar.Style = ProgressBarStyle.Continuous;
            row.ProgressBar.Value = value;
            row.LastRenderedProgress = value;
            lock (row.ProgressSync) row.LastQueuedProgress = value;
        }
        private void Ui(Action action) { if (closing || IsDisposed) return; if (InvokeRequired) BeginInvoke(action); else action(); }
        private static void PlayCompletionSound(bool success) { if (!AppSettings.LoadSoundsEnabled()) return; if (success) SystemSounds.Asterisk.Play(); else SystemSounds.Hand.Play(); }
        private const uint SemFailCriticalErrors = 0x0001;
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool GetVolumeInformation(string rootPath, StringBuilder volumeName, int volumeNameSize, out uint serialNumber, out uint maximumComponentLength, out uint fileSystemFlags, StringBuilder fileSystemName, int fileSystemNameSize);
        [DllImport("kernel32.dll")]
        private static extern bool SetThreadErrorMode(uint newMode, out uint oldMode);
        private static string GetVolumeLabel(string letter)
        {
            uint oldMode = 0;
            bool changed = SetThreadErrorMode(SemFailCriticalErrors, out oldMode);
            try
            {
                var volume = new StringBuilder(261); var fileSystem = new StringBuilder(261);
                uint serial, maximumComponentLength, flags;
                return GetVolumeInformation(letter.TrimEnd(':') + @":\", volume, volume.Capacity, out serial, out maximumComponentLength, out flags, fileSystem, fileSystem.Capacity) ? volume.ToString() : "";
            }
            catch { return ""; }
            finally { if (changed) { uint ignored; SetThreadErrorMode(oldMode, out ignored); } }
        }
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

    internal sealed class ThickBorderTableLayoutPanel : TableLayoutPanel
    {
        public int BorderThickness { get; set; }
        public ThickBorderTableLayoutPanel() { DoubleBuffered = true; BorderThickness = 3; }
        protected override void OnCellPaint(TableLayoutCellPaintEventArgs e)
        {
            base.OnCellPaint(e);
            int thickness = Math.Max(1, BorderThickness);
            Color line = ThemeSettings.IsDark() ? Color.FromArgb(115, 115, 120) : Color.FromArgb(125, 125, 125);
            using (var brush = new SolidBrush(line))
            {
                e.Graphics.FillRectangle(brush, e.CellBounds.Left, e.CellBounds.Top, thickness, e.CellBounds.Height);
                e.Graphics.FillRectangle(brush, e.CellBounds.Left, e.CellBounds.Top, e.CellBounds.Width, thickness);
                if (e.Column == ColumnCount - 1) e.Graphics.FillRectangle(brush, e.CellBounds.Right - thickness, e.CellBounds.Top, thickness, e.CellBounds.Height);
                if (e.Row == RowCount - 1) e.Graphics.FillRectangle(brush, e.CellBounds.Left, e.CellBounds.Bottom - thickness, e.CellBounds.Width, thickness);
            }
        }
    }

    internal sealed class ThemedButton : Button
    {
        public ThemedButton() { FlatStyle = FlatStyle.Flat; UseVisualStyleBackColor = false; }
        protected override void OnPaint(PaintEventArgs e)
        {
            bool dark = ThemeSettings.IsDark();
            Color background = dark ? (Enabled ? Color.FromArgb(70, 70, 74) : Color.FromArgb(56, 56, 59)) : (Enabled ? SystemColors.Control : Color.FromArgb(232, 232, 232));
            Color foreground = dark ? Color.White : Color.Black;
            e.Graphics.Clear(background);
            using (var pen = new Pen(dark ? Color.FromArgb(125, 125, 130) : Color.FromArgb(120, 120, 120)))
                e.Graphics.DrawRectangle(pen, 0, 0, Math.Max(0, ClientSize.Width - 1), Math.Max(0, ClientSize.Height - 1));
            TextRenderer.DrawText(e.Graphics, Text, Font, ClientRectangle, foreground, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine);
            if (Focused && ShowFocusCues) ControlPaint.DrawFocusRectangle(e.Graphics, Rectangle.Inflate(ClientRectangle, -3, -3), foreground, background);
        }
    }

    internal sealed class SettingsForm : Form
    {
        public SettingsForm(EventHandler configureDrives, EventHandler configureOutput, EventHandler configureLayout, EventHandler configureMediaTypes, EventHandler configureAudio, EventHandler configureTheme, EventHandler configureBehavior, EventHandler diagnostics, EventHandler logs, EventHandler reset)
        {
            Text = "Media Nexus ARM - Settings"; StartPosition = FormStartPosition.CenterParent;
            Font = new Font("Segoe UI", 9F); FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false; MinimizeBox = false; ClientSize = new Size(590, 660);
            var root = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(18), ColumnCount = 1, RowCount = 12 };
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            for (int i = 1; i <= 10; i++) root.RowStyles.Add(new RowStyle(SizeType.Percent, 10));
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            root.Controls.Add(new Label { Text = "Settings", Font = new Font("Segoe UI", 15F, FontStyle.Bold), AutoSize = true, Padding = new Padding(0, 0, 0, 10) }, 0, 0);
            AddSettingButton(root, 1, "Optical Drives", "Choose which connected optical drives Media Nexus ARM manages.", configureDrives);
            AddSettingButton(root, 2, "Output Folder", "Choose the root folder used for media, staging files, and logs.", configureOutput);
            AddSettingButton(root, 3, "Window and Columns", "Set the window dimensions and individual column widths.", configureLayout);
            AddSettingButton(root, 4, "Media Types", "Choose which media types appear in dropdowns and the Change all toolbar.", configureMediaTypes);
            AddSettingButton(root, 5, "Audio Engine", "View, install, or update the managed fre:ac audio engine.", configureAudio);
            AddSettingButton(root, 6, "Appearance", "Choose the Light or Dark application theme.", configureTheme);
            AddSettingButton(root, 7, "Completion Behavior", "Choose automatic eject behavior and completion sounds.", configureBehavior);
            AddSettingButton(root, 8, "Diagnostics and About", "Check dependencies, output storage, version, and selected drives.", diagnostics);
            AddSettingButton(root, 9, "Logs", "Open the lightweight job-log folder.", logs);
            AddSettingButton(root, 10, "Reset Settings", "Restore application settings to defaults.", reset);
            var closeRow = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.RightToLeft, AutoSize = true, Padding = new Padding(0, 12, 0, 0) };
            var close = new Button { Text = "Close", DialogResult = DialogResult.OK, AutoSize = true };
            closeRow.Controls.Add(close); root.Controls.Add(closeRow, 0, 11); Controls.Add(root); AcceptButton = close; CancelButton = close;
            ThemeSettings.Apply(this);
        }

        private static void AddSettingButton(TableLayoutPanel root, int row, string title, string description, EventHandler action)
        {
            var panel = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1, Margin = new Padding(0, 4, 0, 4) };
            panel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100)); panel.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            var text = new Label { Text = title + Environment.NewLine + description, Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft };
            var button = new Button { Text = "Configure", AutoSize = true, Anchor = AnchorStyles.Right };
            button.Click += action; panel.Controls.Add(text, 0, 0); panel.Controls.Add(button, 1, 0); root.Controls.Add(panel, 0, row);
        }
    }

    internal sealed class MediaTypeSettingsForm : Form
    {
        private readonly CheckedListBox types = new CheckedListBox { Dock = DockStyle.Fill, CheckOnClick = true };
        public HashSet<MediaKind> EnabledKinds { get; private set; }
        public MediaTypeSettingsForm(ISet<MediaKind> enabled)
        {
            Text = "Media Nexus ARM - Media Types"; StartPosition = FormStartPosition.CenterParent; Font = new Font("Segoe UI", 9F);
            FormBorderStyle = FormBorderStyle.FixedDialog; MaximizeBox = false; MinimizeBox = false; ClientSize = new Size(430, 275);
            var root = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(16), ColumnCount = 1, RowCount = 3 };
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize)); root.RowStyles.Add(new RowStyle(SizeType.Percent, 100)); root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            root.Controls.Add(new Label { Text = "Select the media types you want shown. At least one must remain enabled.", AutoSize = true, Padding = new Padding(0, 0, 0, 10) }, 0, 0);
            foreach (MediaKind kind in new[] { MediaKind.Book, MediaKind.Movie, MediaKind.Music, MediaKind.TVSeries }) types.Items.Add(MainFormMediaNames.Display(kind), enabled.Contains(kind));
            root.Controls.Add(types, 0, 1);
            var buttons = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.RightToLeft, AutoSize = true, Padding = new Padding(0, 12, 0, 0) };
            var save = new Button { Text = "Save", AutoSize = true }; var cancel = new Button { Text = "Cancel", DialogResult = DialogResult.Cancel, AutoSize = true };
            save.Click += SaveClicked; buttons.Controls.Add(save); buttons.Controls.Add(cancel); root.Controls.Add(buttons, 0, 2);
            Controls.Add(root); AcceptButton = save; CancelButton = cancel; ThemeSettings.Apply(this);
        }
        private void SaveClicked(object sender, EventArgs e)
        {
            if (types.CheckedItems.Count == 0) { MessageBox.Show(this, "Keep at least one media type enabled.", "Media Nexus ARM", MessageBoxButtons.OK, MessageBoxIcon.Information); return; }
            EnabledKinds = new HashSet<MediaKind>();
            foreach (object item in types.CheckedItems)
            {
                MediaKind kind; if (MainFormMediaNames.TryParse(Convert.ToString(item), out kind)) EnabledKinds.Add(kind);
            }
            DialogResult = DialogResult.OK; Close();
        }
    }

    internal static class MainFormMediaNames
    {
        public static string Display(MediaKind kind) { return kind == MediaKind.Book ? "Audiobook" : kind == MediaKind.TVSeries ? "TV Series" : kind.ToString(); }
        public static bool TryParse(string text, out MediaKind kind)
        {
            if (text == "Audiobook" || text == "Book") { kind = MediaKind.Book; return true; }
            if (text == "TV Series") { kind = MediaKind.TVSeries; return true; }
            return Enum.TryParse(text, out kind) && kind != MediaKind.Choose;
        }
    }

    internal sealed class ThemeSettingsForm : Form
    {
        private readonly ComboBox theme = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Dock = DockStyle.Fill };
        public bool DarkMode { get { return Convert.ToString(theme.SelectedItem) == "Dark"; } }
        public ThemeSettingsForm(bool dark)
        {
            Text = "Media Nexus ARM - Appearance"; StartPosition = FormStartPosition.CenterParent; Font = new Font("Segoe UI", 9F);
            FormBorderStyle = FormBorderStyle.FixedDialog; MaximizeBox = false; MinimizeBox = false; ClientSize = new Size(420, 160);
            var root = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(16), ColumnCount = 2, RowCount = 2 };
            root.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 120)); root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            theme.Items.AddRange(new object[] { "Light", "Dark" }); theme.SelectedItem = dark ? "Dark" : "Light";
            root.Controls.Add(new Label { Text = "Color theme", Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft }, 0, 0); root.Controls.Add(theme, 1, 0);
            var buttons = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.RightToLeft, AutoSize = true, Padding = new Padding(0, 14, 0, 0) };
            var apply = new Button { Text = "Apply", DialogResult = DialogResult.OK, AutoSize = true }; var cancel = new Button { Text = "Cancel", DialogResult = DialogResult.Cancel, AutoSize = true };
            buttons.Controls.Add(apply); buttons.Controls.Add(cancel); root.Controls.Add(buttons, 0, 1); root.SetColumnSpan(buttons, 2); Controls.Add(root); AcceptButton = apply; CancelButton = cancel;
            ThemeSettings.Apply(this, dark);
        }
    }

    internal sealed class BehaviorSettingsForm : Form
    {
        private readonly ComboBox eject = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Dock = DockStyle.Fill };
        private readonly CheckBox sounds = new CheckBox { Text = "Play different sounds for successful and failed jobs", AutoSize = true };
        public string EjectMode { get { string value = Convert.ToString(eject.SelectedItem); return value.StartsWith("Success") ? "Success" : value.StartsWith("All") ? "Always" : "Never"; } }
        public bool SoundsEnabled { get { return sounds.Checked; } }
        public BehaviorSettingsForm(string ejectMode, bool soundsEnabled)
        {
            Text = "Media Nexus ARM - Completion Behavior"; StartPosition = FormStartPosition.CenterParent; Font = new Font("Segoe UI", 9F); FormBorderStyle = FormBorderStyle.FixedDialog; MaximizeBox = false; MinimizeBox = false; ClientSize = new Size(540, 230);
            var root = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(16), ColumnCount = 2, RowCount = 3 };
            root.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 145)); root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            eject.Items.AddRange(new object[] { "Never eject", "Success only", "All completed jobs" }); eject.SelectedIndex = ejectMode == "Always" ? 2 : ejectMode == "Success" ? 1 : 0;
            root.Controls.Add(new Label { Text = "Automatic eject", Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft }, 0, 0); root.Controls.Add(eject, 1, 0);
            sounds.Checked = soundsEnabled; root.Controls.Add(sounds, 1, 1);
            var buttons = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.RightToLeft, AutoSize = true, Padding = new Padding(0, 14, 0, 0) };
            var save = new Button { Text = "Save", DialogResult = DialogResult.OK, AutoSize = true }; var cancel = new Button { Text = "Cancel", DialogResult = DialogResult.Cancel, AutoSize = true };
            var testSuccess = new Button { Text = "Test Success", AutoSize = true }; var testFailure = new Button { Text = "Test Failure", AutoSize = true };
            testSuccess.Click += (s, e) => SystemSounds.Asterisk.Play(); testFailure.Click += (s, e) => SystemSounds.Hand.Play();
            buttons.Controls.Add(save); buttons.Controls.Add(cancel); buttons.Controls.Add(testFailure); buttons.Controls.Add(testSuccess); root.Controls.Add(buttons, 0, 2); root.SetColumnSpan(buttons, 2); Controls.Add(root); AcceptButton = save; CancelButton = cancel; ThemeSettings.Apply(this);
        }
    }

    internal sealed class DiagnosticsForm : Form
    {
        public DiagnosticsForm(string outputRoot, string makeMkv, FreacManager freac, IList<DriveRow> rows)
        {
            Text = "Media Nexus ARM - Diagnostics and About"; StartPosition = FormStartPosition.CenterParent; Font = new Font("Segoe UI", 9F); Size = new Size(700, 480); MinimumSize = new Size(600, 400);
            var root = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(16), RowCount = 2, ColumnCount = 1 };
            var report = new TextBox { Dock = DockStyle.Fill, Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Both, WordWrap = false };
            var lines = new List<string>();
            lines.Add("Media Nexus ARM: " + Assembly.GetExecutingAssembly().GetName().Version);
            lines.Add("MakeMKV: " + (File.Exists(makeMkv) ? FileVersion(makeMkv) + "  (" + makeMkv + ")" : "Not found"));
            lines.Add("fre:ac: " + freac.InstalledVersion);
            lines.Add("Output: " + outputRoot);
            lines.Add("Output status: " + (AppSettings.CheckOutput(outputRoot) ?? "Writable"));
            try { string rootPath = Path.GetPathRoot(Path.GetFullPath(outputRoot)); var drive = new DriveInfo(rootPath); lines.Add("Free space: " + (drive.AvailableFreeSpace / 1073741824.0).ToString("0.0") + " GiB"); } catch { lines.Add("Free space: unavailable (network paths may not report capacity)"); }
            lines.Add(""); lines.Add("Selected optical drives:");
            foreach (DriveRow row in rows.OrderBy(r => r.Letter)) lines.Add("  " + row.Letter + ":  " + row.Device + "  [" + (row.Present ? "disc present" : "empty") + "]");
            report.Lines = lines.ToArray(); root.Controls.Add(report, 0, 0);
            var close = new Button { Text = "Close", DialogResult = DialogResult.OK, AutoSize = true, Anchor = AnchorStyles.Right }; root.Controls.Add(close, 0, 1); Controls.Add(root); AcceptButton = close; CancelButton = close; ThemeSettings.Apply(this);
        }
        private static string FileVersion(string path) { try { return FileVersionInfo.GetVersionInfo(path).FileVersion ?? "Found"; } catch { return "Found"; } }
    }

    internal static class ThemeSettings
    {
        private const string RegistryPath = @"Software\DiscRipper";
        private const string ValueName = "DarkMode";
        public static bool IsDark()
        {
            try { using (var key = Registry.CurrentUser.OpenSubKey(RegistryPath)) return key != null && Convert.ToInt32(key.GetValue(ValueName, 0)) != 0; }
            catch { return false; }
        }
        public static void Save(bool dark)
        {
            using (var key = Registry.CurrentUser.CreateSubKey(RegistryPath)) key.SetValue(ValueName, dark ? 1 : 0, RegistryValueKind.DWord);
        }
        public static void Apply(Control root) { Apply(root, IsDark()); }
        public static void Apply(Control root, bool dark)
        {
            Color back = dark ? Color.FromArgb(32, 32, 32) : SystemColors.Control;
            Color surface = dark ? Color.FromArgb(45, 45, 48) : SystemColors.Window;
            Color fore = dark ? Color.White : Color.Black;
            root.BackColor = root is TextBox || root is ComboBox || root is CheckedListBox || root is DataGridView ? surface : back;
            root.ForeColor = fore;
            var button = root as Button;
            if (button != null)
            {
                button.ForeColor = fore;
                if (dark)
                {
                    button.UseVisualStyleBackColor = false;
                    button.FlatStyle = FlatStyle.Flat;
                    button.BackColor = button.Enabled ? Color.FromArgb(70, 70, 74) : Color.FromArgb(56, 56, 59);
                    button.FlatAppearance.BorderColor = Color.FromArgb(125, 125, 130);
                }
                else
                {
                    button.UseVisualStyleBackColor = true;
                    button.FlatStyle = FlatStyle.Standard;
                }
            }
            var grid = root as DataGridView;
            if (grid != null)
            {
                grid.BackgroundColor = surface; grid.GridColor = dark ? Color.FromArgb(80, 80, 80) : SystemColors.ControlDark;
                grid.DefaultCellStyle.BackColor = surface; grid.DefaultCellStyle.ForeColor = fore;
                grid.ColumnHeadersDefaultCellStyle.BackColor = dark ? Color.FromArgb(55, 55, 58) : SystemColors.Control;
                grid.ColumnHeadersDefaultCellStyle.ForeColor = fore; grid.EnableHeadersVisualStyles = !dark;
                foreach (DataGridViewRow row in grid.Rows) row.DefaultCellStyle.ForeColor = fore;
            }
            foreach (Control child in root.Controls) Apply(child, dark);
        }
    }

    internal sealed class LayoutSettings
    {
        private const string RegistryPath = @"Software\DiscRipper";
        public int WindowWidth = 1060;
        public int WindowHeight = 520;
        public int[] ColumnWidths = { 60, 280, 185, 145, 300, 150 };

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
                    for (int i = 0; i < 6; i++) settings.ColumnWidths[i] = ReadInt(key, "ColumnWidth" + i, settings.ColumnWidths[i], i == 5 ? 130 : 45, 2000);
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
            string[] names = { "Drive", "Device", "Disc", "Media Type", "Status", "Action" };
            for (int i = 0; i < 6; i++)
            {
                columns[i] = NewNumber(i == 5 ? 130 : 45, 2000); columns[i].Value = Math.Max(columns[i].Minimum, current.ColumnWidths[i]); AddSetting(grid, 4 + i, names[i], columns[i]);
            }
            var buttons = new FlowLayoutPanel { FlowDirection = FlowDirection.RightToLeft, Dock = DockStyle.Fill, AutoSize = true, Padding = new Padding(0, 10, 0, 0) };
            var save = new Button { Text = "Apply", AutoSize = true }; var cancel = new Button { Text = "Cancel", DialogResult = DialogResult.Cancel, AutoSize = true };
            var reset = new Button { Text = "Defaults", AutoSize = true, Margin = new Padding(3, 3, 18, 3) };
            save.Click += SaveClicked; reset.Click += ResetClicked; buttons.Controls.Add(save); buttons.Controls.Add(cancel); buttons.Controls.Add(reset);
            grid.Controls.Add(buttons, 0, 10); grid.SetColumnSpan(buttons, 2); Controls.Add(grid); AcceptButton = save; CancelButton = cancel; ThemeSettings.Apply(this);
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
        private const string EjectValue = "EjectMode";
        private const string SoundsValue = "CompletionSounds";
        private const string MediaTypesValue = "EnabledMediaTypes";
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
        public static string LoadEjectMode() { try { using (var key = Registry.CurrentUser.OpenSubKey(RegistryPath)) return key == null ? "Never" : Convert.ToString(key.GetValue(EjectValue, "Never")); } catch { return "Never"; } }
        public static void SaveEjectMode(string value) { using (var key = Registry.CurrentUser.CreateSubKey(RegistryPath)) key.SetValue(EjectValue, value, RegistryValueKind.String); }
        public static bool LoadSoundsEnabled() { try { using (var key = Registry.CurrentUser.OpenSubKey(RegistryPath)) return key == null || Convert.ToInt32(key.GetValue(SoundsValue, 1)) != 0; } catch { return true; } }
        public static void SaveSoundsEnabled(bool value) { using (var key = Registry.CurrentUser.CreateSubKey(RegistryPath)) key.SetValue(SoundsValue, value ? 1 : 0, RegistryValueKind.DWord); }
        public static HashSet<MediaKind> LoadEnabledMediaTypes()
        {
            var defaults = new HashSet<MediaKind> { MediaKind.Movie, MediaKind.TVSeries, MediaKind.Music, MediaKind.Book };
            try
            {
                using (var key = Registry.CurrentUser.OpenSubKey(RegistryPath))
                {
                    if (key == null) return defaults;
                    string[] values = key.GetValue(MediaTypesValue) as string[];
                    if (values == null) return defaults;
                    var result = new HashSet<MediaKind>();
                    foreach (string value in values) { MediaKind kind; if (MainFormMediaNames.TryParse(value, out kind)) result.Add(kind); }
                    return result.Count > 0 ? result : defaults;
                }
            }
            catch { return defaults; }
        }
        public static void SaveEnabledMediaTypes(IEnumerable<MediaKind> kinds)
        {
            string[] values = kinds.Distinct().Select(MainFormMediaNames.Display).ToArray();
            if (values.Length == 0) throw new ArgumentException("At least one media type must remain enabled.");
            using (var key = Registry.CurrentUser.CreateSubKey(RegistryPath)) key.SetValue(MediaTypesValue, values, RegistryValueKind.MultiString);
        }
        public static string CheckOutput(string path)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(path)) return "Output folder is not configured";
                Directory.CreateDirectory(path);
                string probe = Path.Combine(path, ".media-nexus-write-test-" + Guid.NewGuid().ToString("N")); using (File.Create(probe)) { } File.Delete(probe);
                try { string root = Path.GetPathRoot(Path.GetFullPath(path)); var drive = new DriveInfo(root); if (drive.IsReady && drive.AvailableFreeSpace < 1073741824L) return "Output has less than 1 GiB free"; } catch { }
                return null;
            }
            catch { return "Output folder is unavailable or read-only"; }
        }
        public static void CleanOldLogs(string outputRoot, int days)
        {
            try { string folder = Path.Combine(outputRoot, "Logs"); if (!Directory.Exists(folder)) return; DateTime cutoff = DateTime.Now.AddDays(-days); foreach (string file in Directory.GetFiles(folder, "*.log")) if (File.GetLastWriteTime(file) < cutoff) File.Delete(file); } catch { }
        }
        public static void ResetAll() { try { Registry.CurrentUser.DeleteSubKeyTree(RegistryPath, false); } catch { } }
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
            Controls.Add(root); AcceptButton = save; CancelButton = cancel; ThemeSettings.Apply(this);
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
            Controls.Add(root); AcceptButton = save; CancelButton = cancel; ThemeSettings.Apply(this);
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
            AppDomain.CurrentDomain.AssemblyResolve += (sender, args) =>
            {
                if (!args.Name.StartsWith("TagLibSharp", StringComparison.OrdinalIgnoreCase)) return null;
                using (Stream stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("MediaNexus.TagLibSharp.dll"))
                {
                    if (stream == null) return null;
                    byte[] bytes = new byte[stream.Length]; stream.Read(bytes, 0, bytes.Length); return Assembly.Load(bytes);
                }
            };
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
