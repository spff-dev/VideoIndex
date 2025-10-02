using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Processing;
using SixLabors.ImageSharp.Formats.Webp;
using VideoIndex.Core.Data;
using VideoIndex.Core.Models;

namespace VideoIndex.Core.Thumbnails
{
    /// <summary>
    /// Generates multiple WebP thumbnails for video preview functionality
    /// </summary>
    public static class ThumbnailGenerator
    {
        /// <summary>
        /// Generate 5 WebP thumbnails at different positions in the video
        /// </summary>
        public static async Task<(bool ok, string? err)> GenerateMultiThumbnailsAsync(
            VideoIndexDbContext db,
            string videoPath,
            int width = 320,
            int webpQuality = 80)
        {
            if (string.IsNullOrWhiteSpace(videoPath))
                return (false, "Video path is required");

            videoPath = Path.GetFullPath(videoPath);
            if (!File.Exists(videoPath))
                return (false, $"File not found: {videoPath}");

            var mf = await db.MediaFiles
                .Include(m => m.Thumbnails)
                .SingleOrDefaultAsync(m => m.Path == videoPath);

            if (mf == null)
                return (false, "File is not indexed yet");

            var duration = mf.LengthSeconds;
            if (duration is null || duration <= 0)
                return (false, "Could not determine duration");

            // Generate 5 thumbnails at 20%, 35%, 50%, 65%, 80% of video duration
            var positions = new[] { 0.20, 0.35, 0.50, 0.65, 0.80 };

            try
            {
                // Remove old thumbnails
                if (mf.Thumbnails.Count > 0)
                    db.Thumbnails.RemoveRange(mf.Thumbnails);

                var now = DateTimeOffset.UtcNow;

                // Generate each thumbnail
                for (int i = 0; i < positions.Length; i++)
                {
                    var timestamp = duration.Value * positions[i];
                    var (ok, webpBytes, thumbWidth, thumbHeight, err) = await GenerateSingleWebPThumbAsync(
                        videoPath,
                        timestamp,
                        width,
                        webpQuality);

                    if (!ok || webpBytes == null)
                    {
                        Console.WriteLine($"Failed to generate thumbnail {i} for {Path.GetFileName(videoPath)}: {err}");
                        continue;
                    }

                    db.Thumbnails.Add(new Thumbnail
                    {
                        MediaFileId = mf.Id,
                        Jpeg = webpBytes, // Field name is historical, now stores WebP
                        SequenceNumber = i,
                        Format = "webp",
                        Width = thumbWidth,
                        Height = thumbHeight,
                        GeneratedAt = now
                    });
                }

                await db.SaveChangesAsync();
                return (true, null);
            }
            catch (Exception ex)
            {
                return (false, $"Error generating thumbnails: {ex.Message}");
            }
        }

        /// <summary>
        /// Generate a single WebP thumbnail at specified timestamp
        /// </summary>
        private static async Task<(bool ok, byte[]? webp, int width, int height, string? err)> GenerateSingleWebPThumbAsync(
            string videoPath,
            double timestamp,
            int targetWidth,
            int webpQuality)
        {
            var tmpJpeg = Path.Combine(Path.GetTempPath(), $"thumb_{Guid.NewGuid():N}.jpg");

            try
            {
                // Step 1: Extract frame using FFmpeg
                var ts = timestamp.ToString("0.###", CultureInfo.InvariantCulture);
                var args = $"-y -ss {ts} -i \"{videoPath}\" -frames:v 1 -vf \"scale={targetWidth}:-1:flags=lanczos\" -q:v 2 \"{tmpJpeg}\"";

                var (code, _, stderr) = await RunProcessAsync("ffmpeg", args, TimeSpan.FromSeconds(20));

                if (code != 0 || !File.Exists(tmpJpeg))
                    return (false, null, 0, 0, $"FFmpeg failed (code {code}): {stderr}");

                // Step 2: Convert JPEG to WebP using ImageSharp
                using var image = await Image.LoadAsync(tmpJpeg);

                var actualWidth = image.Width;
                var actualHeight = image.Height;

                using var memoryStream = new MemoryStream();

                var encoder = new WebpEncoder
                {
                    Quality = webpQuality,
                    FileFormat = WebpFileFormatType.Lossy,
                    Method = WebpEncodingMethod.BestQuality
                };

                await image.SaveAsync(memoryStream, encoder);

                return (true, memoryStream.ToArray(), actualWidth, actualHeight, null);
            }
            catch (Exception ex)
            {
                return (false, null, 0, 0, ex.Message);
            }
            finally
            {
                try
                {
                    if (File.Exists(tmpJpeg))
                        File.Delete(tmpJpeg);
                }
                catch { }
            }
        }

        private static async Task<(int exitCode, string stdout, string stderr)> RunProcessAsync(
            string exe,
            string args,
            TimeSpan timeout)
        {
            var psi = new ProcessStartInfo
            {
                FileName = exe,
                Arguments = args,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            using var proc = new Process { StartInfo = psi, EnableRaisingEvents = true };
            proc.Start();

            var stdoutTask = proc.StandardOutput.ReadToEndAsync();
            var stderrTask = proc.StandardError.ReadToEndAsync();

            if (!proc.WaitForExit((int)timeout.TotalMilliseconds))
            {
                try { proc.Kill(true); } catch { }
                return (-1, "", "Timed out");
            }

            var stdout = await stdoutTask;
            var stderr = await stderrTask;

            return (proc.ExitCode, stdout, stderr);
        }
    }
}
