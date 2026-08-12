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

        public static bool TrySelectHighConfidenceMovie(IEnumerable<VideoTitleInfo> source, out int titleId, out string reason)
        {
            titleId = -1; reason = "";
            List<VideoTitleInfo> all = source.Where(t => t.DurationSeconds >= 600).ToList();
            MarkCompositeTitles(all);
            List<VideoTitleInfo> features = all.Where(t => t.DurationSeconds >= 3600 && !t.Composite)
                .OrderByDescending(t => t.SizeBytes).ThenByDescending(t => t.DurationSeconds).ToList();
            if (features.Count == 0) return false;

            VideoTitleInfo best = features[0];
            if (all.Count == 1)
            {
                titleId = best.Id; reason = "only substantial title on the disc"; return true;
            }

            List<VideoTitleInfo> competitors = features.Skip(1).ToList();
            bool nearDuplicate = competitors.Any(t =>
                Math.Abs(t.DurationSeconds - best.DurationSeconds) <= Math.Max(300, best.DurationSeconds * 0.10) &&
                (best.SizeBytes <= 0 || t.SizeBytes <= 0 || t.SizeBytes >= best.SizeBytes * 0.70));
            if (nearDuplicate) return false;

            long largestOtherSize = all.Where(t => t.Id != best.Id).Select(t => t.SizeBytes).DefaultIfEmpty(0).Max();
            int longestOtherRuntime = all.Where(t => t.Id != best.Id).Select(t => t.DurationSeconds).DefaultIfEmpty(0).Max();
            const long OneGiB = 1073741824L;
            bool clearSizeWinner = best.SizeBytes >= 3L * OneGiB && largestOtherSize > 0 && largestOtherSize < OneGiB && best.SizeBytes >= largestOtherSize * 3;
            bool clearFeatureWinner = best.DurationSeconds >= 4500 &&
                (longestOtherRuntime == 0 || best.DurationSeconds >= longestOtherRuntime * 1.50) &&
                (largestOtherSize == 0 || best.SizeBytes == 0 || best.SizeBytes >= largestOtherSize * 1.75);
            if (!clearSizeWinner && !clearFeatureWinner) return false;

            titleId = best.Id;
            reason = clearSizeWinner ? "one feature-sized title and all other titles are under 1 GiB" : "one title is dominant by both runtime and size";
            return true;
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

        public static List<int> SelectTvTitles(IEnumerable<VideoTitleInfo> titles, int expectedCount = 0)
        {
            List<VideoTitleInfo> all = titles.Where(t => t.DurationSeconds >= 600).ToList();
            foreach (VideoTitleInfo title in all) title.SelectionReason = null;
            List<VideoTitleInfo> episodeRange = all.Where(t => t.DurationSeconds >= 900 && t.DurationSeconds <= 5400).ToList();
            List<VideoTitleInfo> cluster = FindEpisodeCluster(episodeRange);
            if (cluster.Count == 0) return new List<int>();

            double center = Median(cluster.Select(t => t.DurationSeconds));
            int typicalChapters = (int)Math.Round(Median(cluster.Where(t => t.Chapters > 0).Select(t => t.Chapters)));
            var scored = new List<Tuple<VideoTitleInfo, double>>();
            foreach (VideoTitleInfo title in episodeRange)
            {
                double runtimeDistance = Math.Abs(title.DurationSeconds - center) / center;
                double score = Math.Max(0, 100 - runtimeDistance * 260);
                if (runtimeDistance <= 0.15) score += 35;
                if (typicalChapters > 0 && title.Chapters > 0) score += Math.Max(0, 15 - Math.Abs(title.Chapters - typicalChapters) * 3);
                if (title.SizeBytes > 0) score += 5;
                scored.Add(Tuple.Create(title, score));
            }

            int target = expectedCount > 0 ? Math.Min(expectedCount, scored.Count) : cluster.Count;
            List<VideoTitleInfo> selected = scored.OrderByDescending(x => x.Item2).ThenBy(x => x.Item1.Id).Take(target).Select(x => x.Item1).ToList();
            int selectedTotal = selected.Sum(t => t.DurationSeconds);
            foreach (VideoTitleInfo title in all)
            {
                bool playAllByRuntime = selected.Count >= 2 && !selected.Contains(title) && title.DurationSeconds >= selectedTotal * 0.88 && title.DurationSeconds <= selectedTotal * 1.12;
                bool playAllBySegments = playAllByRuntime && selected.Count(t => ContainsSegments(title, t)) >= Math.Max(2, selected.Count - 1);
                if (playAllBySegments) title.SelectionReason = "Probable Play All (summed runtime and segment containment)";
                else if (playAllByRuntime) title.SelectionReason = "Probable Play All (runtime approximates selected episodes)";
                else if (selected.Contains(title))
                {
                    bool duplicateMap = selected.Any(other => other.Id != title.Id && SameSegments(other, title));
                    title.SelectionReason = duplicateMap ? "Probable episode; segment map is shared (not excluded)" : "Probable episode; strong runtime/chapter match";
                }
                else if (title.DurationSeconds < 900) title.SelectionReason = "Probable short extra";
                else title.SelectionReason = "Weaker episode candidate; outside the best runtime cluster";
            }
            return selected.OrderBy(t => t.Id).Select(t => t.Id).ToList();
        }

        private static bool SameSegments(VideoTitleInfo left, VideoTitleInfo right)
        {
            return left.Segments.Count > 0 && left.Segments.Count == right.Segments.Count && left.Segments.SequenceEqual(right.Segments, StringComparer.OrdinalIgnoreCase);
        }

        private static double Median(IEnumerable<int> values)
        {
            int[] ordered = values.OrderBy(x => x).ToArray();
            if (ordered.Length == 0) return 0;
            int middle = ordered.Length / 2;
            return ordered.Length % 2 == 0 ? (ordered[middle - 1] + ordered[middle]) / 2.0 : ordered[middle];
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

            return best;
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
