using System;
using System.Collections.Generic;

namespace DiscRipper
{
    internal enum DetectionConfidence { Low, Medium, High }

    internal sealed class DiscToc
    {
        public int FirstTrack;
        public int LastTrack;
        public int LeadoutOffset;
        public readonly List<int> TrackOffsets = new List<int>();
        public string DiscId;
        public string TocQuery;
    }

    internal sealed class VideoTitleInfo
    {
        public int Id;
        public int DurationSeconds;
        public long SizeBytes;
        public int Chapters;
        public string Name;
    }

    internal sealed class DiscAnalysis
    {
        public MediaKind Kind;
        public DetectionConfidence Confidence;
        public string Summary;
        public DiscToc AudioToc;
        public readonly List<VideoTitleInfo> VideoTitles = new List<VideoTitleInfo>();
        public readonly List<int> SelectedTitleIds = new List<int>();
    }

    internal sealed class MusicRelease
    {
        public string Id;
        public string ReleaseGroupId;
        public string Title;
        public string Artist;
        public string AlbumArtist;
        public string Date;
        public string Country;
        public string Label;
        public string CatalogNumber;
        public string Barcode;
        public int MediumPosition;
        public int MediumCount;
        public readonly List<MusicTrack> Tracks = new List<MusicTrack>();
        public string DisplayName
        {
            get
            {
                string edition = string.Join(" · ", new[] { Country, Date, Label }.WhereNotEmpty());
                return Artist + " — " + Title + (edition.Length == 0 ? "" : " (" + edition + ")");
            }
        }
    }

    internal sealed class MusicTrack
    {
        public int Number;
        public string Title;
        public string Artist;
        public string RecordingId;
        public string Isrc;
        public string Composer;
    }

    internal static class StringArrayExtensions
    {
        public static IEnumerable<string> WhereNotEmpty(this IEnumerable<string> values)
        {
            foreach (string value in values) if (!string.IsNullOrWhiteSpace(value)) yield return value;
        }
    }
}
