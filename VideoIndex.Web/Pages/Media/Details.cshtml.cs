using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using VideoIndex.Core.Data;
using VideoIndex.Core.Models;

namespace VideoIndex.Web.Pages.Media
{
    public class DetailsModel : PageModel
    {
        private readonly IDbContextFactory<VideoIndexDbContext> _factory;

        public DetailsModel(IDbContextFactory<VideoIndexDbContext> factory) => _factory = factory;

        public VM? Item { get; private set; }

        public async Task<IActionResult> OnGetAsync(int id)
        {
            await using var db = await _factory.CreateDbContextAsync();

            var m = await db.MediaFiles
                .AsNoTracking()
                .Include(x => x.Root)
                .SingleOrDefaultAsync(x => x.Id == id);

            if (m == null) return NotFound();

            Item = new VM
            {
                Id = m.Id,
                Filename = m.Filename,
                Path = m.Path,
                Extension = m.Extension,
                SizeBytes = m.SizeBytes,
                SizeHuman = HumanSize(m.SizeBytes),
                LengthSeconds = m.LengthSeconds,
                LengthHuman = m.LengthSeconds is double s ? TimeSpan.FromSeconds(s).ToString(@"hh\:mm\:ss", CultureInfo.InvariantCulture) : null,
                Width = m.Width,
                Height = m.Height,
                BitRate = m.BitRate,
                BitRateHuman = m.BitRate is long br ? HumanBitrate(br) : null,
                FrameRate = m.FrameRate,
                VideoFormat = m.VideoFormat,
                AudioFormat = m.AudioFormat,
                AudioBitrate = m.AudioBitrate,
                AudioBitrateHuman = m.AudioBitrate is long abr ? HumanBitrate(abr) : null,
                AudioChannels = m.AudioChannels,
                Sha256 = m.Sha256,
                CreatedAt = m.CreatedAt,
                UpdatedAt = m.UpdatedAt,
                Root = m.Root?.Name,
                RootPath = m.Root?.Path,

                // NEW: expose tag-style fields to the view
                SourceTypes = m.SourceTypes ?? new List<string>(),
                OrientationTags = m.OrientationTags ?? new List<string>(),
                OtherTags = m.OtherTags ?? new List<string>(),
                StudioName = m.StudioName,
                SourceUsername = m.SourceUsername,
                Year = m.Year,

                // Keep legacy fields so existing .cshtml still works until we swap it next
                Orientation = (m.OrientationTags != null && m.OrientationTags.Count > 0) ? m.OrientationTags[0] : null,
                Type = (m.SourceTypes != null && m.SourceTypes.Count > 0) ? m.SourceTypes[0] : null,
                Studio = m.StudioName,
                WebsiteSource = null,

                PerformerCount = m.PerformerCount,
                PerformerNames = m.PerformerNames ?? new List<string>()
            };

            return Page();
        }

        static string HumanSize(long bytes)
        {
            double b = bytes;
            string[] units = { "B", "KB", "MB", "GB", "TB" };
            int u = 0;
            while (b >= 1024 && u < units.Length - 1) { b /= 1024; u++; }
            return $"{b:0.##} {units[u]}";
        }

        static string HumanBitrate(long bps)
        {
            if (bps >= 1_000_000) return $"{bps / 1_000_000.0:0.##} Mbps";
            if (bps >= 1_000) return $"{bps / 1_000.0:0.##} kbps";
            return $"{bps} bps";
        }

        public class VM
        {
            public int Id { get; set; }
            public string Filename { get; set; } = "";
            public string Path { get; set; } = "";
            public string? Extension { get; set; }
            public long SizeBytes { get; set; }
            public string SizeHuman { get; set; } = "";
            public double? LengthSeconds { get; set; }
            public string? LengthHuman { get; set; }
            public int? Width { get; set; }
            public int? Height { get; set; }
            public long? BitRate { get; set; }
            public string? BitRateHuman { get; set; }
            public double? FrameRate { get; set; }
            public string? VideoFormat { get; set; }
            public string? AudioFormat { get; set; }
            public long? AudioBitrate { get; set; }
            public string? AudioBitrateHuman { get; set; }
            public int? AudioChannels { get; set; }
            public string Sha256 { get; set; } = "";
            public DateTimeOffset CreatedAt { get; set; }
            public DateTimeOffset UpdatedAt { get; set; }
            public string? Root { get; set; }
            public string? RootPath { get; set; }

            // Tag-style fields (new)
            public List<string> SourceTypes { get; set; } = new();
            public List<string> OrientationTags { get; set; } = new();
            public List<string> OtherTags { get; set; } = new();
            public string? StudioName { get; set; }
            public string? SourceUsername { get; set; }
            public int? Year { get; set; }

            // Legacy fields kept so current .cshtml still compiles until next step
            public string? Orientation { get; set; }
            public string? Type { get; set; }
            public string? Studio { get; set; }
            public string? WebsiteSource { get; set; }

            public int? PerformerCount { get; set; }
            public List<string> PerformerNames { get; set; } = new();
        }
    }
}
