using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing; // average luma calc
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using VideoIndex.Core.Data;
using VideoIndex.Core.Models;
using VideoIndex.Web.Hubs;
using System.Text.RegularExpressions;

[assembly: SupportedOSPlatform("windows")]

var builder = WebApplication.CreateBuilder(args);

// DB via factory
var conn = builder.Configuration.GetConnectionString("VideoIndex");
builder.Services.AddDbContextFactory<VideoIndexDbContext>(options => options.UseSqlite(conn));

// UI + SignalR
builder.Services.AddRazorPages();
builder.Services.AddSignalR();

var app = builder.Build();

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error");
    app.UseHsts();
}
// app.UseHttpsRedirection();
app.UseStaticFiles();
app.UseRouting();

// Redirect home to /library
app.MapGet("/", () => Results.Redirect("/library", permanent: false));

app.MapGet("/api/ping", () => Results.Ok("pong"));

// ---------- Roots ----------
app.MapGet("/api/roots", async (IDbContextFactory<VideoIndexDbContext> dbFactory) =>
{
    await using var db = await dbFactory.CreateDbContextAsync();
    return await db.ScanRoots.OrderBy(r => r.Id).ToListAsync();
});

app.MapPost("/api/roots", async (IDbContextFactory<VideoIndexDbContext> dbFactory, RootDto dto) =>
{
    if (string.IsNullOrWhiteSpace(dto.Name) || string.IsNullOrWhiteSpace(dto.Path))
        return Results.BadRequest("Name and Path are required.");
    if (!Directory.Exists(dto.Path))
        return Results.BadRequest($"Path does not exist: {dto.Path}");

    await using var db = await dbFactory.CreateDbContextAsync();
    var root = new ScanRoot { Name = dto.Name.Trim(), Path = dto.Path.Trim(), LastScannedAt = null };
    db.ScanRoots.Add(root);
    await db.SaveChangesAsync();
    return Results.Created($"/api/roots/{root.Id}", root);
});

// ---------- Dry-run ----------
app.MapGet("/api/roots/{id:int}/scan-dry-run", async (int id, IDbContextFactory<VideoIndexDbContext> dbFactory) =>
{
    await using var db = await dbFactory.CreateDbContextAsync();
    var root = await db.ScanRoots.FindAsync(id);
    if (root is null) return Results.NotFound($"Root {id} not found.");
    if (!Directory.Exists(root.Path)) return Results.BadRequest($"Path does not exist: {root.Path}");

    var allowedExt = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    { "mp4","mkv","mov","avi","wmv","webm","flv","mpg","mpeg","ogv","ts","3gp" };

    var sw = Stopwatch.StartNew();
    var sample = new List<string>(capacity: 10);
    var count = 0;

    foreach (var file in Directory.EnumerateFiles(root.Path, "*.*", SearchOption.AllDirectories))
    {
        var ext = Path.GetExtension(file);
        if (string.IsNullOrEmpty(ext)) continue;
        var extNoDot = ext[0] == '.' ? ext[1..] : ext;
        if (!allowedExt.Contains(extNoDot)) continue;

        count++;
        if (sample.Count < 10)
            sample.Add(Path.GetRelativePath(root.Path, file));
    }

    sw.Stop();
    return Results.Ok(new
    {
        root = new { root.Id, root.Name, root.Path },
        discovered = count,
        sample,
        elapsedMs = sw.ElapsedMilliseconds
    });
});

// ---------- ffprobe debug ----------
app.MapGet("/api/debug/inspect", async (string path) =>
{
    if (string.IsNullOrWhiteSpace(path))
        return Results.BadRequest("Query ?path=... is required.");
    path = Path.GetFullPath(path);
    if (!File.Exists(path))
        return Results.NotFound($"File not found: {path}");

    if (new FileInfo(path).Length == 0)
        return Results.BadRequest("File is zero bytes.");

    var (code, stdout, stderr) = await Proc.RunAsync(
        "ffprobe",
        $"-v error -hide_banner -print_format json -show_format -show_streams \"{path}\"",
        TimeSpan.FromSeconds(30)
    );
    if (code != 0) return Results.Problem($"ffprobe failed (code {code}). {stderr}");
    return Results.Content(stdout, "application/json");
});

// ---------- Index one ----------
app.MapGet("/api/debug/index-one", async (IDbContextFactory<VideoIndexDbContext> dbFactory, string path) =>
{
    var res = await Indexer.IndexOneWithFactoryAsync(dbFactory, path);
    return res.ok ? Results.Ok(res.result) : Results.Problem(res.err!);
});

// ---------- Debug fetch ----------
app.MapGet("/api/debug/media-by-path", async (IDbContextFactory<VideoIndexDbContext> dbFactory, string path) =>
{
    if (string.IsNullOrWhiteSpace(path))
        return Results.BadRequest("Query ?path=... is required.");
    path = Path.GetFullPath(path);

    await using var db = await dbFactory.CreateDbContextAsync();
    var row = await db.MediaFiles
        .AsNoTracking()
        .Where(m => m.Path == path)
        .Select(m => new
        {
            m.Id,
            m.Path,
            m.Filename,
            m.Extension,
            m.Sha256,
            m.SizeBytes,
            m.LengthSeconds,
            m.Width,
            m.Height,
            m.BitRate,
            m.FrameRate,
            m.VideoFormat,
            m.AudioFormat,
            m.AudioBitrate,
            m.AudioChannels,
            m.CreatedAt,
            m.UpdatedAt,
            m.RootId
        })
        .SingleOrDefaultAsync();

    return row is null ? Results.NotFound() : Results.Ok(row);
});

// ---------- Thumbnail one ----------
app.MapGet("/api/debug/thumbnail-one", async (IDbContextFactory<VideoIndexDbContext> dbFactory, string path) =>
{
    var res = await Indexer.GenerateThumbnailWithFactoryAsync(dbFactory, path, lumaThreshold: 28, maxTries: 5, width: 320, jpegQuality: 85, onlyIfMissing: false);
    if (!res.ok) return Results.Problem(res.err!);
    return Results.File(res.jpeg!, "image/jpeg");
});

