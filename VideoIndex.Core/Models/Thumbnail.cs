using System;

namespace VideoIndex.Core.Models
{
    public class Thumbnail
    {
        public int Id { get; set; }

        public int MediaFileId { get; set; }      // FK
        public MediaFile? MediaFile { get; set; } // nav paired in OnModelCreating

        public byte[] Jpeg { get; set; } = Array.Empty<byte>();
        public DateTimeOffset GeneratedAt { get; set; }
    }
}
