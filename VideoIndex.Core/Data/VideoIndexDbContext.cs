using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;
using VideoIndex.Core.Models;

namespace VideoIndex.Core.Data
{
    public class VideoIndexDbContext : DbContext
    {
        public VideoIndexDbContext(DbContextOptions<VideoIndexDbContext> options) : base(options) { }

        public DbSet<ScanRoot> ScanRoots => Set<ScanRoot>();
        public DbSet<MediaFile> MediaFiles => Set<MediaFile>();
        public DbSet<Thumbnail> Thumbnails => Set<Thumbnail>();

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            base.OnModelCreating(modelBuilder);

            // JSON list converter for List<string>
            var jsonOptions = new JsonSerializerOptions { WriteIndented = false };

            var listConverter = new ValueConverter<List<string>?, string>(
                v => JsonSerializer.Serialize(v ?? new List<string>(), jsonOptions),
                v => string.IsNullOrWhiteSpace(v)
                        ? new List<string>()
                        : (JsonSerializer.Deserialize<List<string>>(v, jsonOptions) ?? new List<string>())
            );

            // Expression-tree-safe ValueComparer (no 'is' patterns or null-propagation)
            var listComparer = new ValueComparer<List<string>?>(
                (a, b) => (a == null && b == null) || (a != null && b != null && a.SequenceEqual(b)),
                v => v == null
                        ? 0
                        : v.Aggregate(0, (acc, s) => System.HashCode.Combine(acc, s == null ? 0 : s.GetHashCode())),
                v => v == null ? null : v.ToList()
            );

            // MediaFile config
            var mf = modelBuilder.Entity<MediaFile>();
            mf.HasIndex(x => x.Path).IsUnique();
            mf.HasIndex(x => x.Sha256);

            // Apply converter + comparer to all List<string> props
            mf.Property(x => x.PerformerNames).HasConversion(listConverter).Metadata.SetValueComparer(listComparer);
            mf.Property(x => x.SourceTypes).HasConversion(listConverter).Metadata.SetValueComparer(listComparer);
            mf.Property(x => x.OrientationTags).HasConversion(listConverter).Metadata.SetValueComparer(listComparer);
            mf.Property(x => x.OtherTags).HasConversion(listConverter).Metadata.SetValueComparer(listComparer);

            // Thumbnails FK
            modelBuilder.Entity<Thumbnail>()
                .HasOne(t => t.MediaFile)     // ← use the nav, not HasOne<MediaFile>()
                .WithMany(m => m.Thumbnails)
                .HasForeignKey(t => t.MediaFileId)
                .OnDelete(DeleteBehavior.Cascade);
        }
    }
}