// ---------- Get stored thumbnail (by path) ----------
app.MapGet("/api/debug/get-thumb", async (IDbContextFactory<VideoIndexDbContext> dbFactory, string path) =>
{
    if (string.IsNullOrWhiteSpace(path))
        return Results.BadRequest("Query ?path=... is required.");
    path = Path.GetFullPath(path);

    await using var db = await dbFactory.CreateDbContextAsync();
    var mf = await db.MediaFiles.Include(m => m.Thumbnails).SingleOrDefaultAsync(m => m.Path == path);
    if (mf == null) return Results.NotFound("Media not found.");
    var th = mf.Thumbnails.OrderByDescending(t => t.Id).FirstOrDefault();
    if (th == null) return Results.NotFound("No thumbnail stored for this media.");

    return Results.File(th.Jpeg, "image/jpeg");
});

// ---------- Serve thumbnail (by id) ----------
app.MapGet("/api/media/{id:int}/thumb", async (IDbContextFactory<VideoIndexDbContext> dbFactory, int id) =>
{
    await using var db = await dbFactory.CreateDbContextAsync();
    var jpeg = await db.Thumbnails
        .Where(t => t.MediaFileId == id)
        .OrderByDescending(t => t.Id)
        .Select(t => t.Jpeg)
        .FirstOrDefaultAsync();

    if (jpeg == null) return Results.NotFound();
    return Results.File(jpeg, "image/jpeg");
});

// ---------- Quick actions: open / reveal ----------
app.MapPost("/api/media/{id:int}/open", async (int id, IDbContextFactory<VideoIndexDbContext> dbFactory) =>
{
    await using var db = await dbFactory.CreateDbContextAsync();
    var path = await db.MediaFiles.AsNoTracking()
        .Where(m => m.Id == id)
        .Select(m => m.Path)
        .SingleOrDefaultAsync();

    if (path == null || !File.Exists(path))
        return Results.NotFound("File not found on disk.");

    try
    {
        var psi = new ProcessStartInfo
        {
            FileName = path,
            UseShellExecute = true
        };
        Process.Start(psi);
        return Results.Ok(new { launched = true });
    }
    catch (Exception ex)
    {
        return Results.Problem(ex.Message);
    }
});

app.MapPost("/api/media/{id:int}/reveal", async (int id, IDbContextFactory<VideoIndexDbContext> dbFactory) =>
{
    await using var db = await dbFactory.CreateDbContextAsync();
    var path = await db.MediaFiles.AsNoTracking()
        .Where(m => m.Id == id)
        .Select(m => m.Path)
        .SingleOrDefaultAsync();

    if (path == null || !File.Exists(path))
        return Results.NotFound("File not found on disk.");

    try
    {
        var psi = new ProcessStartInfo
        {
            FileName = "explorer.exe",
            Arguments = $"/select,\"{path}\"",
            UseShellExecute = true
        };
        Process.Start(psi);
        return Results.Ok(new { revealed = true });
    }
    catch (Exception ex)
    {
        return Results.Problem(ex.Message);
    }
});

// ---------- Stream video with Range (for inline HTML5 playback) ----------
app.MapGet("/api/media/{id:int}/stream", async (int id, IDbContextFactory<VideoIndexDbContext> dbFactory) =>
{
    await using var db = await dbFactory.CreateDbContextAsync();
    var path = await db.MediaFiles.AsNoTracking()
        .Where(m => m.Id == id)
        .Select(m => m.Path)
        .SingleOrDefaultAsync();

    if (path == null || !File.Exists(path))
        return Results.NotFound("File not found on disk.");

    // Basic content-type mapping; browser playback still depends on codec support.
    string ext = Path.GetExtension(path).TrimStart('.').ToLowerInvariant();
    string contentType = ext switch
    {
        "mp4" => "video/mp4",
        "webm" => "video/webm",
        "mkv" => "video/x-matroska",
        "avi" => "video/x-msvideo",
        "mov" => "video/quicktime",
        "wmv" => "video/x-ms-wmv",
        "ogv" => "video/ogg",
        "ts" => "video/mp2t",
        "3gp" => "video/3gpp",
        "mpg" or "mpeg" => "video/mpeg",
        "flv" => "video/x-flv",
        _ => "application/octet-stream"
    };

    // Let ASP.NET handle Range requests for seeking.
    return Results.File(path, contentType, enableRangeProcessing: true);
});

app.MapDelete("/api/roots/{id:int}", async (int id, IDbContextFactory<VideoIndexDbContext> dbFactory) =>
{
    await using var db = await dbFactory.CreateDbContextAsync();
    var root = await db.ScanRoots.FindAsync(id);
    if (root is null) return Results.NotFound();
    db.ScanRoots.Remove(root);
    await db.SaveChangesAsync();
    return Results.Ok(new { deleted = true });
});


// ---------- NAV: prev/next (with optional "untagged only") ----------
app.MapGet("/api/media/nav", async (
    IDbContextFactory<VideoIndexDbContext> dbFactory,
    int id,
    bool? untaggedOnly
) =>
{
    await using var db = await dbFactory.CreateDbContextAsync();

    IQueryable<MediaFile> q = db.MediaFiles.AsNoTracking();

    if (untaggedOnly == true)
    {
        // "Untagged" = no user-provided metadata yet
        q = q.Where(m =>
            (m.SourceTypes == null || m.SourceTypes.Count == 0) &&
            (m.OrientationTags == null || m.OrientationTags.Count == 0) &&
            (m.OtherTags == null || m.OtherTags.Count == 0) &&
            (m.PerformerNames == null || m.PerformerNames.Count == 0) &&
            m.PerformerCount == null &&
            m.Year == null &&
            (m.StudioName == null || m.StudioName == "") &&
            (m.SourceUsername == null || m.SourceUsername == "")
        );
    }

    // Stable default order: UpdatedAt desc, then Id desc (ties)
    var ids = await q
        .OrderByDescending(m => m.UpdatedAt)
        .ThenByDescending(m => m.Id)
        .Select(m => m.Id)
        .ToListAsync();

    var idx = ids.IndexOf(id);
    if (idx < 0)
        return Results.Ok(new { prevId = (int?)null, nextId = (int?)null });

    int? prevId = idx > 0 ? ids[idx - 1] : (int?)null;
    int? nextId = idx < ids.Count - 1 ? ids[idx + 1] : (int?)null;

    return Results.Ok(new { prevId, nextId });
});

