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
    internal sealed class TvMazeLookup
    {
        public string ShowName;
        public int PremieredYear;
        public string ImdbId;
        public readonly List<string> EpisodeNames = new List<string>();
    }

    internal static class TvMazeClient
    {
        public static Task<TvMazeLookup> LookupAsync(string show, int season, int firstEpisode, int count, CancellationToken token)
        {
            return Task.Run(() =>
            {
                ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;
                string url = "https://api.tvmaze.com/singlesearch/shows?q=" + Uri.EscapeDataString(show) + "&embed=episodes";
                var request = (HttpWebRequest)WebRequest.Create(url); request.UserAgent = "Media-Nexus-ARM/0.3.0 (https://github.com/SonofVlad/Media-Nexus-ARM)"; request.Accept = "application/json"; request.Timeout = 30000;
                string json; using (token.Register(() => request.Abort())) using (var response = request.GetResponse()) using (var reader = new StreamReader(response.GetResponseStream(), Encoding.UTF8)) json = reader.ReadToEnd();
                var serializer = new JavaScriptSerializer { MaxJsonLength = 16 * 1024 * 1024 }; var root = serializer.DeserializeObject(json) as Dictionary<string, object>;
                if (root == null) return null;
                var result = new TvMazeLookup { ShowName = Text(root, "name") };
                string premiered = Text(root, "premiered"); int year; if (premiered.Length >= 4 && int.TryParse(premiered.Substring(0, 4), out year)) result.PremieredYear = year;
                Dictionary<string, object> externals = Object(root, "externals"); result.ImdbId = Text(externals, "imdb");
                Dictionary<string, object> embedded = Object(root, "_embedded");
                foreach (Dictionary<string, object> episode in Objects(embedded, "episodes").Where(e => Number(e, "season") == season && Number(e, "number") >= firstEpisode && Number(e, "number") < firstEpisode + count).OrderBy(e => Number(e, "number"))) result.EpisodeNames.Add(Text(episode, "name"));
                return result;
            }, token);
        }
        private static IEnumerable<Dictionary<string, object>> Objects(Dictionary<string, object> data, string key) { object value; if (data == null || !data.TryGetValue(key, out value)) yield break; object[] array = value as object[]; if (array == null) yield break; foreach (object item in array) { var obj = item as Dictionary<string, object>; if (obj != null) yield return obj; } }
        private static Dictionary<string, object> Object(Dictionary<string, object> data, string key) { object value; return data != null && data.TryGetValue(key, out value) ? value as Dictionary<string, object> : null; }
        private static string Text(Dictionary<string, object> data, string key) { object value; return data != null && data.TryGetValue(key, out value) && value != null ? Convert.ToString(value) : ""; }
        private static int Number(Dictionary<string, object> data, string key) { int n; return int.TryParse(Text(data, key), out n) ? n : -1; }
    }
}
