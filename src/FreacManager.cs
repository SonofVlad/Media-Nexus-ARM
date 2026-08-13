using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;

namespace DiscRipper
{
    internal sealed class FreacManager
    {
        private const string StableVersion = "1.1.7";
        private const string StableUrl = "https://github.com/enzo1982/freac/releases/download/v1.1.7/freac-1.1.7-windows-x64.zip";
        private const string StableSha256 = "EF45665AAE6C1C0EB4C0ECD8ECC6BED24F02F3CDDF6CFD72D8E5C9BC858BF110";
        private static readonly SemaphoreSlim InstallGate = new SemaphoreSlim(1, 1);
        private readonly string root;
        public FreacManager() { root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Media Nexus ARM", "Data", "Tools", "freac"); }
        public string ExecutablePath { get { return Path.Combine(root, "current", "freaccmd.exe"); } }
        public string InstalledVersion { get { string file = Path.Combine(root, "current", "version.txt"); return File.Exists(file) ? File.ReadAllText(file).Trim() : "Not installed"; } }
        public string LatestStableVersion { get { return StableVersion; } }

        public async Task EnsureInstalledAsync(Action<string> status, CancellationToken token)
        {
            await InstallGate.WaitAsync(token);
            try
            {
            if (File.Exists(ExecutablePath) && await ValidateAsync(ExecutablePath, token)) return;
            if (status != null) status("Installing managed fre:ac " + StableVersion + "...");
            string temp = Path.Combine(root, "install-" + Guid.NewGuid().ToString("N"));
            string zip = Path.Combine(temp, "freac.zip"), expanded = Path.Combine(temp, "expanded");
            Directory.CreateDirectory(temp);
            try
            {
                await DownloadAsync(StableUrl, zip, token);
                if (!string.Equals(Hashing.Sha256File(zip), StableSha256, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("The fre:ac package checksum did not match the official package expected by this build.");
                Directory.CreateDirectory(expanded); SafeExtract(zip, expanded);
                string exe = Directory.GetFiles(expanded, "freaccmd.exe", SearchOption.AllDirectories).FirstOrDefault();
                if (exe == null || !await ValidateAsync(exe, token)) throw new InvalidDataException("The downloaded fre:ac command-line engine did not start correctly.");
                string packageRoot = Path.GetDirectoryName(exe), next = Path.Combine(root, "next"), current = Path.Combine(root, "current"), previous = Path.Combine(root, "previous");
                DeleteDirectory(next); CopyDirectory(packageRoot, next); File.WriteAllText(Path.Combine(next, "version.txt"), StableVersion);
                DeleteDirectory(previous); if (Directory.Exists(current)) Directory.Move(current, previous); Directory.Move(next, current);
                if (!await ValidateAsync(ExecutablePath, token))
                {
                    DeleteDirectory(current); if (Directory.Exists(previous)) Directory.Move(previous, current);
                    throw new InvalidDataException("fre:ac validation failed after installation; the previous version was restored.");
                }
            }
            finally { DeleteDirectory(temp); }
            }
            finally { InstallGate.Release(); }
        }

        public async Task<FreacRipResult> RipAudioAsync(string driveLetter, DiscToc toc, string destination, string coverPath, AudioFormat format, Action<int> progress, CancellationToken token)
        {
            Directory.CreateDirectory(destination);
            int driveIndex = GetFreacDriveIndex(driveLetter);
            if (driveIndex < 0) throw new InvalidOperationException("Could not map drive " + driveLetter + ": to a fre:ac device.");
            string encoder = format == AudioFormat.FLAC ? "flac" : format == AudioFormat.MP3 ? "lame" : "coreaudio";
            string extension = format == AudioFormat.FLAC ? ".flac" : format == AudioFormat.MP3 ? ".mp3" : ".m4a";
            var args = new List<string> { "--drive=" + driveIndex, "--track=all", "--encoder=" + encoder, "-d", Quote(destination), "--pattern=<track>" };
            if (!string.IsNullOrWhiteSpace(coverPath) && File.Exists(coverPath)) args.Add("--add-cover=" + Quote(coverPath));
            if (format == AudioFormat.ALAC) { args.Add("--"); args.Add("-f"); args.Add("ALAC"); }
            int completed = 0, lastProgress = -1;
            int totalFrames = Math.Max(1, toc.LeadoutOffset - toc.TrackOffsets[0]);
            Action<int> reportTrackPercent = trackPercent =>
            {
                int track = Math.Min(completed, toc.TrackOffsets.Count - 1);
                int start = toc.TrackOffsets[track];
                int end = track + 1 < toc.TrackOffsets.Count ? toc.TrackOffsets[track + 1] : toc.LeadoutOffset;
                long framesDone = (long)(start - toc.TrackOffsets[0]) + ((long)Math.Max(0, end - start) * Math.Max(0, Math.Min(100, trackPercent)) / 100);
                int value = Math.Min(99, (int)(framesDone * 100 / totalFrames));
                if (value > lastProgress) { lastProgress = value; if (progress != null) progress(value); }
            };
            ProcessTextResult run = await ProcessText.RunAsync(ExecutablePath, string.Join(" ", args.ToArray()), token, line =>
            {
                Match percent = Regex.Match(line, @"(?<!\d)(\d{1,3})(?:\.\d+)?\s*%");
                int livePercent;
                if (percent.Success && int.TryParse(percent.Groups[1].Value, out livePercent) && livePercent <= 100) reportTrackPercent(livePercent);
                if (line.IndexOf("done.", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    reportTrackPercent(100);
                    completed++;
                }
            });
            string[] files = Directory.GetFiles(destination, "*" + extension, SearchOption.TopDirectoryOnly).OrderBy(NaturalTrackOrder).ToArray();
            return new FreacRipResult { ExitCode = run.ExitCode, Output = run.Output, Files = files, Success = run.ExitCode == 0 && files.Length == toc.TrackOffsets.Count };
        }

        private static string NaturalTrackOrder(string path)
        {
            Match match = Regex.Match(Path.GetFileNameWithoutExtension(path), @"\d+");
            int number; return match.Success && int.TryParse(match.Value, out number) ? number.ToString("D4") : path;
        }

        private static int GetFreacDriveIndex(string driveLetter)
        {
            List<string> letters = DriveSettings.DiscoverOpticalDrives().Select(d => d.Letter).OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToList();
            return letters.FindIndex(x => string.Equals(x, driveLetter.TrimEnd(':'), StringComparison.OrdinalIgnoreCase));
        }

        private static async Task<bool> ValidateAsync(string exe, CancellationToken token)
        {
            try { ProcessTextResult result = await ProcessText.RunAsync(exe, "--list-drives", token, null); return result.Output.IndexOf("fre:ac", StringComparison.OrdinalIgnoreCase) >= 0 && result.Output.IndexOf("Available CD drives", StringComparison.OrdinalIgnoreCase) >= 0; }
            catch { return false; }
        }

        private static Task DownloadAsync(string url, string target, CancellationToken token)
        {
            return Task.Run(() =>
            {
                ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;
                var request = (HttpWebRequest)WebRequest.Create(url); request.UserAgent = "Media-Nexus-ARM/0.7.13"; request.AllowAutoRedirect = true;
                using (token.Register(() => request.Abort())) using (var response = request.GetResponse()) using (Stream input = response.GetResponseStream()) using (FileStream output = File.Create(target)) input.CopyTo(output);
            }, token);
        }

        private static void SafeExtract(string zipPath, string destination)
        {
            string root = Path.GetFullPath(destination) + Path.DirectorySeparatorChar;
            using (ZipArchive archive = ZipFile.OpenRead(zipPath)) foreach (ZipArchiveEntry entry in archive.Entries)
            {
                string target = Path.GetFullPath(Path.Combine(destination, entry.FullName));
                if (!target.StartsWith(root, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Unsafe path in fre:ac package.");
                if (string.IsNullOrEmpty(entry.Name)) Directory.CreateDirectory(target);
                else { Directory.CreateDirectory(Path.GetDirectoryName(target)); entry.ExtractToFile(target, true); }
            }
        }
        private static void CopyDirectory(string source, string destination) { foreach (string directory in Directory.GetDirectories(source, "*", SearchOption.AllDirectories)) Directory.CreateDirectory(directory.Replace(source, destination)); Directory.CreateDirectory(destination); foreach (string file in Directory.GetFiles(source, "*", SearchOption.AllDirectories)) File.Copy(file, file.Replace(source, destination), true); }
        private static void DeleteDirectory(string path) { try { if (Directory.Exists(path)) Directory.Delete(path, true); } catch { } }
        private static string Quote(string value) { return "\"" + value.Replace("\"", "\\\"") + "\""; }
    }

    internal sealed class FreacRipResult { public bool Success; public int ExitCode; public string Output; public string[] Files; }

    internal static class Hashing
    {
        public static string Sha256File(string path) { using (var sha = System.Security.Cryptography.SHA256.Create()) using (FileStream stream = File.OpenRead(path)) return BitConverter.ToString(sha.ComputeHash(stream)).Replace("-", ""); }
    }

    internal sealed class ProcessTextResult { public int ExitCode; public string Output; }
    internal static class ProcessText
    {
        public static async Task<ProcessTextResult> RunAsync(string file, string arguments, CancellationToken token, Action<string> lineHandler)
        {
            var output = new StringBuilder();
            using (var process = new Process())
            {
                process.StartInfo = new ProcessStartInfo(file, arguments) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
                Action<string> handle = line => { if (line == null) return; lock (output) output.AppendLine(line); if (lineHandler != null) lineHandler(line); };
                process.Start();
                Task stdout = PumpAsync(process.StandardOutput, handle);
                Task stderr = PumpAsync(process.StandardError, handle);
                using (token.Register(() => { try { if (!process.HasExited) process.Kill(); } catch { } })) await Task.Run(() => process.WaitForExit(), token);
                await Task.WhenAll(stdout, stderr);
                return new ProcessTextResult { ExitCode = process.ExitCode, Output = output.ToString() };
            }
        }

        private static async Task PumpAsync(StreamReader reader, Action<string> handler)
        {
            var text = new StringBuilder();
            var buffer = new char[256];
            int count;
            while ((count = await reader.ReadAsync(buffer, 0, buffer.Length)) > 0)
            {
                for (int i = 0; i < count; i++)
                {
                    char value = buffer[i];
                    if (value == '\r' || value == '\n')
                    {
                        if (text.Length > 0) { handler(text.ToString()); text.Clear(); }
                    }
                    else text.Append(value);
                }
            }
            if (text.Length > 0) handler(text.ToString());
        }
    }
}