// ---------- Meta distincts (updated to new fields) ----------
app.MapGet("/api/meta/distinct", async (IDbContextFactory<VideoIndexDbContext> dbFactory, string field, int? take) =>
{
    int top = Math.Clamp(take ?? 50, 1, 200);
    string key = field?.Trim() ?? "";
    if (string.IsNullOrEmpty(key)) return Results.BadRequest("field is required.");

    await using var db = await dbFactory.CreateDbContextAsync();

    switch (key.ToLowerInvariant())
    {
        case "type": // now sourced from SourceTypes (flatten)
            {
                var lists = await db.MediaFiles.AsNoTracking()
                    .Select(m => m.SourceTypes)
                    .ToListAsync();

                var dict = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
                foreach (var l in lists)
                {
                    if (l is null) continue;
                    foreach (var v in l)
                    {
                        var s = v?.Trim();
                        if (string.IsNullOrEmpty(s)) continue;
                        dict[s] = dict.TryGetValue(s, out var c) ? c + 1 : 1;
                    }
                }

                var items = dict.OrderByDescending(kv => kv.Value).ThenBy(kv => kv.Key)
                    .Take(top).Select(kv => new { value = kv.Key, count = kv.Value });
                return Results.Ok(items);
            }

        case "studio": // now StudioName
            {
                var items = await db.MediaFiles.AsNoTracking()
                    .Where(m => m.StudioName != null && m.StudioName != "")
                    .GroupBy(m => m.StudioName!)
                    .Select(g => new { value = g.Key, count = g.Count() })
                    .OrderByDescending(x => x.count).ThenBy(x => x.value)
                    .Take(top)
                    .ToListAsync();
                return Results.Ok(items);
            }

        case "websitesource":
            {
                // Column removed; return empty set to keep callers happy.
                return Results.Ok(Array.Empty<object>());
            }

        case "sourceusername":
            {
                var items = await db.MediaFiles.AsNoTracking()
                    .Where(m => m.SourceUsername != null && m.SourceUsername != "")
                    .GroupBy(m => m.SourceUsername!)
                    .Select(g => new { value = g.Key, count = g.Count() })
                    .OrderByDescending(x => x.count).ThenBy(x => x.value)
                    .Take(top)
                    .ToListAsync();
                return Results.Ok(items);
            }

        case "orientation":
            {
                var lists = await db.MediaFiles.AsNoTracking()
                    .Select(m => m.OrientationTags)
                    .ToListAsync();

                var dict = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
                foreach (var l in lists)
                {
                    if (l is null) continue;
                    foreach (var v in l)
                    {
                        var s = v?.Trim();
                        if (string.IsNullOrEmpty(s)) continue;
                        dict[s] = dict.TryGetValue(s, out var c) ? c + 1 : 1;
                    }
                }

                var items = dict.OrderByDescending(kv => kv.Value).ThenBy(kv => kv.Key)
                    .Take(top).Select(kv => new { value = kv.Key, count = kv.Value });
                return Results.Ok(items);
            }

        case "performers":
            {
                var lists = await db.MediaFiles.AsNoTracking()
                    .Select(m => m.PerformerNames)
                    .ToListAsync();

                var dict = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
                foreach (var l in lists)
                {
                    if (l is null || l.Count == 0) continue;
                    foreach (var name in l)
                    {
                        var n = name?.Trim();
                        if (string.IsNullOrEmpty(n)) continue;
                        dict[n] = dict.TryGetValue(n, out var c) ? c + 1 : 1;
                    }
                }

                var items = dict.OrderByDescending(kv => kv.Value).ThenBy(kv => kv.Key)
                    .Take(top).Select(kv => new { value = kv.Key, count = kv.Value });
                return Results.Ok(items);
            }

        case "other":
            {
                var lists = await db.MediaFiles.AsNoTracking()
                    .Select(m => m.OtherTags)
                    .ToListAsync();

                var dict = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
                foreach (var l in lists)
                {
                    if (l is null) continue;
                    foreach (var v in l)
                    {
                        var s = v?.Trim();
                        if (string.IsNullOrEmpty(s)) continue;
                        dict[s] = dict.TryGetValue(s, out var c) ? c + 1 : 1;
                    }
                }

                var items = dict.OrderByDescending(kv => kv.Value).ThenBy(kv => kv.Key)
                    .Take(top).Select(kv => new { value = kv.Key, count = kv.Value });
                return Results.Ok(items);
            }

        default:
            return Results.BadRequest("Unknown field. Try: type, studio, websiteSource, sourceUsername, orientation, performers, other");
    }
});

