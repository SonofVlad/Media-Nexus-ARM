using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;

namespace DiscRipper
{
    internal static class MusicOrganizer
    {
        public static string TagAndOrganize(string[] rippedFiles, MusicRelease release, DiscToc toc, byte[] cover, string outputRoot, Action<string> log)
        {
            if (release == null || release.Tracks.Count != rippedFiles.Length) throw new InvalidOperationException("The selected MusicBrainz track list does not match the ripped CD.");
            string artist = SafeName(string.IsNullOrWhiteSpace(release.AlbumArtist) ? release.Artist : release.AlbumArtist);
            int year = ParseYear(release.Date);
            string album = SafeName(release.Title + (year > 0 ? " (" + year + ")" : ""));
            string albumFolder = Path.Combine(outputRoot, "Music", artist, album);
            Directory.CreateDirectory(albumFolder);
            if (cover != null && cover.Length > 0) File.WriteAllBytes(Path.Combine(albumFolder, "cover.jpg"), cover);

            var completed = new List<string>();
            for (int i = 0; i < rippedFiles.Length; i++)
            {
                MusicTrack track = release.Tracks[i];
                string extension = Path.GetExtension(rippedFiles[i]);
                string fileName = string.Format("{0:00} - {1}{2}", track.Number, SafeName(track.Title), extension);
                string target = UniquePath(Path.Combine(albumFolder, fileName));
                string partial = Path.Combine(Path.GetDirectoryName(target), Path.GetFileNameWithoutExtension(target) + ".partial" + extension);
                File.Copy(rippedFiles[i], partial, false);
                if (!File.Exists(partial) || new FileInfo(partial).Length == 0) throw new IOException("Failed to verify copied audio track " + fileName);
                WriteTags(partial, release, track, toc, year, cover);
                File.Move(partial, target);
                completed.Add(target);
                if (log != null) log(Path.GetFileName(rippedFiles[i]) + " -> " + target);
            }
            return albumFolder;
        }

        public static string MoveToPending(string[] rippedFiles, DiscToc toc, string outputRoot, Action<string> log)
        {
            string folder = Path.Combine(outputRoot, "Pending Metadata", "Disc_" + DateTime.Now.ToString("yyyy-MM-dd_HH-mm-ss") + "_" + toc.DiscId.Replace('.', '_'));
            Directory.CreateDirectory(folder);
            for (int i = 0; i < rippedFiles.Length; i++)
            {
                string target = Path.Combine(folder, string.Format("{0:00}{1}", i + 1, Path.GetExtension(rippedFiles[i])));
                File.Copy(rippedFiles[i], target, false);
                if (!File.Exists(target) || new FileInfo(target).Length == 0) throw new IOException("Failed to preserve pending audio track.");
                if (log != null) log(Path.GetFileName(rippedFiles[i]) + " -> " + target);
            }
            File.WriteAllText(Path.Combine(folder, "metadata-pending.txt"), "MusicBrainz Disc ID: " + toc.DiscId + Environment.NewLine + "TOC: " + toc.TocQuery + Environment.NewLine);
            return folder;
        }

        private static void WriteTags(string path, MusicRelease release, MusicTrack track, DiscToc toc, int year, byte[] cover)
        {
            using (TagLib.File file = TagLib.File.Create(path))
            {
                TagLib.Tag tag = file.Tag;
                tag.Title = track.Title;
                tag.Performers = new[] { string.IsNullOrWhiteSpace(track.Artist) ? release.Artist : track.Artist };
                tag.AlbumArtists = new[] { release.AlbumArtist };
                tag.Album = release.Title;
                tag.Year = (uint)Math.Max(0, year);
                tag.Track = (uint)track.Number;
                tag.TrackCount = (uint)release.Tracks.Count;
                tag.Disc = (uint)Math.Max(1, release.MediumPosition);
                tag.DiscCount = (uint)Math.Max(1, release.MediumCount);
                tag.MusicBrainzTrackId = track.RecordingId;
                tag.MusicBrainzReleaseId = release.Id;
                tag.MusicBrainzReleaseGroupId = release.ReleaseGroupId;
                tag.MusicBrainzDiscId = toc.DiscId;
                if (!string.IsNullOrWhiteSpace(track.Isrc)) tag.ISRC = track.Isrc;
                if (!string.IsNullOrWhiteSpace(track.Composer)) tag.Composers = new[] { track.Composer };
                if (cover != null && cover.Length > 0) tag.Pictures = new TagLib.IPicture[] { new TagLib.Picture(new TagLib.ByteVector(cover)) { Type = TagLib.PictureType.FrontCover, Description = "Cover" } };
                file.Save();
            }
        }

        private static int ParseYear(string date) { int year; return !string.IsNullOrWhiteSpace(date) && date.Length >= 4 && int.TryParse(date.Substring(0, 4), NumberStyles.None, CultureInfo.InvariantCulture, out year) ? year : 0; }
        public static string SafeName(string value)
        {
            if (string.IsNullOrWhiteSpace(value)) return "Unknown";
            foreach (char c in Path.GetInvalidFileNameChars()) value = value.Replace(c, '_');
            value = value.Trim().TrimEnd('.');
            string[] reserved = { "CON", "PRN", "AUX", "NUL", "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9", "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9" };
            if (reserved.Contains(value.ToUpperInvariant())) value += "_";
            return value.Length > 120 ? value.Substring(0, 120).TrimEnd() : value;
        }
        private static string UniquePath(string path) { if (!File.Exists(path)) return path; string directory = Path.GetDirectoryName(path), stem = Path.GetFileNameWithoutExtension(path), extension = Path.GetExtension(path); for (int i = 2; i < 1000; i++) { string candidate = Path.Combine(directory, stem + " (" + i + ")" + extension); if (!File.Exists(candidate)) return candidate; } return Path.Combine(directory, stem + " " + Guid.NewGuid().ToString("N") + extension); }
    }

    internal sealed class JobLog : IDisposable
    {
        private readonly StreamWriter writer;
        public string PathName { get; private set; }
        public JobLog(string outputRoot, string drive, string kind)
        {
            string folder = Path.Combine(outputRoot, "Logs"); Directory.CreateDirectory(folder);
            PathName = Path.Combine(folder, DateTime.Now.ToString("yyyy-MM-dd_HH-mm-ss") + "_" + drive + "_" + kind + ".log");
            writer = new StreamWriter(PathName, false, new System.Text.UTF8Encoding(false)) { AutoFlush = true };
        }
        public void Write(string message) { lock (writer) writer.WriteLine(DateTime.Now.ToString("O") + "  " + message); }
        public void Dispose() { writer.Dispose(); }
    }
}
