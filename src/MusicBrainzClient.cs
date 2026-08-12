using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;

namespace DiscRipper
{
    internal sealed class MusicBrainzClient
    {
        private const string UserAgent = "Media-Nexus-ARM/0.2.7 (https://github.com/SonofVlad/Media-Nexus-ARM)";
        private static readonly SemaphoreSlim RequestGate = new SemaphoreSlim(1, 1);
        private static DateTime lastRequest = DateTime.MinValue;

        public async Task<List<MusicRelease>> LookupDiscAsync(DiscToc toc, CancellationToken token)
        {
            string url = "https://musicbrainz.org/ws/2/discid/" + Uri.EscapeDataString(toc.DiscId) +
                "?fmt=json&cdstubs=no&toc=" + toc.TocQuery +
                "&inc=artist-credits+recordings+release-groups+labels+isrcs+genres";
            string json;
            try { json = await GetStringAsync(url, token); }
            catch (WebException ex)
            {
                var response = ex.Response as HttpWebResponse;
                if (response != null && response.StatusCode == HttpStatusCode.NotFound) return new List<MusicRelease>();
                throw;
            }
            var serializer = new JavaScriptSerializer { MaxJsonLength = 16 * 1024 * 1024 };
            var root = serializer.DeserializeObject(json) as Dictionary<string, object>;
            var releases = new List<MusicRelease>();
            foreach (Dictionary<string, object> releaseData in Objects(root, "releases"))
            {
                MusicRelease release = ParseRelease(releaseData, toc.DiscId);
                if (release != null && release.Tracks.Count == toc.TrackOffsets.Count) releases.Add(release);
            }
            return releases;
        }

        public async Task<byte[]> TryDownloadCoverAsync(string releaseId, CancellationToken token)
        {
            try { return await GetBytesAsync("https://coverartarchive.org/release/" + Uri.EscapeDataString(releaseId) + "/front-500", token, false); }
            catch { return null; }
        }

        private static MusicRelease ParseRelease(Dictionary<string, object> data, string discId)
        {
            var result = new MusicRelease
            {
                Id = Text(data, "id"), Title = Text(data, "title"), Date = Text(data, "date"), Country = Text(data, "country"), Barcode = Text(data, "barcode")
            };
            result.Artist = ArtistCredit(data);
            result.AlbumArtist = result.Artist;
            Dictionary<string, object> group = Object(data, "release-group");
            result.ReleaseGroupId = Text(group, "id");
            Dictionary<string, object> labelInfo = Objects(data, "label-info").FirstOrDefault();
            if (labelInfo != null)
            {
                result.CatalogNumber = Text(labelInfo, "catalog-number");
                result.Label = Text(Object(labelInfo, "label"), "name");
            }
            List<Dictionary<string, object>> media = Objects(data, "media").ToList();
            result.MediumCount = media.Count;
            Dictionary<string, object> medium = media.FirstOrDefault(m => Objects(m, "discs").Any(d => string.Equals(Text(d, "id"), discId, StringComparison.OrdinalIgnoreCase)));
            if (medium == null && media.Count == 1) medium = media[0];
            if (medium == null) return null;
            result.MediumPosition = Number(medium, "position", 1);
            foreach (Dictionary<string, object> trackData in Objects(medium, "tracks"))
            {
                Dictionary<string, object> recording = Object(trackData, "recording");
                var track = new MusicTrack
                {
                    Number = Number(trackData, "position", result.Tracks.Count + 1),
                    Title = Text(trackData, "title"),
                    Artist = ArtistCredit(trackData),
                    RecordingId = Text(recording, "id")
                };
                if (string.IsNullOrWhiteSpace(track.Artist)) track.Artist = ArtistCredit(recording);
                object[] isrcs = Array(recording, "isrcs");
                if (isrcs.Length > 0) track.Isrc = Convert.ToString(isrcs[0]);
                result.Tracks.Add(track);
            }
            return result;
        }

        private static async Task<string> GetStringAsync(string url, CancellationToken token)
        {
            byte[] bytes = await GetBytesAsync(url, token, true);
            return Encoding.UTF8.GetString(bytes);
        }

        private static async Task<byte[]> GetBytesAsync(string url, CancellationToken token, bool rateLimited)
        {
            if (rateLimited)
            {
                await RequestGate.WaitAsync(token);
                try
                {
                    TimeSpan wait = TimeSpan.FromMilliseconds(1100) - (DateTime.UtcNow - lastRequest);
                    if (wait > TimeSpan.Zero) await Task.Delay(wait, token);
                    byte[] result = await DownloadAsync(url, token);
                    lastRequest = DateTime.UtcNow;
                    return result;
                }
                finally { RequestGate.Release(); }
            }
            return await DownloadAsync(url, token);
        }

        private static Task<byte[]> DownloadAsync(string url, CancellationToken token)
        {
            return Task.Run(() =>
            {
                token.ThrowIfCancellationRequested();
                ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;
                var request = (HttpWebRequest)WebRequest.Create(url);
                request.UserAgent = UserAgent; request.Accept = "application/json,image/*"; request.AllowAutoRedirect = true; request.Timeout = 30000;
                using (token.Register(() => request.Abort()))
                using (var response = (HttpWebResponse)request.GetResponse())
                using (Stream stream = response.GetResponseStream())
                using (var memory = new MemoryStream()) { stream.CopyTo(memory); return memory.ToArray(); }
            }, token);
        }

        private static string ArtistCredit(Dictionary<string, object> data)
        {
            var text = new StringBuilder();
            foreach (Dictionary<string, object> credit in Objects(data, "artist-credit"))
            {
                string name = Text(credit, "name");
                if (string.IsNullOrWhiteSpace(name)) name = Text(Object(credit, "artist"), "name");
                text.Append(name); text.Append(Text(credit, "joinphrase"));
            }
            return text.ToString();
        }

        private static IEnumerable<Dictionary<string, object>> Objects(Dictionary<string, object> data, string key)
        {
            foreach (object item in Array(data, key)) { var value = item as Dictionary<string, object>; if (value != null) yield return value; }
        }
        private static object[] Array(Dictionary<string, object> data, string key) { object value; return data != null && data.TryGetValue(key, out value) ? (value as object[] ?? new object[0]) : new object[0]; }
        private static Dictionary<string, object> Object(Dictionary<string, object> data, string key) { object value; return data != null && data.TryGetValue(key, out value) ? value as Dictionary<string, object> : null; }
        private static string Text(Dictionary<string, object> data, string key) { object value; return data != null && data.TryGetValue(key, out value) && value != null ? Convert.ToString(value) : ""; }
        private static int Number(Dictionary<string, object> data, string key, int fallback) { int result; return int.TryParse(Text(data, key), out result) ? result : fallback; }
    }
}