// ---------- UPDATE MEDIA (new + legacy accepted) ----------
app.MapPut("/api/media/{id:int}", async (IDbContextFactory<VideoIndexDbContext> dbFactory, int id, UpdateMediaDto dto) =>
{
    await using var db = await dbFactory.CreateDbContextAsync();
    var m = await db.MediaFiles.SingleOrDefaultAsync(x => x.Id == id);
    if (m is null) return Results.NotFound("Media not found.");

    // Allowed sets
    var allowedSourceTypes = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    { "Studio", "OnlyFans", "Amateur", "Vintage", "Webcam", "Spycam", "CCTV", "Selfie", "Social Media", "Animated", "Unknown" };

    var allowedOrientation = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    { "Gay", "Straight", "Bi", "FtM Trans", "MtF Trans" };

    // --- SourceTypes (array) or legacy Type (single) ---
    List<string>? srcTypes = null;
    if (dto.SourceTypes is { Length: > 0 })
    {
        srcTypes = dto.SourceTypes
            .Select(s => s?.Trim())
            .Where(s => !string.IsNullOrEmpty(s))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Select(s => s!)
            .ToList();

        if (srcTypes.Any(s => !allowedSourceTypes.Contains(s!)))
            return Results.BadRequest("SourceType contains invalid values.");
        // normalize casing
        srcTypes = srcTypes.Select(s => allowedSourceTypes.First(a => a.Equals(s!, StringComparison.OrdinalIgnoreCase))).ToList();
    }
    else if (!string.IsNullOrWhiteSpace(dto.Type)) // legacy
    {
        var t = dto.Type.Trim();
        if (allowedSourceTypes.Contains(t))
            srcTypes = new List<string> { allowedSourceTypes.First(a => a.Equals(t, StringComparison.OrdinalIgnoreCase)) };
    }
    m.SourceTypes = (srcTypes is { Count: > 0 }) ? srcTypes : null;

    // --- StudioName (free text) (legacy Studio maps here) ---
    var studioName = dto.StudioName ?? dto.Studio; // legacy map
    studioName = string.IsNullOrWhiteSpace(studioName) ? null : studioName.Trim();
    m.StudioName = studioName;

    // --- SourceUsername (free text) ---
    m.SourceUsername = string.IsNullOrWhiteSpace(dto.SourceUsername) ? null : dto.SourceUsername.Trim();

    // --- Year ---
    if (dto.Year.HasValue)
    {
        var y = dto.Year.Value;
        if (y < 1900 || y > 2100) return Results.BadRequest("Year must be between 1900 and 2100.");
        m.Year = y;
    }

    // --- OrientationTags (array) or legacy Orientation (single) ---
    List<string>? oTags = null;
    if (dto.OrientationTags is { Length: > 0 })
    {
        oTags = dto.OrientationTags
            .Select(s => s?.Trim())
            .Where(s => !string.IsNullOrEmpty(s))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Select(s => s!)
            .ToList();

        if (oTags.Any(s => !allowedOrientation.Contains(s!)))
            return Results.BadRequest("Orientation contains invalid values.");
        oTags = oTags.Select(s => allowedOrientation.First(a => a.Equals(s!, StringComparison.OrdinalIgnoreCase))).ToList();
    }
    else if (!string.IsNullOrWhiteSpace(dto.Orientation)) // legacy single
    {
        var o = dto.Orientation.Trim();
        if (allowedOrientation.Contains(o))
            oTags = new List<string> { allowedOrientation.First(a => a.Equals(o, StringComparison.OrdinalIgnoreCase)) };
    }
    m.OrientationTags = (oTags is { Count: > 0 }) ? oTags : null;

    // --- OtherTags (comma-separated free text) ---
    if (dto.OtherTags is not null)
    {
        var list = dto.OtherTags
            .Split(',', StringSplitOptions.RemoveEmptyEntries)
            .Select(s => s.Trim())
            .Where(s => !string.IsNullOrWhiteSpace(s))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        m.OtherTags = list.Count > 0 ? list : null;
    }

    // --- Performers (legacy field; still valid) ---
    if (dto.Performers is not null)
    {
        var list = dto.Performers
            .Split(',', StringSplitOptions.RemoveEmptyEntries)
            .Select(s => s.Trim())
            .Where(s => !string.IsNullOrWhiteSpace(s))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        m.PerformerNames = list.Count > 0 ? list : null;
        m.PerformerCount = list.Count > 0 ? list.Count : m.PerformerCount;
    }

    // --- PerformerCount explicit (optional) ---
    if (dto.PerformerCount.HasValue)
    {
        var pc = dto.PerformerCount.Value;
        if (pc < 0) return Results.BadRequest("PerformerCount must be >= 0.");
        m.PerformerCount = pc;
    }

    m.UpdatedAt = DateTimeOffset.UtcNow;
    await db.SaveChangesAsync();
    return Results.Ok(new { updated = true });
});

// ---------- Live scan (unchanged) ----------
app.MapPost("/api/roots/{id:int}/scan-live", async (
    int id,
    string scanId,
    bool? thumbs,
    bool? regenAll,
    int? dop,
    IHubContext<ScanHub> hub,
    IDbContextFactory<VideoIndexDbContext> dbFactory) =>
{
    await using var db0 = await dbFactory.CreateDbContextAsync();
    var root = await db0.ScanRoots.FindAsync(id);
    if (root is null) return Results.NotFound($"Root {id} not found.");
    if (!Directory.Exists(root.Path)) return Results.BadRequest($"Path does not exist: {root.Path}");
    if (string.IsNullOrWhiteSpace(scanId)) return Results.BadRequest("scanId is required.");

    bool genThumbs = thumbs.GetValueOrDefault(true);
    bool doRegenAll = regenAll.GetValueOrDefault(false);
    int degree = Math.Clamp(dop ?? Math.Max(1, Environment.ProcessorCount - 4), 1, 24);

    var allowedExt = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    { "mp4","mkv","mov","avi","wmv","webm","flv","mpg","mpeg","ogv","ts","3gp" };

    var files = new List<string>(capacity: 8192);
    foreach (var file in Directory.EnumerateFiles(root.Path, "*.*", SearchOption.AllDirectories))
    {
        var ext = Path.GetExtension(file);
        if (string.IsNullOrEmpty(ext)) continue;
        var extNoDot = ext[0] == '.' ? ext[1..] : ext;
        if (!allowedExt.Contains(extNoDot)) continue;
        files.Add(Path.GetFullPath(file));
    }

    var startedAt = DateTimeOffset.UtcNow;
    int total = files.Count;
    int done = 0, indexed = 0, thumbsMade = 0, errors = 0;

    await hub.Clients.Group($"scan:{scanId}").SendAsync("progress", new
    {
        kind = "started",
        root = new { root.Id, root.Name, root.Path },
        totals = new { files = total, thumbsRequested = genThumbs, thumbsCap = (int?)null, regenAll = doRegenAll, dop = degree },
        startedAt
    });

    var sw = Stopwatch.StartNew();

    await Parallel.ForEachAsync(files, new ParallelOptions { MaxDegreeOfParallelism = degree }, async (f, ct) =>
    {
        var rel = Path.GetRelativePath(root.Path, f);

        var res = await Indexer.IndexOneWithFactoryAsync(dbFactory, f);
        if (!res.ok)
        {
            Interlocked.Increment(ref errors);
            await hub.Clients.Group($"scan:{scanId}").SendAsync("progress", new { kind = "indexError", file = rel, error = res.err });
        }
        else
        {
            Interlocked.Increment(ref indexed);

            if (genThumbs)
            {
                var tres = await Indexer.GenerateThumbnailWithFactoryAsync(dbFactory, f, lumaThreshold: 28, maxTries: 5, width: 320, jpegQuality: 85, onlyIfMissing: !doRegenAll);
                if (tres.ok)
                {
                    Interlocked.Increment(ref thumbsMade);
                    await hub.Clients.Group($"scan:{scanId}").SendAsync("progress", new { kind = "thumbMade", file = rel });
                }
                else if (tres.err != "skip")
                {
                    await hub.Clients.Group($"scan:{scanId}").SendAsync("progress", new { kind = "thumbError", file = rel, error = tres.err });
                }
            }
        }

        var curDone = Interlocked.Increment(ref done);
        if (curDone % 10 == 0)
        {
            await hub.Clients.Group($"scan:{scanId}").SendAsync("progress", new { kind = "heartbeat", processed = curDone, indexed, thumbsMade, errors, elapsedMs = sw.ElapsedMilliseconds });
        }
    });

    await using (var db1 = await dbFactory.CreateDbContextAsync())
    {
        var r = await db1.ScanRoots.FindAsync(id);
        if (r != null)
        {
            r.LastScannedAt = DateTimeOffset.UtcNow;
            await db1.SaveChangesAsync();
        }
    }

    sw.Stop();

    await hub.Clients.Group($"scan:{scanId}").SendAsync("progress", new { kind = "completed", processed = done, indexed, thumbsMade, errors, elapsedMs = sw.ElapsedMilliseconds });

    return Results.Ok(new
    {
        root = new { root.Id, root.Name, root.Path },
        scanned = total,
        processed = done,
        indexed,
        thumbs = new { requested = genThumbs, made = thumbsMade, regenAll = doRegenAll },
        errors,
        elapsedMs = sw.ElapsedMilliseconds
    });
});

