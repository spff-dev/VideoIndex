using System;
using System.Collections.Generic;

namespace VideoIndex.Core.Models
{
    public class MediaFile
    {
        public int Id { get; set; }

        public int RootId { get; set; }
        public ScanRoot? Root { get; set; }

        public string Path { get; set; } = "";
        public string Filename { get; set; } = "";
        public string? Extension { get; set; }

        public string Sha256 { get; set; } = "";
        public long SizeBytes { get; set; }

        // Probed metadata
        public double? LengthSeconds { get; set; }
        public int? Width { get; set; }
        public int? Height { get; set; }
        public long? BitRate { get; set; }
        public double? FrameRate { get; set; }
        public string? VideoFormat { get; set; }
        public string? AudioFormat { get; set; }
        public long? AudioBitrate { get; set; }
        public int? AudioChannels { get; set; }

        // EDITABLE FIELDS (tag-style)
        public List<string>? SourceTypes { get; set; }          // e.g. Studio, OnlyFans, Amateur, ...
        public string? StudioName { get; set; }                 // free text (suggestions)
        public List<string>? OrientationTags { get; set; }      // e.g. Gay, Straight, Bi, FtM Trans, MtF Trans
        public List<string>? OtherTags { get; set; }            // arbitrary tags (comma sep in UI)

        public int? Year { get; set; }
        public string? SourceUsername { get; set; }

        public List<string>? PerformerNames { get; set; }
        public int? PerformerCount { get; set; }

        public DateTimeOffset CreatedAt { get; set; }
        public DateTimeOffset UpdatedAt { get; set; }

        public ICollection<Thumbnail> Thumbnails { get; set; } = new List<Thumbnail>();
    }
}
