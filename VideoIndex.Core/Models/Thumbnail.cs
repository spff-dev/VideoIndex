using System;

namespace VideoIndex.Core.Models
{
    public class Thumbnail
    {
        public int Id { get; set; }

        public int MediaFileId { get; set; }      // FK
        public MediaFile? MediaFile { get; set; } // nav paired in OnModelCreating

        public byte[] Jpeg { get; set; } = Array.Empty<byte>();

        // Multi-thumbnail support
        public int SequenceNumber { get; set; } = 0;  // 0-4 for position in sequence
        public string Format { get; set; } = "jpeg";  // "jpeg" or "webp"
        public int Width { get; set; }
        public int Height { get; set; }

        public DateTimeOffset GeneratedAt { get; set; }
    }
}