// ----- Media browse (filters + sorting + paging + search) -----
app.MapGet("/api/media/browse", async (
    HttpRequest req,
    IDbContextFactory<VideoIndexDbContext> factory
) =>
{
    // Read query params
    int skip = int.TryParse(req.Query["skip"], out var sk) ? Math.Max(0, sk) : 0;
    int take = int.TryParse(req.Query["take"], out var tk) ? Math.Clamp(tk, 1, 200) : 36;

    string sort = req.Query["sort"].ToString();
    if (string.IsNullOrWhiteSpace(sort)) sort = "updated_desc";
    sort = sort.ToLowerInvariant();

    List<string> srcFilter = SplitCsv(req.Query["source"].ToString());
    List<string> oriFilter = SplitCsv(req.Query["orientation"].ToString());
    string? q = req.Query["q"]; // free-text search

    int? minPerf = int.TryParse(req.Query["minPerformers"], out var mp) ? mp : null;
    int? maxPerf = int.TryParse(req.Query["maxPerformers"], out var xp) ? xp : null;

    await using var db = await factory.CreateDbContextAsync();

    IQueryable<MediaFile> qEF = db.MediaFiles.AsNoTracking();

    if (minPerf.HasValue)
        qEF = qEF.Where(m => m.PerformerCount != null && m.PerformerCount >= minPerf.Value);

    if (maxPerf.HasValue)
        qEF = qEF.Where(m => m.PerformerCount != null && m.PerformerCount <= maxPerf.Value);

    // Prefetch cap for client-side tag filtering + updated sorting.
    const int PREFETCH_MAX = 5000;

    // Pre-order for stable pagination before client-side sort/filter
    qEF = qEF.OrderByDescending(m => m.Id).Take(PREFETCH_MAX);

    // Materialize to a strongly-typed anonymous list (not dynamic)
    var pre = await qEF
        .Select(m => new
        {
            m.Id,
            m.Filename,
            m.Path,
            m.Extension,
            m.SizeBytes,
            m.LengthSeconds,
            m.Width,
            m.Height,
            m.Year,
            m.PerformerCount,
            m.PerformerNames,   // <-- included for search
            m.SourceTypes,
            m.OrientationTags,
            m.OtherTags,
            m.StudioName,
            m.UpdatedAt
        })
        .ToListAsync();

    // Client-side tag filtering (SourceTypes / OrientationTags)
    if (srcFilter.Count > 0)
    {
        var set = new HashSet<string>(srcFilter, StringComparer.OrdinalIgnoreCase);
        pre = pre.Where(m => (m.SourceTypes ?? new List<string>()).Any(t => set.Contains(t))).ToList();
    }

    if (oriFilter.Count > 0)
    {
        var set = new HashSet<string>(oriFilter, StringComparer.OrdinalIgnoreCase);
        pre = pre.Where(m => (m.OrientationTags ?? new List<string>()).Any(t => set.Contains(t))).ToList();
    }

    // ---- Free-text search: AND by default; split on " OR " (case-insensitive) to OR clauses
    var clauses = ParseSearch(q);
    if (clauses.Count > 0)
    {
        pre = pre.Where(m =>
        {
            // Build the haystack per item
            var hay = new List<string>(16)
            {
                m.Filename,
                m.StudioName ?? "",
                m.Year?.ToString() ?? ""
            };
            if (m.PerformerNames != null) hay.AddRange(m.PerformerNames.Where(s => !string.IsNullOrWhiteSpace(s)));
            if (m.SourceTypes != null) hay.AddRange(m.SourceTypes.Where(s => !string.IsNullOrWhiteSpace(s)));
            if (m.OrientationTags != null) hay.AddRange(m.OrientationTags.Where(s => !string.IsNullOrWhiteSpace(s)));
            if (m.OtherTags != null) hay.AddRange(m.OtherTags.Where(s => !string.IsNullOrWhiteSpace(s)));

            return MatchesAny(hay, clauses);
        }).ToList();
    }

    // Sorting (stay strongly typed; no dynamic)
    var ordered = sort switch
    {
        "updated_asc" => pre.OrderBy(m => m.UpdatedAt),
        "size_desc" => pre.OrderByDescending(m => m.SizeBytes),
        "size_asc" => pre.OrderBy(m => m.SizeBytes),
        "length_desc" => pre.OrderByDescending(m => m.LengthSeconds ?? -1),
        "length_asc" => pre.OrderBy(m => m.LengthSeconds ?? double.MaxValue),
        "source_asc" => pre.OrderBy(m => FirstOrEmpty(m.SourceTypes)),
        "source_desc" => pre.OrderByDescending(m => FirstOrEmpty(m.SourceTypes)),
        "year_desc" => pre.OrderByDescending(m => m.Year ?? int.MinValue),
        "year_asc" => pre.OrderBy(m => m.Year ?? int.MaxValue),
        _ => pre.OrderByDescending(m => m.UpdatedAt), // updated_desc
    };

    int total = ordered.Count();

    var page = ordered
        .Skip(skip)
        .Take(take)
        .Select(m => new
        {
            m.Id,
            m.Filename,
            m.SizeBytes,
            SizeHuman = HumanSize(m.SizeBytes),
            m.LengthSeconds,
            LengthHuman = (m.LengthSeconds is double ls) ? HumanDuration(ls) : null,
            m.Width,
            m.Height,
            m.Year,
            m.PerformerCount,
            SourceTypes = m.SourceTypes ?? new List<string>(),
            OrientationTags = m.OrientationTags ?? new List<string>(),
            OtherTags = m.OtherTags ?? new List<string>(),
            m.StudioName,
            UpdatedAt = m.UpdatedAt,
            Thumb = $"/api/media/{m.Id}/thumb"
        })
        .ToList();

    return Results.Ok(new { total, items = page });

    // ---- helpers ----
    static List<string> SplitCsv(string? s)
        => string.IsNullOrWhiteSpace(s)
           ? new List<string>()
           : s.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();

    static string FirstOrEmpty(List<string>? xs)
        => (xs != null && xs.Count > 0) ? xs[0] : "";

    static string HumanSize(long bytes)
    {
        double b = bytes;
        string[] u = { "B", "KB", "MB", "GB", "TB" };
        int i = 0;
        while (b >= 1024 && i < u.Length - 1) { b /= 1024; i++; }
        return $"{b:0.##} {u[i]}";
    }

    static string HumanDuration(double seconds)
    {
        var ts = TimeSpan.FromSeconds(seconds);
        return ts.TotalHours >= 1
            ? $"{(int)ts.TotalHours:0}:{ts.Minutes:00}:{ts.Seconds:00}"
            : $"{ts.Minutes:0}:{ts.Seconds:00}";
    }

    // Parse q → OR of AND-clauses (split on " or ")
    static List<List<string>> ParseSearch(string? query)
    {
        var result = new List<List<string>>();
        if (string.IsNullOrWhiteSpace(query)) return result;

        var ors = Regex.Split(query, "\\s+or\\s+", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        foreach (var part in ors)
        {
            var terms = part
                .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .ToList();
            if (terms.Count > 0) result.Add(terms);
        }
        return result;
    }

    // OR across clauses; each clause requires all its terms to match somewhere in the haystack
    static bool MatchesAny(IEnumerable<string> hay, List<List<string>> clauses)
    {
        foreach (var clause in clauses)
        {
            bool all = true;
            foreach (var term in clause)
            {
                bool oneFieldMatches = false;
                foreach (var h in hay)
                {
                    if (!string.IsNullOrEmpty(h) && h.IndexOf(term, StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        oneFieldMatches = true; break;
                    }
                }
                if (!oneFieldMatches) { all = false; break; }
            }
            if (all) return true;
        }
        return false;
    }
});

app.MapHub<ScanHub>("/hubs/scan");
app.MapGet("/api/media/thumb", () => Results.BadRequest("Missing media id. Use /api/media/{id:int}/thumb"));
app.MapGet("/api/media/stream", () => Results.BadRequest("Missing media id. Use /api/media/{id:int}/stream"));
app.MapGet("/api/roots/scan-dry-run", () => Results.BadRequest("Missing root id. Use /api/roots/{id:int}/scan-dry-run"));
app.MapRazorPages();
app.Run();

// ---------- DTOs & helpers ----------
public record RootDto(string Name, string Path);

// New DTO (accepts new + legacy fields)
public record UpdateMediaDto(
    string[]? SourceTypes,
    string? StudioName,
    string? SourceUsername,
    int? Year,
    string[]? OrientationTags,
    string? OtherTags,       // comma-separated
    string? Performers,      // comma-separated
    int? PerformerCount,
    // legacy accepts (ignored if new fields provided)
    string? Orientation,
    string? Type,
    string? Studio,
    string? WebsiteSource
);

static class Proc
{
    public static async Task<(int exitCode, string stdout, string stderr)> RunAsync(string exe, string args, TimeSpan timeout)
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
            try { proc.Kill(entireProcessTree: true); } catch { }
            return (-1, "", "Timed out");
        }

        var stdout = await stdoutTask;
        var stderr = await stderrTask;
        return (proc.ExitCode, stdout, stderr);
    }
}

