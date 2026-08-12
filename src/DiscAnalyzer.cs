using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace DiscRipper
{
    internal static class DiscAnalyzer
    {
        public static DiscAnalysis AnalyzeAudio(DiscToc toc)
        {
            return new DiscAnalysis
            {
                Kind = MediaKind.Music,
                Confidence = DetectionConfidence.High,
                Summary = "Audio CD",
                AudioToc = toc
            };
        }

        public static DiscAnalysis AnalyzeVideo(string makeMkvOutput)
        {
            var analysis = new DiscAnalysis();
            var durations = new Dictionary<int, int>();
            foreach (string raw in (makeMkvOutput ?? "").Split('\n'))
            {
                string line = raw.Trim();
                Match idMatch = Regex.Match(line, @"^TINFO:(\d+),");
                if (!idMatch.Success) continue;
                int id = int.Parse(idMatch.Groups[1].Value);
                Match durationMatch = Regex.Match(line, @"(\d+):([0-5]\d):([0-5]\d)");
                if (!durationMatch.Success) continue;
                int seconds = int.Parse(durationMatch.Groups[1].Value) * 3600 + int.Parse(durationMatch.Groups[2].Value) * 60 + int.Parse(durationMatch.Groups[3].Value);
                if (!durations.ContainsKey(id) || seconds > durations[id]) durations[id] = seconds;
            }
            foreach (var pair in durations.OrderBy(p => p.Key)) analysis.VideoTitles.Add(new VideoTitleInfo { Id = pair.Key, DurationSeconds = pair.Value });

            List<VideoTitleInfo> substantial = analysis.VideoTitles.Where(t => t.DurationSeconds >= 900).OrderBy(t => t.DurationSeconds).ToList();
            if (substantial.Count == 0)
            {
                analysis.Kind = MediaKind.Choose; analysis.Confidence = DetectionConfidence.Low; analysis.Summary = "No substantial video titles found"; return analysis;
            }

            List<VideoTitleInfo> episodeCluster = FindEpisodeCluster(substantial);
            if (episodeCluster.Count >= 2)
            {
                analysis.Kind = MediaKind.TVSeries;
                analysis.Confidence = episodeCluster.Count >= 3 ? DetectionConfidence.High : DetectionConfidence.Medium;
                analysis.Summary = episodeCluster.Count + " probable episodes";
                foreach (VideoTitleInfo title in episodeCluster.OrderBy(t => t.Id)) analysis.SelectedTitleIds.Add(title.Id);
                return analysis;
            }

            VideoTitleInfo longest = substantial[substantial.Count - 1];
            VideoTitleInfo second = substantial.Count > 1 ? substantial[substantial.Count - 2] : null;
            bool featureLength = longest.DurationSeconds >= 3600;
            bool dominant = second == null || longest.DurationSeconds >= second.DurationSeconds * 1.35;
            analysis.Kind = MediaKind.Movie;
            analysis.Confidence = featureLength && dominant ? DetectionConfidence.High : DetectionConfidence.Medium;
            analysis.Summary = "Main feature " + FormatDuration(longest.DurationSeconds);
            analysis.SelectedTitleIds.Add(longest.Id);
            return analysis;
        }

        public static List<int> SelectTvTitles(IEnumerable<VideoTitleInfo> titles)
        {
            return FindEpisodeCluster(titles.Where(t => t.DurationSeconds >= 900).OrderBy(t => t.DurationSeconds).ToList()).OrderBy(t => t.Id).Select(t => t.Id).ToList();
        }

        private static List<VideoTitleInfo> FindEpisodeCluster(List<VideoTitleInfo> titles)
        {
            var best = new List<VideoTitleInfo>();
            foreach (VideoTitleInfo seed in titles)
            {
                if (seed.DurationSeconds < 900 || seed.DurationSeconds > 5400) continue;
                int tolerance = Math.Max(180, (int)(seed.DurationSeconds * 0.15));
                List<VideoTitleInfo> cluster = titles.Where(t => t.DurationSeconds >= 900 && t.DurationSeconds <= 5400 && Math.Abs(t.DurationSeconds - seed.DurationSeconds) <= tolerance).ToList();
                if (cluster.Count > best.Count || (cluster.Count == best.Count && Spread(cluster) < Spread(best))) best = cluster;
            }
            if (best.Count < 2) return new List<VideoTitleInfo>();

            int combined = best.Sum(t => t.DurationSeconds);
            return best.Where(t => t.DurationSeconds < combined * 0.80).ToList();
        }

        private static int Spread(List<VideoTitleInfo> titles)
        {
            return titles.Count == 0 ? int.MaxValue : titles.Max(t => t.DurationSeconds) - titles.Min(t => t.DurationSeconds);
        }

        private static string FormatDuration(int seconds)
        {
            TimeSpan value = TimeSpan.FromSeconds(seconds);
            return string.Format("{0}:{1:00}:{2:00}", (int)value.TotalHours, value.Minutes, value.Seconds);
        }
    }
}
