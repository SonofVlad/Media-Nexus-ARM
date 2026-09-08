using System;
using System.IO;
using System.Web.Script.Serialization;

namespace DiscRipper
{
    internal sealed class VideoScanCacheEntry
    {
        public string Fingerprint { get; set; }
        public string MakeMkvVersion { get; set; }
        public int MinimumSeconds { get; set; }
        public DateTime SavedUtc { get; set; }
        public string Output { get; set; }
    }

    internal static class VideoScanCache
    {
        private static string CacheRoot { get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Media Nexus", "ARM", "Cache", "VideoScans"); } }

        public static bool TryLoad(string fingerprint, string makeMkvVersion, int minimumSeconds, out string output)
        {
            output = null;
            if (string.IsNullOrWhiteSpace(fingerprint)) return false;
            try
            {
                string path = Path.Combine(CacheRoot, fingerprint + ".json");
                if (!File.Exists(path)) return false;
                var serializer = new JavaScriptSerializer { MaxJsonLength = int.MaxValue };
                var entry = serializer.Deserialize<VideoScanCacheEntry>(File.ReadAllText(path));
                if (entry == null || entry.Fingerprint != fingerprint || entry.MakeMkvVersion != makeMkvVersion || entry.MinimumSeconds > minimumSeconds || entry.SavedUtc < DateTime.UtcNow.AddDays(-30) || string.IsNullOrWhiteSpace(entry.Output) || entry.Output.IndexOf("TINFO:", StringComparison.OrdinalIgnoreCase) < 0) return false;
                output = entry.Output;
                return true;
            }
            catch { return false; }
        }

        public static void Save(string fingerprint, string makeMkvVersion, int minimumSeconds, string output)
        {
            if (string.IsNullOrWhiteSpace(fingerprint) || string.IsNullOrWhiteSpace(output) || output.IndexOf("TINFO:", StringComparison.OrdinalIgnoreCase) < 0) return;
            try
            {
                Directory.CreateDirectory(CacheRoot);
                string path = Path.Combine(CacheRoot, fingerprint + ".json");
                string temporary = path + ".new";
                var entry = new VideoScanCacheEntry { Fingerprint = fingerprint, MakeMkvVersion = makeMkvVersion, MinimumSeconds = minimumSeconds, SavedUtc = DateTime.UtcNow, Output = output };
                var serializer = new JavaScriptSerializer { MaxJsonLength = int.MaxValue };
                File.WriteAllText(temporary, serializer.Serialize(entry));
                if (File.Exists(path)) File.Delete(path);
                File.Move(temporary, path);
            }
            catch { }
        }
    }
}