static class ProbeParser
{
    public sealed record Result(
        double? LengthSeconds,
        long? OverallBitrate,
        int? Width,
        int? Height,
        double? FrameRate,
        string? VideoCodec,
        string? AudioCodec,
        long? AudioBitrate,
        int? AudioChannels
    );

    public static Result Parse(string json)
    {
        using var doc = JsonDocument.Parse(json);

        double? length = TryGetDouble(doc.RootElement, "format", "duration");
        long? overall = TryGetLong(doc.RootElement, "format", "bit_rate");

        int? width = null, height = null;
        double? fps = null;
        string? vcodec = null;

        string? acodec = null;
        long? abitrate = null;
        int? achannels = null;

        if (doc.RootElement.TryGetProperty("streams", out var streams) && streams.ValueKind == JsonValueKind.Array)
        {
            foreach (var s in streams.EnumerateArray())
            {
                if (!s.TryGetProperty("codec_type", out var ct)) continue;
                var type = ct.GetString();

                if (type == "video" && vcodec is null)
                {
                    vcodec = s.TryGetProperty("codec_name", out var cn) ? cn.GetString() : null;
                    width = s.TryGetProperty("width", out var w) ? TryGetInt(w) : null;
                    height = s.TryGetProperty("height", out var h) ? TryGetInt(h) : null;

                    string? rate = null;
                    if (s.TryGetProperty("avg_frame_rate", out var afr)) rate = afr.GetString();
                    else if (s.TryGetProperty("r_frame_rate", out var rfr)) rate = rfr.GetString();
                    fps = ParseRate(rate);
                }
                else if (type == "audio" && acodec is null)
                {
                    acodec = s.TryGetProperty("codec_name", out var an) ? an.GetString() : null;
                    abitrate = s.TryGetProperty("bit_rate", out var abr) ? TryGetLong(abr) : null;
                    achannels = s.TryGetProperty("channels", out var ch) ? TryGetInt(ch) : null;
                }
            }
        }

        return new Result(length, overall, width, height, fps, vcodec, acodec, abitrate, achannels);
    }

