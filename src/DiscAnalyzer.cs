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
            var titles = new Dictionary<int, VideoTitleInfo>();
            foreach (string raw in (makeMkvOutput ?? "").Split('\n'))
            {
                string line = raw.Trim();
                Match idMatch = Regex.Match(line, "^TINFO:(\\d+),(\\d+),\\d+,\\\"(.*)\\\"$");
                if (!idMatch.Success) continue;
                int id = int.Parse(idMatch.Groups[1].Value);
                int code = int.Parse(idMatch.Groups[2].Value); string value = idMatch.Groups[3].Value;
                VideoTitleInfo title;
                if (!titles.TryGetValue(id, out title)) { title = new VideoTitleInfo { Id = id }; titles[id] = title; }
                if (code == 9)
                {
                    Match duration = Regex.Match(value, @"^(\d+):([0-5]\d):([0-5]\d)$");
                    if (duration.Success) title.DurationSeconds = int.Parse(duration.Groups[1].Value) * 3600 + int.Parse(duration.Groups[2].Value) * 60 + int.Parse(duration.Groups[3].Value);
                }
                else if (code == 8) { int chapters; if (int.TryParse(value, out chapters)) title.Chapters = chapters; }
                else if (code == 11) { long size; if (long.TryParse(value, out size)) title.SizeBytes = size; }
                else if (code == 16) title.Playlist = value;
                else if (code == 26) { title.Segments.Clear(); title.Segments.AddRange(value.Split(',').Select(x => x.Trim()).Where(x => x.Length > 0)); }
                else if (code == 27) title.OutputFileName = value;
                else if (code == 2) title.Name = value;
            }
            foreach (VideoTitleInfo title in titles.Values.OrderBy(t => t.Id)) analysis.VideoTitles.Add(title);
            MarkCompositeTitles(analysis.VideoTitles);

            List<VideoTitleInfo> substantial = analysis.VideoTitles.Where(t => t.DurationSeconds >= 900).OrderBy(t => t.DurationSeconds).ToList();
            if (substantial.Count == 0)
            {
                analysis.Kind = MediaKind.Choose; analysis.Confidence = DetectionConfidence.Low; analysis.Summary = "No substantial video titles found"; return analysis;
            }

            VideoTitleInfo longest = substantial[substantial.Count - 1];
            VideoTitleInfo second = substantial.Count > 1 ? substantial[substantial.Count - 2] : null;
            List<VideoTitleInfo> episodeCluster = FindEpisodeCluster(substantial);
            int longestEpisode = episodeCluster.Count == 0 ? 0 : episodeCluster.Max(t => t.DurationSeconds);
            int combinedEpisodes = episodeCluster.Sum(t => t.DurationSeconds);
            bool probablePlayAll = episodeCluster.Count >= 2 && longest.DurationSeconds >= combinedEpisodes * 0.80 && longest.DurationSeconds <= combinedEpisodes * 1.20;
            bool dominantFeature = longest.DurationSeconds >= 4500 &&
                !probablePlayAll &&
                (second == null || longest.DurationSeconds >= second.DurationSeconds * 1.35) &&
                (longestEpisode == 0 || longest.DurationSeconds >= longestEpisode * 1.35);
            if (dominantFeature)
            {
                analysis.Kind = MediaKind.Movie;
                analysis.Confidence = DetectionConfidence.High;
                analysis.Summary = "Dominant main feature " + FormatDuration(longest.DurationSeconds);
                analysis.SelectedTitleIds.Add(longest.Id);
                return analysis;
            }

            if (episodeCluster.Count >= 2)
            {
                analysis.Kind = MediaKind.TVSeries;
                analysis.Confidence = episodeCluster.Count >= 3 ? DetectionConfidence.High : DetectionConfidence.Medium;
                analysis.Summary = episodeCluster.Count + " probable episodes";
                foreach (VideoTitleInfo title in episodeCluster.OrderBy(t => t.Id)) analysis.SelectedTitleIds.Add(title.Id);
                return analysis;
            }

            bool featureLength = longest.DurationSeconds >= 3600;
            bool dominant = second == null || longest.DurationSeconds >= second.DurationSeconds * 1.35;
            analysis.Kind = MediaKind.Movie;
            analysis.Confidence = featureLength && dominant ? DetectionConfidence.High : DetectionConfidence.Medium;
            analysis.Summary = "Main feature " + FormatDuration(longest.DurationSeconds);
            analysis.SelectedTitleIds.Add(longest.Id);
            return analysis;
        }

        public static List<VideoTitleInfo> RankMovieCandidates(IEnumerable<VideoTitleInfo> source)
        {
            List<VideoTitleInfo> candidates = source.Where(t => t.DurationSeconds >= 3600).ToList();
            MarkCompositeTitles(candidates);
            foreach (VideoTitleInfo title in candidates)
            {
                title.SelectionReason = title.Composite ? "Composite playlist; probably includes feature plus other material" :
                    candidates.Any(other => other.Id != title.Id && ContainsSegments(other, title) && other.DurationSeconds > title.DurationSeconds * 1.15) ? "Feature playlist contained in a longer composite" :
                    "Feature-length candidate";
            }
            return candidates.OrderBy(t => t.Composite).ThenByDescending(t => t.DurationSeconds >= 4500).ThenByDescending(t => t.SizeBytes).ToList();
        }

        private static void MarkCompositeTitles(IEnumerable<VideoTitleInfo> source)
        {
            List<VideoTitleInfo> titles = source.ToList();
            foreach (VideoTitleInfo outer in titles)
                outer.Composite = titles.Any(inner => inner.Id != outer.Id && inner.DurationSeconds >= 3600 && outer.DurationSeconds > inner.DurationSeconds * 1.15 && ContainsSegments(outer, inner));
        }

        private static bool ContainsSegments(VideoTitleInfo outer, VideoTitleInfo inner)
        {
            if (outer.Segments.Count == 0 || inner.Segments.Count == 0 || outer.Segments.Count <= inner.Segments.Count) return false;
            var outerSet = new HashSet<string>(outer.Segments, StringComparer.OrdinalIgnoreCase);
            return inner.Segments.All(outerSet.Contains);
        }

        public static List<int> SelectTvTitles(IEnumerable<VideoTitleInfo> titles)
        {
            List<VideoTitleInfo> all = titles.Where(t => t.DurationSeconds >= 900).OrderBy(t => t.DurationSeconds).ToList();
            List<VideoTitleInfo> cluster = FindEpisodeCluster(all).OrderBy(t => t.Id).ToList();
            var seenSegments = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var selected = new List<int>();
            foreach (VideoTitleInfo title in cluster)
            {
                string signature = title.Segments.Count == 0 ? "title:" + title.Id : string.Join(",", title.Segments.ToArray());
                if (!seenSegments.Add(signature)) { title.SelectionReason = "Duplicate segment map"; continue; }
                title.SelectionReason = "Probable individual episode"; selected.Add(title.Id);
            }
            int combined = cluster.Sum(t => t.DurationSeconds);
            foreach (VideoTitleInfo title in all.Where(t => !selected.Contains(t.Id) && string.IsNullOrWhiteSpace(t.SelectionReason)))
            {
                if (combined > 0 && title.DurationSeconds >= combined * 0.80 && title.DurationSeconds <= combined * 1.20) title.SelectionReason = "Probable Play All playlist";
                else if (title.DurationSeconds < 1200) title.SelectionReason = "Probable extra / short feature";
                else title.SelectionReason = "Outside episode-duration cluster";
            }
            return selected;
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