    static double? TryGetDouble(JsonElement root, string objName, string propName)
        => root.TryGetProperty(objName, out var obj) && obj.TryGetProperty(propName, out var val)
           ? TryGetDouble(val) : null;

    static long? TryGetLong(JsonElement root, string objName, string propName)
        => root.TryGetProperty(objName, out var obj) && obj.TryGetProperty(propName, out var val)
           ? TryGetLong(val) : null;

    static int? TryGetInt(JsonElement e)
    {
        return e.ValueKind switch
        {
            JsonValueKind.String => int.TryParse(e.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var i) ? i : null,
            JsonValueKind.Number => e.TryGetInt32(out var i) ? i : null,
            _ => null
        };
    }

    static long? TryGetLong(JsonElement e)
    {
        return e.ValueKind switch
        {
            JsonValueKind.String => long.TryParse(e.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var l) ? l : null,
            JsonValueKind.Number => e.TryGetInt64(out var l) ? l : null,
            _ => null
        };
    }

    static double? TryGetDouble(JsonElement e)
    {
        return e.ValueKind switch
        {
            JsonValueKind.String => double.TryParse(e.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? d : null,
            JsonValueKind.Number => e.TryGetDouble(out var d) ? d : null,
            _ => null
        };
    }

    static double? ParseRate(string? fraction)
    {
        if (string.IsNullOrWhiteSpace(fraction)) return null;
        var slash = fraction.IndexOf('/');
        if (slash <= 0) return double.TryParse(fraction, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? d : null;

        var numStr = fraction[..slash];
        var denStr = fraction[(slash + 1)..];
        if (!double.TryParse(numStr, NumberStyles.Float, CultureInfo.InvariantCulture, out var num)) return null;
        if (!double.TryParse(denStr, NumberStyles.Float, CultureInfo.InvariantCulture, out var den)) return null;
        if (den == 0) return null;
        return num / den;
    }
}

static class Indexer
{
    private static readonly HashSet<string> _allowedExt = new(StringComparer.OrdinalIgnoreCase)
    { "mp4","mkv","mov","avi","wmv","webm","flv","mpg","mpeg","ogv","ts","3gp" };

    public static async Task<(bool ok, object result, string? err)> IndexOneAsync(VideoIndexDbContext db, string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return (false, null!, "Query ?path=... is required.");
        path = Path.GetFullPath(path);
        if (!File.Exists(path)) return (false, null!, $"File not found: {path}");

        var ext = Path.GetExtension(path);
        var extNoDot = string.IsNullOrEmpty(ext) ? "" : (ext[0] == '.' ? ext[1..] : ext);
        if (!_allowedExt.Contains(extNoDot)) return (false, null!, $"Unsupported extension: {extNoDot}");

        var fi = new FileInfo(path);
        if (fi.Length == 0) return (false, null!, "Zero-byte file; skipping.");

        var roots = await db.ScanRoots.AsNoTracking().ToListAsync();
        var owner = roots
            .Where(r => path.StartsWith(r.Path, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(r => r.Path.Length)
            .FirstOrDefault();
        if (owner == null) return (false, null!, "File is not under any registered scan root.");

        string sha256;
        using (var fs = File.OpenRead(path))
        using (var sha = SHA256.Create())
            sha256 = Convert.ToHexString(await sha.ComputeHashAsync(fs)).ToLowerInvariant();

        var filename = fi.Name;
        var sizeBytes = fi.Length;

        var (meta, probeErr) = await ProbeForMetaAsync(path);
        if (probeErr != null) return (false, null!, probeErr);
        var now = DateTimeOffset.UtcNow;

        var existing = await db.MediaFiles.SingleOrDefaultAsync(m => m.Path == path);
        if (existing == null)
        {
            var mf = new MediaFile
            {
                Sha256 = sha256,
                RootId = owner.Id,
                Filename = filename,
                Extension = extNoDot.ToLowerInvariant(),
                Path = path,
                SizeBytes = sizeBytes,
                LengthSeconds = meta!.LengthSeconds,
                Height = meta!.Height,
                Width = meta!.Width,
                BitRate = meta!.OverallBitrate,
                FrameRate = meta!.FrameRate,
                VideoFormat = meta!.VideoCodec,
                AudioFormat = meta!.AudioCodec,
                AudioBitrate = meta!.AudioBitrate,
                AudioChannels = meta!.AudioChannels,
                PerformerNames = null,
                PerformerCount = null,
                CreatedAt = now,
                UpdatedAt = now
            };
            db.MediaFiles.Add(mf);
            await db.SaveChangesAsync();
            return (true, new { created = true, mf.Id, mf.Path, mf.Filename, mf.Sha256 }, null);
        }
        else
        {
            existing.Sha256 = sha256;
            existing.RootId = owner.Id;
            existing.Filename = filename;
            existing.Extension = extNoDot.ToLowerInvariant();
            existing.SizeBytes = sizeBytes;
            existing.LengthSeconds = meta!.LengthSeconds;
            existing.Height = meta!.Height;
            existing.Width = meta!.Width;
            existing.BitRate = meta!.OverallBitrate;
            existing.FrameRate = meta!.FrameRate;
            existing.VideoFormat = meta!.VideoCodec;
            existing.AudioFormat = meta!.AudioCodec;
            existing.AudioBitrate = meta!.AudioBitrate;
            existing.AudioChannels = meta!.AudioChannels;
            existing.UpdatedAt = now;
            await db.SaveChangesAsync();
            return (true, new { created = false, existing.Id, existing.Path, existing.Filename, existing.Sha256 }, null);
        }
    }

    public static async Task<(bool ok, object result, string? err)> IndexOneWithFactoryAsync(
        IDbContextFactory<VideoIndexDbContext> factory, string path)
    {
        await using var db = await factory.CreateDbContextAsync();
        return await IndexOneAsync(db, path);
    }

    public static async Task<(bool ok, byte[]? jpeg, string? err)> GenerateThumbnailWithFactoryAsync(
        IDbContextFactory<VideoIndexDbContext> factory, string path, int lumaThreshold, int maxTries, int width, int jpegQuality, bool onlyIfMissing)
    {
        await using var db = await factory.CreateDbContextAsync();

        var mf = await db.MediaFiles.Include(m => m.Thumbnails).SingleOrDefaultAsync(m => m.Path == path);
        if (onlyIfMissing && mf != null && mf.Thumbnails.Any())
            return (false, null, "skip");

        return await GenerateThumbnailAsync(db, path, lumaThreshold, maxTries, width, jpegQuality);
    }

    public static async Task<(bool ok, byte[]? jpeg, string? err)> GenerateThumbnailAsync(
        VideoIndexDbContext db, string path, int lumaThreshold, int maxTries, int width, int jpegQuality)
    {
        if (string.IsNullOrWhiteSpace(path)) return (false, null, "Query ?path=... is required.");
        path = Path.GetFullPath(path);
        if (!File.Exists(path)) return (false, null, $"File not found: {path}");

        var mf = await db.MediaFiles.Include(m => m.Thumbnails).SingleOrDefaultAsync(m => m.Path == path);
        if (mf == null) return (false, null, "File is not indexed yet. Call /api/debug/index-one first.");

        var duration = mf.LengthSeconds;
        if (duration is null || duration <= 0)
        {
            var (meta, err) = await ProbeForMetaAsync(path);
            if (err != null) return (false, null, err);
            duration = meta!.LengthSeconds;
            if (duration is null || duration <= 0) return (false, null, "Could not determine duration.");
            mf.LengthSeconds = duration;
            await db.SaveChangesAsync();
        }

        var start = duration.Value * 0.25;
        var end = duration.Value * 0.50;
        if (end <= start) end = Math.Max(start + 1, start * 1.25);

        byte[]? chosen = null;
        var rng = new Random();

        for (int i = 0; i < maxTries; i++)
        {
            var t = start + rng.NextDouble() * (end - start);
            var (ok, jpeg, avg, err) = await TryMakeThumbAsync(path, t, width, jpegQuality);
            if (!ok)
            {
                if (i == maxTries - 1) return (false, null, err ?? "ffmpeg failed creating thumbnail.");
                continue;
            }

            if (avg >= lumaThreshold || i == maxTries - 1)
            {
                chosen = jpeg;
                break;
            }
        }

        if (chosen == null) return (false, null, "Failed to generate thumbnail.");

        if (mf.Thumbnails.Count > 0) db.Thumbnails.RemoveRange(mf.Thumbnails);
        db.Thumbnails.Add(new Thumbnail
        {
            MediaFileId = mf.Id,
            Jpeg = chosen,
            GeneratedAt = DateTimeOffset.UtcNow
        });
        await db.SaveChangesAsync();

        return (true, chosen, null);
    }

    private static async Task<(bool ok, byte[]? jpeg, double avgLuma, string? err)> TryMakeThumbAsync(
        string videoPath, double tSeconds, int width, int jpegQuality)
    {
        var tmp = Path.Combine(Path.GetTempPath(), $"thumb_{Guid.NewGuid():N}.jpg");
        try
        {
            var ts = tSeconds.ToString("0.###", CultureInfo.InvariantCulture);
            var q = Math.Clamp(jpegQuality / 5, 1, 5);
            var args = $"-y -ss {ts} -i \"{videoPath}\" -frames:v 1 -vf \"scale={width}:-1:flags=lanczos\" -q:v {q} \"{tmp}\"";
            var (code, _, stderr) = await Proc.RunAsync("ffmpeg", args, TimeSpan.FromSeconds(20));
            if (code != 0 || !File.Exists(tmp))
                return (false, null, 0, $"ffmpeg failed (code {code}). {stderr}");

            using var bmp = new Bitmap(tmp);
            double sum = 0; long count = 0;
            for (int y = 0; y < bmp.Height; y++)
                for (int x = 0; x < bmp.Width; x++)
                {
                    var c = bmp.GetPixel(x, y);
                    var l = 0.2126 * c.R + 0.7152 * c.G + 0.0722 * c.B;
                    sum += l; count++;
                }
            var avg = count > 0 ? sum / count : 0.0;
            var bytes = await File.ReadAllBytesAsync(tmp);
            return (true, bytes, avg, null);
        }
        catch (Exception ex) { return (false, null, 0, ex.Message); }
        finally { try { if (File.Exists(tmp)) File.Delete(tmp); } catch { } }
    }

    private static async Task<(ProbeParser.Result? meta, string? err)> ProbeForMetaAsync(string path)
    {
        var (code, stdout, stderr) = await Proc.RunAsync(
            "ffprobe",
            $"-v error -hide_banner -print_format json -show_format -show_streams \"{path}\"",
            TimeSpan.FromSeconds(30)
        );
        if (code != 0) return (null, $"ffprobe failed (code {code}). {stderr}");
        return (ProbeParser.Parse(stdout), null);
    }
}
