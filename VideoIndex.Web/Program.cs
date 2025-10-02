using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc;
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
app.UseStaticFiles();
app.UseRouting();

app.MapGet("/", () => Results.Redirect("/library", permanent: false));

// --- API Endpoints ---

// Roots
app.MapGet("/api/roots", async (IDbContextFactory<VideoIndexDbContext> dbFactory) =>
{
    await using var db = await dbFactory.CreateDbContextAsync();
    return await db.ScanRoots.OrderBy(r => r.Id).ToListAsync();
});

app.MapPost("/api/roots", async (IDbContextFactory<VideoIndexDbContext> dbFactory, RootDto dto) =>
{
    if (string.IsNullOrWhiteSpace(dto.Name) || string.IsNullOrWhiteSpace(dto.Path)) return Results.BadRequest("Name and Path are required.");
    if (!Directory.Exists(dto.Path)) return Results.BadRequest($"Path does not exist: {dto.Path}");
    await using var db = await dbFactory.CreateDbContextAsync();
    var root = new ScanRoot { Name = dto.Name.Trim(), Path = dto.Path.Trim(), LastScannedAt = null };
    db.ScanRoots.Add(root);
    await db.SaveChangesAsync();
    return Results.Created($"/api/roots/{root.Id}", root);
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

// Scanning & Maintenance
app.MapGet("/api/roots/{id:int}/scan-dry-run", async (int id, IDbContextFactory<VideoIndexDbContext> dbFactory) =>
{
    await using var db = await dbFactory.CreateDbContextAsync();
    var root = await db.ScanRoots.FindAsync(id);
    if (root is null) return Results.NotFound($"Root {id} not found.");
    if (!Directory.Exists(root.Path)) return Results.BadRequest($"Path does not exist: {root.Path}");
    var allowedExt = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "mp4", "mkv", "mov", "avi", "wmv", "webm", "flv", "mpg", "mpeg", "ogv", "ts", "3gp" };
    var sw = Stopwatch.StartNew();
    var sample = new List<string>(capacity: 10);
    var count = 0;
    foreach (var file in Directory.EnumerateFiles(root.Path, "*.*", SearchOption.AllDirectories)) { if (allowedExt.Contains(Path.GetExtension(file).TrimStart('.'))) { count++; if (sample.Count < 10) sample.Add(Path.GetRelativePath(root.Path, file)); } }
    sw.Stop();
    return Results.Ok(new { root = new { root.Id, root.Name, root.Path }, discovered = count, sample, elapsedMs = sw.ElapsedMilliseconds });
});

app.MapPost("/api/roots/{id:int}/scan-live", async (int id, string scanId, bool? newFilesOnly, bool? autoTag, bool? thumbs, bool? regenAll, int? dop, IHubContext<ScanHub> hub, IDbContextFactory<VideoIndexDbContext> dbFactory) =>
{
    await using var db0 = await dbFactory.CreateDbContextAsync();
    var root = await db0.ScanRoots.FindAsync(id);
    if (root is null) return Results.NotFound($"Root {id} not found.");
    if (!Directory.Exists(root.Path)) return Results.BadRequest($"Path does not exist: {root.Path}");
    if (string.IsNullOrWhiteSpace(scanId)) return Results.BadRequest("scanId is required.");
    var cts = new CancellationTokenSource();
    if (!ScanManager.ActiveScans.TryAdd(scanId, cts)) { return Results.Conflict("A scan with this ID is already in progress."); }
    try
    {
        bool doNewFilesOnly = newFilesOnly.GetValueOrDefault(true), doAutoTag = autoTag.GetValueOrDefault(true); int degree = Math.Clamp(dop ?? Environment.ProcessorCount, 1, 24);
        var allowedExt = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "mp4", "mkv", "mov", "avi", "wmv", "webm", "flv", "mpg", "mpeg", "ogv", "ts", "3gp" };
        var allDiskFiles = Directory.EnumerateFiles(root.Path, "*.*", SearchOption.AllDirectories).Where(f => allowedExt.Contains(Path.GetExtension(f).TrimStart('.'))).Select(Path.GetFullPath).ToList();
        List<string> filesToProcess;
        if (doNewFilesOnly) { var existingPaths = (await db0.MediaFiles.AsNoTracking().Where(m => m.RootId == id).Select(m => m.Path).ToListAsync()).ToHashSet(StringComparer.OrdinalIgnoreCase); filesToProcess = allDiskFiles.Where(f => !existingPaths.Contains(f)).ToList(); }
        else { filesToProcess = allDiskFiles; }
        int total = filesToProcess.Count, done = 0, indexed = 0, tagged = 0, errors = 0;
        await hub.Clients.Group($"scan:{scanId}").SendAsync("progress", new { kind = "started", root = new { root.Id, root.Name, root.Path }, totals = new { files = total, dop = degree }, startedAt = DateTimeOffset.UtcNow });
        var sw = Stopwatch.StartNew();
        await Parallel.ForEachAsync(filesToProcess, new ParallelOptions { MaxDegreeOfParallelism = degree, CancellationToken = cts.Token }, async (f, ct) =>
        {
            var (ok, result, err) = await Indexer.IndexOneWithFactoryAsync(dbFactory, f);
            if (ok) { Interlocked.Increment(ref indexed); if (doAutoTag) { await using var dbTag = await dbFactory.CreateDbContextAsync(); var mediaFile = await dbTag.MediaFiles.FindAsync(((dynamic)result).Id); if (mediaFile != null && AutoTagger.Apply(mediaFile, dryRun: false)) { await dbTag.SaveChangesAsync(ct); Interlocked.Increment(ref tagged); } } }
            else { Interlocked.Increment(ref errors); await hub.Clients.Group($"scan:{scanId}").SendAsync("progress", new { kind = "indexError", file = Path.GetRelativePath(root.Path, f), error = err }); }
            var curDone = Interlocked.Increment(ref done);
            if (curDone % 10 == 0) { await hub.Clients.Group($"scan:{scanId}").SendAsync("progress", new { kind = "heartbeat", processed = curDone, indexed, tagged, errors, elapsedMs = sw.ElapsedMilliseconds }); }
        });
        sw.Stop();
        await hub.Clients.Group($"scan:{scanId}").SendAsync("progress", new { kind = "completed", processed = done, indexed, tagged, errors, elapsedMs = sw.ElapsedMilliseconds });
    }
    catch (OperationCanceledException) { await hub.Clients.Group($"scan:{scanId}").SendAsync("progress", new { kind = "cancelled" }); }
    finally { ScanManager.ActiveScans.TryRemove(scanId, out _); }
    return Results.Ok();
});

app.MapPost("/api/scan/{scanId}/stop", (string scanId) =>
{
    if (ScanManager.ActiveScans.TryGetValue(scanId, out var cts)) { cts.Cancel(); return Results.Ok(new { message = "Scan cancellation requested." }); }
    return Results.NotFound(new { message = "Scan ID not found or already completed." });
});

app.MapPost("/api/media/generate-missing-thumbnails", (IHubContext<ScanHub> hub, IDbContextFactory<VideoIndexDbContext> dbFactory) =>
{
    string scanId = Guid.NewGuid().ToString();
    _ = Task.Run(async () =>
    {
        var cts = new CancellationTokenSource();
        if (!ScanManager.ActiveScans.TryAdd(scanId, cts)) return;
        try
        {
            await using var db = await dbFactory.CreateDbContextAsync();
            var filesMissingThumbs = await db.MediaFiles.AsNoTracking().Where(m => !m.Thumbnails.Any()).Select(m => m.Path).ToListAsync(cts.Token);
            int total = filesMissingThumbs.Count, done = 0, thumbsMade = 0;
            await hub.Clients.Group($"scan:{scanId}").SendAsync("progress", new { kind = "started", root = new { Name = "Entire Library" }, totals = new { files = total } });
            await Parallel.ForEachAsync(filesMissingThumbs, new ParallelOptions { MaxDegreeOfParallelism = Environment.ProcessorCount, CancellationToken = cts.Token }, async (path, token) =>
            {
                var (thumbOk, _, _) = await Indexer.GenerateThumbnailWithFactoryAsync(dbFactory, path, 28, 5, 320, 85, onlyIfMissing: true);
                if (thumbOk) Interlocked.Increment(ref thumbsMade);
                Interlocked.Increment(ref done);
                await hub.Clients.Group($"scan:{scanId}").SendAsync("progress", new { kind = "heartbeat", processed = done, thumbsMade });
            });
            await hub.Clients.Group($"scan:{scanId}").SendAsync("progress", new { kind = "completed", processed = done, thumbsMade, elapsedMs = 0 });
        }
        catch (OperationCanceledException) { await hub.Clients.Group($"scan:{scanId}").SendAsync("progress", new { kind = "cancelled" }); }
        finally { ScanManager.ActiveScans.TryRemove(scanId, out _); }
    });
    return Results.Ok(new { scanId });
});

app.MapGet("/api/media/duplicates", async (IDbContextFactory<VideoIndexDbContext> dbFactory) =>
{
    static string HumanSize(long bytes) { double b = bytes; string[] u = { "B", "KB", "MB", "GB", "TB" }; int i = 0; while (b >= 1024 && i < u.Length - 1) { b /= 1024; i++; } return $"{b:0.##} {u[i]}"; }
    await using var db = await dbFactory.CreateDbContextAsync();
    var rawDuplicates = await db.MediaFiles.AsNoTracking().GroupBy(m => m.Sha256).Where(g => g.Count() > 1).Select(g => new { Sha256 = g.Key, SizeBytes = g.First().SizeBytes, Count = g.Count(), Files = g.Select(m => new { m.Id, m.Path, m.Filename }).ToList() }).ToListAsync();
    var duplicates = rawDuplicates.Select(g => new { g.Sha256, SizeHuman = HumanSize(g.SizeBytes), g.Count, g.Files }).OrderByDescending(x => x.Count).ThenBy(x => x.Sha256).ToList();
    return Results.Ok(duplicates);
});

app.MapDelete("/api/media/batch-delete", async ([FromBody] BatchDeleteDto dto, [FromServices] IDbContextFactory<VideoIndexDbContext> dbFactory) =>
{
    if (dto?.Ids == null || dto.Ids.Count == 0) return Results.BadRequest("No file IDs provided.");
    await using var db = await dbFactory.CreateDbContextAsync();
    var mediaFiles = await db.MediaFiles.Where(m => dto.Ids.Contains(m.Id)).ToListAsync();
    if (mediaFiles.Count == 0) return Results.NotFound("None of the provided file IDs were found.");

    if (dto.DeleteFile == true)
    {
        foreach (var mediaFile in mediaFiles)
        {
            try { if (File.Exists(mediaFile.Path)) { File.Delete(mediaFile.Path); } }
            catch (Exception ex) { Console.WriteLine($"Error deleting file {mediaFile.Path}: {ex.Message}"); }
        }
    }

    db.MediaFiles.RemoveRange(mediaFiles);
    await db.SaveChangesAsync();
    return Results.Ok(new { deletedCount = mediaFiles.Count, ids = mediaFiles.Select(m => m.Id) });
});

// Media Endpoints
app.MapGet("/api/media/browse", async (HttpRequest req, IDbContextFactory<VideoIndexDbContext> factory) =>
{
    int skip = int.TryParse(req.Query["skip"], out var sk) ? Math.Max(0, sk) : 0;
    int take = int.TryParse(req.Query["take"], out var tk) ? Math.Clamp(tk, 1, 200) : 36;
    string sort = string.IsNullOrWhiteSpace(req.Query["sort"]) ? "updated_desc" : req.Query["sort"].ToString().ToLowerInvariant();
    bool getRandom = req.Query.ContainsKey("random");
    List<string> srcFilter = SplitCsv(req.Query["source"].ToString());
    string sourceLogic = req.Query["sourceLogic"].ToString()?.ToUpperInvariant() ?? "OR";
    List<string> oriFilter = SplitCsv(req.Query["orientation"].ToString());
    string? q = req.Query["q"];
    int? minPerf = int.TryParse(req.Query["minPerformers"], out var mp) ? mp : null;
    int? maxPerf = int.TryParse(req.Query["maxPerformers"], out var xp) ? xp : null;
    string? dir = req.Query["dir"];
    bool recursive = bool.TryParse(req.Query["recursive"], out var rec) && rec;
    bool favouritesOnly = bool.TryParse(req.Query["favouritesOnly"], out var fav) && fav; // New favourite filter
    string? normDir = string.IsNullOrWhiteSpace(dir) ? null : Path.GetFullPath(dir).TrimEnd('\\', '/');
    string? prefix = normDir is null ? null : normDir + Path.DirectorySeparatorChar;
    await using var db = await factory.CreateDbContextAsync();
    IQueryable<MediaFile> qEF = db.MediaFiles.AsNoTracking();
    if (minPerf.HasValue) qEF = qEF.Where(m => m.PerformerCount != null && m.PerformerCount >= minPerf.Value);
    if (maxPerf.HasValue) qEF = qEF.Where(m => m.PerformerCount != null && m.PerformerCount <= maxPerf.Value);
    if (prefix != null) qEF = qEF.Where(m => m.Path.StartsWith(prefix));
    if (favouritesOnly) qEF = qEF.Where(m => m.IsFavourite); // Filter by favourite
    qEF = qEF.OrderByDescending(m => m.Id);
    var pre = await qEF.Select(m => new { m.Id, m.Filename, m.Title, m.Path, m.SizeBytes, m.LengthSeconds, m.Width, m.Height, m.Year, m.PerformerCount, m.PerformerNames, m.SourceTypes, m.OrientationTags, m.OtherTags, m.StudioName, m.SourceUsername, RootName = m.Root != null ? m.Root.Name : null, m.UpdatedAt }).ToListAsync();
    if (normDir != null && !recursive) { pre = pre.Where(m => { string? parent = null; try { parent = Path.GetDirectoryName(m.Path)?.TrimEnd('\\', '/'); } catch { } return parent != null && parent.Equals(normDir, StringComparison.OrdinalIgnoreCase); }).ToList(); }
    if (srcFilter.Count > 0) { var set = new HashSet<string>(srcFilter, StringComparer.OrdinalIgnoreCase); if (sourceLogic == "AND") pre = pre.Where(m => set.All(filterTag => (m.SourceTypes ?? new List<string>()).Contains(filterTag, StringComparer.OrdinalIgnoreCase))).ToList(); else pre = pre.Where(m => (m.SourceTypes ?? new List<string>()).Any(t => set.Contains(t))).ToList(); }
    if (oriFilter.Count > 0) { var set = new HashSet<string>(oriFilter, StringComparer.OrdinalIgnoreCase); pre = pre.Where(m => (m.OrientationTags ?? new List<string>()).Any(t => set.Contains(t))).ToList(); }
    var clauses = ParseSearch(q);
    if (clauses.Count > 0) { pre = pre.Where(m => { var hay = new List<string>(32) { m.Filename, m.Title ?? "", m.StudioName ?? "", m.SourceUsername ?? "", m.Year?.ToString() ?? "", m.Path ?? "" }; if (m.PerformerNames != null) hay.AddRange(m.PerformerNames.Where(s => !string.IsNullOrWhiteSpace(s))); if (m.SourceTypes != null) hay.AddRange(m.SourceTypes.Where(s => !string.IsNullOrWhiteSpace(s))); if (m.OrientationTags != null) hay.AddRange(m.OrientationTags.Where(s => !string.IsNullOrWhiteSpace(s))); if (m.OtherTags != null) hay.AddRange(m.OtherTags.Where(s => !string.IsNullOrWhiteSpace(s))); hay.AddRange(PathTokens(m.Path)); return MatchesAny(hay, clauses); }).ToList(); }
    var ordered = sort switch { "updated_asc" => pre.OrderBy(m => m.UpdatedAt), "size_desc" => pre.OrderByDescending(m => m.SizeBytes), "size_asc" => pre.OrderBy(m => m.SizeBytes), "length_desc" => pre.OrderByDescending(m => m.LengthSeconds ?? -1), "length_asc" => pre.OrderBy(m => m.LengthSeconds ?? double.MaxValue), _ => pre.OrderByDescending(m => m.UpdatedAt), };
    var orderedList = ordered.ToList(); int total = orderedList.Count;
    if (getRandom) { if (total == 0) return Results.NotFound(); int randomSkip = new Random().Next(0, total); var randomItem = orderedList.Skip(randomSkip).FirstOrDefault(); return Results.Ok(new { id = randomItem?.Id }); }
    var page = orderedList.Skip(skip).Take(take).Select(m => new { m.Id, m.Filename, m.Title, m.SizeBytes, SizeHuman = HumanSize(m.SizeBytes), m.LengthSeconds, LengthHuman = (m.LengthSeconds is double ls) ? HumanDuration(ls) : null, m.Width, m.Height, m.Year, m.PerformerCount, SourceTypes = m.SourceTypes ?? new List<string>(), OrientationTags = m.OrientationTags ?? new List<string>(), OtherTags = m.OtherTags ?? new List<string>(), m.StudioName, m.UpdatedAt, Thumb = $"/api/media/{m.Id}/thumb" }).ToList();
    return Results.Ok(new { total, items = page });
    static List<string> SplitCsv(string? s) => string.IsNullOrWhiteSpace(s) ? new List<string>() : s.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
    static IEnumerable<string> PathTokens(string? path) { if (string.IsNullOrWhiteSpace(path)) yield break; foreach (var p in Regex.Split(path, "[\\\\/]+").Where(p => !string.IsNullOrWhiteSpace(p))) yield return p; }
    static string HumanSize(long bytes) { double b = bytes; string[] u = { "B", "KB", "MB", "GB", "TB" }; int i = 0; while (b >= 1024 && i < u.Length - 1) { b /= 1024; i++; } return $"{b:0.##} {u[i]}"; }
    static string HumanDuration(double seconds) { var ts = TimeSpan.FromSeconds(seconds); return ts.TotalHours >= 1 ? $"{(int)ts.TotalHours:0}:{ts.Minutes:00}:{ts.Seconds:00}" : $"{ts.Minutes:0}:{ts.Seconds:00}"; }
    static List<List<string>> ParseSearch(string? query) { var result = new List<List<string>>(); if (string.IsNullOrWhiteSpace(query)) return result; foreach (var part in Regex.Split(query, "\\s+or\\s+", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)) { var terms = part.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList(); if (terms.Count > 0) result.Add(terms); } return result; }
    static bool MatchesAny(IEnumerable<string> hay, List<List<string>> clauses) { foreach (var clause in clauses) { bool all = true; foreach (var term in clause) { bool oneFieldMatches = false; foreach (var h in hay) { if (!string.IsNullOrEmpty(h) && h.IndexOf(term, StringComparison.OrdinalIgnoreCase) >= 0) { oneFieldMatches = true; break; } } if (!oneFieldMatches) { all = false; break; } } if (all) return true; } return false; }
});

app.MapGet("/api/media/nav", async (IDbContextFactory<VideoIndexDbContext> dbFactory, int id, bool? untaggedOnly, bool? taggedOnly, string? dir, bool? recursive) =>
{
    await using var db = await dbFactory.CreateDbContextAsync();
    bool wantUntagged = untaggedOnly == true, wantTagged = taggedOnly == true;
    if (wantUntagged && wantTagged) { wantUntagged = false; wantTagged = false; }
    string? normDir = null; if (!string.IsNullOrWhiteSpace(dir)) { try { normDir = Path.GetFullPath(dir.Trim()).Replace('/', Path.DirectorySeparatorChar).TrimEnd(Path.DirectorySeparatorChar); } catch { normDir = dir.Trim().Replace('/', Path.DirectorySeparatorChar).TrimEnd(Path.DirectorySeparatorChar); } }
    bool useDirFilter = !string.IsNullOrEmpty(normDir), recurseDirs = recursive == true;
    if (!useDirFilter && !wantUntagged && !wantTagged) { var idsAll = await db.MediaFiles.AsNoTracking().OrderBy(m => m.Id).Select(m => m.Id).ToListAsync(); return Results.Ok(ComputeNeighbors(idsAll, id)); }
    var rows = await db.MediaFiles.AsNoTracking().OrderBy(m => m.Id).Select(m => new { m.Id, m.Path, m.SourceTypes, m.OrientationTags, m.OtherTags, m.PerformerNames, m.PerformerCount, m.Year, m.StudioName, m.SourceUsername }).ToListAsync();
    static bool IsUntagged(dynamic m) => (m.SourceTypes == null || m.SourceTypes.Count == 0) && (m.OrientationTags == null || m.OrientationTags.Count == 0) && (m.OtherTags == null || m.OtherTags.Count == 0) && (m.PerformerNames == null || m.PerformerNames.Count == 0) && m.PerformerCount == null && m.Year == null && string.IsNullOrEmpty(m.StudioName) && string.IsNullOrEmpty(m.SourceUsername);
    bool DirMatch(string filePath) { if (string.IsNullOrEmpty(normDir)) return true; var full = filePath?.Replace('/', Path.DirectorySeparatorChar) ?? ""; var prefix = normDir + Path.DirectorySeparatorChar; if (!full.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return false; if (recurseDirs) return true; string? parent; try { parent = Path.GetDirectoryName(full)?.TrimEnd(Path.DirectorySeparatorChar); } catch { parent = null; } return parent != null && parent.Equals(normDir, StringComparison.OrdinalIgnoreCase); }
    var filtered = rows.Where(m => DirMatch(m.Path) && (!wantUntagged || IsUntagged(m)) && (!wantTagged || !IsUntagged(m)));
    var ids = filtered.Select(m => (int)m.Id).ToList(); return Results.Ok(ComputeNeighbors(ids, id));
    static object ComputeNeighbors(List<int> ids, int currentId) { if (ids.Count == 0) return new { prevId = (int?)null, nextId = (int?)null }; var idx = ids.IndexOf(currentId); int? prevId, nextId; if (idx >= 0) { prevId = idx > 0 ? ids[idx - 1] : (int?)null; nextId = idx < ids.Count - 1 ? ids[idx + 1] : (int?)null; } else { prevId = ids.Where(x => x < currentId).Cast<int?>().DefaultIfEmpty(null).Max(); nextId = ids.Where(x => x > currentId).Cast<int?>().DefaultIfEmpty(null).Min(); } return new { prevId, nextId }; }
});

app.MapGet("/api/meta/distinct", async (IDbContextFactory<VideoIndexDbContext> dbFactory, string field, int? take) =>
{
    int top = Math.Clamp(take ?? 50, 1, 200);
    string key = field?.Trim() ?? "";
    if (string.IsNullOrEmpty(key)) return Results.BadRequest("field is required.");
    await using var db = await dbFactory.CreateDbContextAsync();
    async Task<IResult> GetDistinctListValues(IQueryable<List<string>?> query)
    {
        var lists = await query.ToListAsync();
        var dict = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var l in lists) { if (l is null) continue; foreach (var v in l) { var s = v?.Trim(); if (string.IsNullOrEmpty(s)) continue; dict[s] = dict.TryGetValue(s, out var c) ? c + 1 : 1; } }
        var items = dict.OrderByDescending(kv => kv.Value).ThenBy(kv => kv.Key).Take(top).Select(kv => new { value = kv.Key, count = kv.Value });
        return Results.Ok(items);
    }
    switch (key.ToLowerInvariant())
    {
        case "type": return await GetDistinctListValues(db.MediaFiles.AsNoTracking().Select(m => m.SourceTypes));
        case "studio": { var items = await db.MediaFiles.AsNoTracking().Where(m => m.StudioName != null && m.StudioName != "").GroupBy(m => m.StudioName!).Select(g => new { value = g.Key, count = g.Count() }).OrderByDescending(x => x.count).ThenBy(x => x.value).Take(top).ToListAsync(); return Results.Ok(items); }
        case "orientation": return await GetDistinctListValues(db.MediaFiles.AsNoTracking().Select(m => m.OrientationTags));
        case "performers": return await GetDistinctListValues(db.MediaFiles.AsNoTracking().Select(m => m.PerformerNames));
        case "other": return await GetDistinctListValues(db.MediaFiles.AsNoTracking().Select(m => m.OtherTags));
        default: return Results.BadRequest("Unknown field. Supported fields: type, studio, orientation, performers, other");
    }
});

app.MapPost("/api/media/{id:int}/open", async (int id, IDbContextFactory<VideoIndexDbContext> dbFactory) =>
{
    await using var db = await dbFactory.CreateDbContextAsync();
    var path = await db.MediaFiles.AsNoTracking().Where(m => m.Id == id).Select(m => m.Path).SingleOrDefaultAsync();
    if (path == null || !File.Exists(path)) return Results.NotFound("File not found on disk.");
    try { Process.Start(new ProcessStartInfo { FileName = path, UseShellExecute = true }); return Results.Ok(new { launched = true }); }
    catch (Exception ex) { return Results.Problem(ex.Message); }
});

app.MapPost("/api/media/{id:int}/reveal", async (int id, IDbContextFactory<VideoIndexDbContext> dbFactory) =>
{
    await using var db = await dbFactory.CreateDbContextAsync();
    var path = await db.MediaFiles.AsNoTracking().Where(m => m.Id == id).Select(m => m.Path).SingleOrDefaultAsync();
    if (path == null || !File.Exists(path)) return Results.NotFound("File not found on disk.");
    try { Process.Start(new ProcessStartInfo { FileName = "explorer.exe", Arguments = $"/select,\"{path}\"", UseShellExecute = true }); return Results.Ok(new { revealed = true }); }
    catch (Exception ex) { return Results.Problem(ex.Message); }
});

app.MapGet("/api/media/{id:int}/stream", async (int id, IDbContextFactory<VideoIndexDbContext> dbFactory) =>
{
    await using var db = await dbFactory.CreateDbContextAsync();
    var path = await db.MediaFiles.AsNoTracking().Where(m => m.Id == id).Select(m => m.Path).SingleOrDefaultAsync();
    if (path == null || !File.Exists(path)) return Results.NotFound("File not found on disk.");
    string ext = Path.GetExtension(path).TrimStart('.').ToLowerInvariant();
    string contentType = ext switch { "mp4" => "video/mp4", "webm" => "video/webm", _ => "application/octet-stream" };
    return Results.File(path, contentType, enableRangeProcessing: true);
});

app.MapGet("/api/media/{id:int}/thumb", async (IDbContextFactory<VideoIndexDbContext> dbFactory, int id) =>
{
    await using var db = await dbFactory.CreateDbContextAsync();
    var jpeg = await db.Thumbnails.Where(t => t.MediaFileId == id).OrderByDescending(t => t.Id).Select(t => t.Jpeg).FirstOrDefaultAsync();
    if (jpeg == null) return Results.NotFound();
    return Results.File(jpeg, "image/jpeg");
});

app.MapPost("/api/media/{id:int}/autotag", async (int id, bool? dryRun, IDbContextFactory<VideoIndexDbContext> dbFactory) =>
{
    await using var db = await dbFactory.CreateDbContextAsync();
    var m = await db.MediaFiles.SingleOrDefaultAsync(x => x.Id == id);
    if (m is null) return Results.NotFound("Media not found.");
    var before = AutoTagger.Snapshot(m);
    var changed = AutoTagger.Apply(m, dryRun.GetValueOrDefault(false));
    if (!dryRun.GetValueOrDefault(false) && changed) { m.UpdatedAt = DateTimeOffset.UtcNow; await db.SaveChangesAsync(); }
    var after = AutoTagger.Snapshot(m);
    return Results.Ok(new { updated = changed && !dryRun.GetValueOrDefault(false), dryRun = dryRun.GetValueOrDefault(false), changes = AutoTagger.Diff(before, after), before, after });
});

app.MapPost("/api/autotag/all", async (bool? dryRun, int? batchSize, IDbContextFactory<VideoIndexDbContext> dbFactory) =>
{
    var bs = Math.Clamp(batchSize.GetValueOrDefault(500), 50, 2_000);
    int total = 0, changed = 0, unchanged = 0, skipped = 0;
    await using var db = await dbFactory.CreateDbContextAsync();
    var ids = await db.MediaFiles.AsNoTracking().OrderBy(m => m.Id).Select(m => m.Id).ToListAsync();
    total = ids.Count;
    for (int ofs = 0; ofs < ids.Count; ofs += bs)
    {
        var chunkIds = ids.Skip(ofs).Take(bs).ToList();
        var set = await db.MediaFiles.Where(m => chunkIds.Contains(m.Id)).ToListAsync();
        foreach (var m in set) { if (AutoTagger.Apply(m, dryRun.GetValueOrDefault(false))) changed++; else unchanged++; }
        if (!dryRun.GetValueOrDefault(false)) { await db.SaveChangesAsync(); }
    }
    return Results.Ok(new { dryRun = dryRun.GetValueOrDefault(false), total, changed, unchanged, skipped });
});

app.MapPut("/api/media/{id:int}", async (IDbContextFactory<VideoIndexDbContext> dbFactory, int id, UpdateMediaDto dto) =>
{
    await using var db = await dbFactory.CreateDbContextAsync();
    var m = await db.MediaFiles.SingleOrDefaultAsync(x => x.Id == id);
    if (m is null) return Results.NotFound("Media not found.");
    var allowedSourceTypes = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "Studio", "OnlyFans", "Amateur", "Vintage", "Webcam", "Spycam", "CCTV", "Selfie", "Social Media", "Animated", "Unknown" };
    var allowedOrientation = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "Gay", "Straight", "Bi", "FtM Trans", "MtF Trans" };

    m.Title = string.IsNullOrWhiteSpace(dto.Title) ? null : dto.Title.Trim();
    m.IsFavourite = dto.IsFavourite ?? false;

    if (dto.SourceTypes is { Length: > 0 }) { var srcTypes = dto.SourceTypes.Select(s => s?.Trim()).Where(s => !string.IsNullOrEmpty(s)).Distinct(StringComparer.OrdinalIgnoreCase).Select(s => s!).ToList(); if (srcTypes.Any(s => !allowedSourceTypes.Contains(s!))) return Results.BadRequest("SourceType contains invalid values."); m.SourceTypes = srcTypes.Select(s => allowedSourceTypes.First(a => a.Equals(s!, StringComparison.OrdinalIgnoreCase))).ToList(); }
    m.StudioName = string.IsNullOrWhiteSpace(dto.StudioName) ? null : dto.StudioName.Trim();
    m.SourceUsername = string.IsNullOrWhiteSpace(dto.SourceUsername) ? null : dto.SourceUsername.Trim();
    if (dto.Year.HasValue) { if (dto.Year < 1900 || dto.Year > 2100) return Results.BadRequest("Year must be between 1900 and 2100."); m.Year = dto.Year.Value; }
    if (dto.OrientationTags is { Length: > 0 }) { var oTags = dto.OrientationTags.Select(s => s?.Trim()).Where(s => !string.IsNullOrEmpty(s)).Distinct(StringComparer.OrdinalIgnoreCase).Select(s => s!).ToList(); if (oTags.Any(s => !allowedOrientation.Contains(s!))) return Results.BadRequest("Orientation contains invalid values."); m.OrientationTags = oTags.Select(s => allowedOrientation.First(a => a.Equals(s!, StringComparison.OrdinalIgnoreCase))).ToList(); }
    if (dto.OtherTags is not null) { var list = dto.OtherTags.Split(',', StringSplitOptions.RemoveEmptyEntries).Select(s => s.Trim()).Where(s => !string.IsNullOrWhiteSpace(s)).Distinct(StringComparer.OrdinalIgnoreCase).ToList(); m.OtherTags = list.Count > 0 ? list : null; }
    if (dto.Performers is not null) { var list = dto.Performers.Split(',', StringSplitOptions.RemoveEmptyEntries).Select(s => s.Trim()).Where(s => !string.IsNullOrWhiteSpace(s)).Distinct(StringComparer.OrdinalIgnoreCase).ToList(); m.PerformerNames = list.Count > 0 ? list : null; m.PerformerCount = list.Count > 0 ? list.Count : m.PerformerCount; }
    if (dto.PerformerCount.HasValue) { if (dto.PerformerCount < 0) return Results.BadRequest("PerformerCount must be >= 0."); m.PerformerCount = dto.PerformerCount.Value; }
    m.UpdatedAt = DateTimeOffset.UtcNow;
    await db.SaveChangesAsync();
    return Results.Ok(new { updated = true });
});

app.MapDelete("/api/media/{id:int}", async (int id, bool? deleteFile, IDbContextFactory<VideoIndexDbContext> dbFactory) =>
{
    await using var db = await dbFactory.CreateDbContextAsync();
    var mediaFile = await db.MediaFiles.FindAsync(id);
    if (mediaFile is null) return Results.NotFound();
    if (deleteFile == true) { try { if (File.Exists(mediaFile.Path)) { File.Delete(mediaFile.Path); } } catch (Exception ex) { return Results.Problem($"Error deleting file from disk: {ex.Message}"); } }
    db.MediaFiles.Remove(mediaFile);
    await db.SaveChangesAsync();
    return Results.Ok(new { deleted = true, id });
});

app.MapPost("/api/autotag/cleanup", async (IDbContextFactory<VideoIndexDbContext> dbFactory) =>
{
    await using var db = await dbFactory.CreateDbContextAsync();
    int cleanedCount = 0, scannedCount = 0, page = 0; const int batchSize = 500;
    while (true)
    {
        var batch = await db.MediaFiles.Where(m => m.OtherTags != null).OrderBy(m => m.Id).Skip(page * batchSize).Take(batchSize).ToListAsync();
        if (batch.Count == 0) break;
        scannedCount += batch.Count;
        var toProcess = batch.Where(m => m.OtherTags!.Contains("x-auto-tagged")).ToList();
        foreach (var m in toProcess) { bool hasOtherMeaningfulTags = (m.SourceTypes?.Count > 0) || (m.OrientationTags?.Count > 0) || (m.PerformerNames?.Count > 0) || !string.IsNullOrWhiteSpace(m.StudioName) || !string.IsNullOrWhiteSpace(m.SourceUsername) || m.Year.HasValue || m.PerformerCount.HasValue || (m.OtherTags?.Count > 1); if (!hasOtherMeaningfulTags) { m.OtherTags?.Remove("x-auto-tagged"); if (m.OtherTags?.Count == 0) { m.OtherTags = null; } m.UpdatedAt = DateTimeOffset.UtcNow; cleanedCount++; } }
        if (toProcess.Any()) { await db.SaveChangesAsync(); }
        page++;
    }
    return Results.Ok(new { filesScanned = scannedCount, filesCleaned = cleanedCount });
});


// Final registration
app.MapHub<ScanHub>("/hubs/scan");
app.MapRazorPages();
app.Run();

// ---------- DTOs & helpers ----------
public static class ScanManager { public static readonly System.Collections.Concurrent.ConcurrentDictionary<string, CancellationTokenSource> ActiveScans = new(); }
public record RootDto(string Name, string Path);
public record UpdateMediaDto(string? Title, bool? IsFavourite, string[]? SourceTypes, string? StudioName, string? SourceUsername, int? Year, string[]? OrientationTags, string? OtherTags, string? Performers, int? PerformerCount, string? Orientation, string? Type, string? Studio, string? WebsiteSource);
public record BatchDeleteDto(List<int> Ids, bool? DeleteFile);
static class Proc { public static async Task<(int exitCode, string stdout, string stderr)> RunAsync(string exe, string args, TimeSpan timeout) { var psi = new ProcessStartInfo { FileName = exe, Arguments = args, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true }; using var proc = new Process { StartInfo = psi, EnableRaisingEvents = true }; proc.Start(); var stdoutTask = proc.StandardOutput.ReadToEndAsync(); var stderrTask = proc.StandardError.ReadToEndAsync(); if (!proc.WaitForExit((int)timeout.TotalMilliseconds)) { try { proc.Kill(true); } catch { } return (-1, "", "Timed out"); } var stdout = await stdoutTask; var stderr = await stderrTask; return (proc.ExitCode, stdout, stderr); } }
static class ProbeParser { public sealed record Result(double? LengthSeconds, long? OverallBitrate, int? Width, int? Height, double? FrameRate, string? VideoCodec, string? AudioCodec, long? AudioBitrate, int? AudioChannels); public static Result Parse(string json) { using var doc = JsonDocument.Parse(json); double? length = TryGetDouble(doc.RootElement, "format", "duration"); long? overall = TryGetLong(doc.RootElement, "format", "bit_rate"); int? width = null, height = null; double? fps = null; string? vcodec = null, acodec = null; long? abitrate = null; int? achannels = null; if (doc.RootElement.TryGetProperty("streams", out var streams) && streams.ValueKind == JsonValueKind.Array) { foreach (var s in streams.EnumerateArray()) { if (!s.TryGetProperty("codec_type", out var ct)) continue; var type = ct.GetString(); if (type == "video" && vcodec is null) { vcodec = s.TryGetProperty("codec_name", out var cn) ? cn.GetString() : null; width = s.TryGetProperty("width", out var w) ? TryGetInt(w) : null; height = s.TryGetProperty("height", out var h) ? TryGetInt(h) : null; string? rate = null; if (s.TryGetProperty("avg_frame_rate", out var afr)) rate = afr.GetString(); else if (s.TryGetProperty("r_frame_rate", out var rfr)) rate = rfr.GetString(); fps = ParseRate(rate); } else if (type == "audio" && acodec is null) { acodec = s.TryGetProperty("codec_name", out var an) ? an.GetString() : null; abitrate = s.TryGetProperty("bit_rate", out var abr) ? TryGetLong(abr) : null; achannels = s.TryGetProperty("channels", out var ch) ? TryGetInt(ch) : null; } } } return new Result(length, overall, width, height, fps, vcodec, acodec, abitrate, achannels); } static double? TryGetDouble(JsonElement root, string objName, string propName) => root.TryGetProperty(objName, out var obj) && obj.TryGetProperty(propName, out var val) ? TryGetDouble(val) : null; static long? TryGetLong(JsonElement root, string objName, string propName) => root.TryGetProperty(objName, out var obj) && obj.TryGetProperty(propName, out var val) ? TryGetLong(val) : null; static int? TryGetInt(JsonElement e) { return e.ValueKind switch { JsonValueKind.String => int.TryParse(e.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var i) ? i : null, JsonValueKind.Number => e.TryGetInt32(out var i) ? i : null, _ => null }; } static long? TryGetLong(JsonElement e) { return e.ValueKind switch { JsonValueKind.String => long.TryParse(e.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var l) ? l : null, JsonValueKind.Number => e.TryGetInt64(out var l) ? l : null, _ => null }; } static double? TryGetDouble(JsonElement e) { return e.ValueKind switch { JsonValueKind.String => double.TryParse(e.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? d : null, JsonValueKind.Number => e.TryGetDouble(out var d) ? d : null, _ => null }; } static double? ParseRate(string? fraction) { if (string.IsNullOrWhiteSpace(fraction)) return null; var slash = fraction.IndexOf('/'); if (slash <= 0) return double.TryParse(fraction, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? d : null; var numStr = fraction[..slash]; var denStr = fraction[(slash + 1)..]; if (!double.TryParse(numStr, NumberStyles.Float, CultureInfo.InvariantCulture, out var num)) return null; if (!double.TryParse(denStr, NumberStyles.Float, CultureInfo.InvariantCulture, out var den)) return null; if (den == 0) return null; return num / den; } }
static class AutoTagger { public static bool Apply(MediaFile m, bool dryRun) { var initialSnapshot = Snapshot(m); var src = new HashSet<string>(m.SourceTypes ?? new List<string>(), StringComparer.OrdinalIgnoreCase); var other = new HashSet<string>(m.OtherTags ?? new List<string>(), StringComparer.OrdinalIgnoreCase); other.Remove("x-auto-tagged"); var fp = SafeFullPath(m.Path); var filename = m.Filename ?? ""; var segs = SplitSegments(fp); static string normSeg(string s) => s.Replace(" ", ""); bool hasOnlyFansText = Regex.IsMatch(fp ?? "", @"only\s*fans", RegexOptions.IgnoreCase) || Regex.IsMatch(filename, @"only\s*fans", RegexOptions.IgnoreCase); if (hasOnlyFansText) { src.Add("OnlyFans"); src.Add("Amateur"); var idx = segs.FindIndex(s => normSeg(s).Equals("onlyfans", StringComparison.OrdinalIgnoreCase)); if (idx >= 0 && idx + 1 < segs.Count && string.IsNullOrWhiteSpace(m.SourceUsername)) { m.SourceUsername = segs[idx + 1]; } } var studioIdx = segs.FindIndex(s => s.Equals("Studios", StringComparison.OrdinalIgnoreCase)); if (studioIdx >= 0 && studioIdx + 1 < segs.Count) { src.Add("Studio"); if (string.IsNullOrWhiteSpace(m.StudioName)) { m.StudioName = segs[studioIdx + 1]; } } var hasAnyMeaningfulTags = src.Any() || !string.IsNullOrWhiteSpace(m.StudioName) || !string.IsNullOrWhiteSpace(m.SourceUsername) || other.Any(); if (hasAnyMeaningfulTags) { other.Add("x-auto-tagged"); } var newSrc = src.Count > 0 ? src.OrderBy(s => s, StringComparer.OrdinalIgnoreCase).ToList() : null; var newOther = other.Count > 0 ? other.OrderBy(s => s, StringComparer.OrdinalIgnoreCase).ToList() : null; m.SourceTypes = newSrc; m.OtherTags = newOther; var finalSnapshot = Snapshot(m); bool changed = !JsonSerializer.Serialize(initialSnapshot).Equals(JsonSerializer.Serialize(finalSnapshot)); if (dryRun) return changed; return changed; } public static object Snapshot(MediaFile m) => new { sourceTypes = m.SourceTypes ?? new List<string>(), otherTags = m.OtherTags ?? new List<string>(), studioName = m.StudioName, sourceUsername = m.SourceUsername }; public static object Diff(object before, object after) => new { before, after }; static string SafeFullPath(string p) { try { return Path.GetFullPath(p ?? ""); } catch { return p ?? ""; } } static List<string> SplitSegments(string path) { if (string.IsNullOrEmpty(path)) return new List<string>(); var norm = path.Replace('/', Path.DirectorySeparatorChar); var parts = norm.Split(Path.DirectorySeparatorChar).Where(s => !string.IsNullOrWhiteSpace(s)).Select(s => s.Trim()).ToList(); return parts; } }
static class Indexer { private static readonly HashSet<string> _allowedExt = new(StringComparer.OrdinalIgnoreCase) { "mp4", "mkv", "mov", "avi", "wmv", "webm", "flv", "mpg", "mpeg", "ogv", "ts", "3gp" }; public static async Task<(bool ok, object result, string? err)> IndexOneAsync(VideoIndexDbContext db, string path) { if (string.IsNullOrWhiteSpace(path)) return (false, null!, "Query ?path=... is required."); path = Path.GetFullPath(path); if (!File.Exists(path)) return (false, null!, $"File not found: {path}"); var ext = Path.GetExtension(path); var extNoDot = string.IsNullOrEmpty(ext) ? "" : (ext[0] == '.' ? ext[1..] : ext); if (!_allowedExt.Contains(extNoDot)) return (false, null!, $"Unsupported extension: {extNoDot}"); var fi = new FileInfo(path); if (fi.Length == 0) return (false, null!, "Zero-byte file; skipping."); var roots = await db.ScanRoots.AsNoTracking().ToListAsync(); var owner = roots.Where(r => path.StartsWith(r.Path, StringComparison.OrdinalIgnoreCase)).OrderByDescending(r => r.Path.Length).FirstOrDefault(); if (owner == null) return (false, null!, "File is not under any registered scan root."); string sha256; using (var fs = File.OpenRead(path)) using (var sha = SHA256.Create()) sha256 = Convert.ToHexString(await sha.ComputeHashAsync(fs)).ToLowerInvariant(); var filename = fi.Name; var sizeBytes = fi.Length; var (meta, probeErr) = await ProbeForMetaAsync(path); if (probeErr != null) return (false, null!, probeErr); var now = DateTimeOffset.UtcNow; var existing = await db.MediaFiles.SingleOrDefaultAsync(m => m.Path == path); if (existing == null) { var mf = new MediaFile { Sha256 = sha256, RootId = owner.Id, Filename = filename, Extension = extNoDot.ToLowerInvariant(), Path = path, SizeBytes = sizeBytes, LengthSeconds = meta!.LengthSeconds, Height = meta!.Height, Width = meta!.Width, BitRate = meta!.OverallBitrate, FrameRate = meta!.FrameRate, VideoFormat = meta!.VideoCodec, AudioFormat = meta!.AudioCodec, AudioBitrate = meta!.AudioBitrate, AudioChannels = meta!.AudioChannels, CreatedAt = now, UpdatedAt = now }; db.MediaFiles.Add(mf); await db.SaveChangesAsync(); return (true, new { created = true, mf.Id, mf.Path, mf.Filename, mf.Sha256 }, null); } else { existing.Sha256 = sha256; existing.RootId = owner.Id; existing.Filename = filename; existing.Extension = extNoDot.ToLowerInvariant(); existing.SizeBytes = sizeBytes; existing.LengthSeconds = meta!.LengthSeconds; existing.Height = meta!.Height; existing.Width = meta!.Width; existing.BitRate = meta!.OverallBitrate; existing.FrameRate = meta!.FrameRate; existing.VideoFormat = meta!.VideoCodec; existing.AudioFormat = meta!.AudioCodec; existing.AudioBitrate = meta!.AudioBitrate; existing.AudioChannels = meta!.AudioChannels; existing.UpdatedAt = now; await db.SaveChangesAsync(); return (true, new { created = false, existing.Id, existing.Path, existing.Filename, existing.Sha256 }, null); } } public static async Task<(bool ok, object result, string? err)> IndexOneWithFactoryAsync(IDbContextFactory<VideoIndexDbContext> factory, string path) { await using var db = await factory.CreateDbContextAsync(); return await IndexOneAsync(db, path); } public static async Task<(bool ok, byte[]? jpeg, string? err)> GenerateThumbnailWithFactoryAsync(IDbContextFactory<VideoIndexDbContext> factory, string path, int lumaThreshold, int maxTries, int width, int jpegQuality, bool onlyIfMissing) { await using var db = await factory.CreateDbContextAsync(); var mf = await db.MediaFiles.Include(m => m.Thumbnails).SingleOrDefaultAsync(m => m.Path == path); if (onlyIfMissing && mf != null && mf.Thumbnails.Any()) return (false, null, "skip"); return await GenerateThumbnailAsync(db, path, lumaThreshold, maxTries, width, jpegQuality); } public static async Task<(bool ok, byte[]? jpeg, string? err)> GenerateThumbnailAsync(VideoIndexDbContext db, string path, int lumaThreshold, int maxTries, int width, int jpegQuality) { if (string.IsNullOrWhiteSpace(path)) return (false, null, "Query ?path=... is required."); path = Path.GetFullPath(path); if (!File.Exists(path)) return (false, null, $"File not found: {path}"); var mf = await db.MediaFiles.Include(m => m.Thumbnails).SingleOrDefaultAsync(m => m.Path == path); if (mf == null) return (false, null, "File is not indexed yet."); var duration = mf.LengthSeconds; if (duration is null || duration <= 0) { var (meta, err) = await ProbeForMetaAsync(path); if (err != null) return (false, null, err); duration = meta!.LengthSeconds; if (duration is null || duration <= 0) return (false, null, "Could not determine duration."); mf.LengthSeconds = duration; await db.SaveChangesAsync(); } var start = duration.Value * 0.25; var end = duration.Value * 0.50; if (end <= start) end = Math.Max(start + 1, start * 1.25); byte[]? chosen = null; var rng = new Random(); for (int i = 0; i < maxTries; i++) { var t = start + rng.NextDouble() * (end - start); var (ok, jpeg, avg, err) = await TryMakeThumbAsync(path, t, width, jpegQuality); if (!ok) { if (i == maxTries - 1) return (false, null, err ?? "ffmpeg failed creating thumbnail."); continue; } if (avg >= lumaThreshold || i == maxTries - 1) { chosen = jpeg; break; } } if (chosen == null) return (false, null, "Failed to generate thumbnail."); if (mf.Thumbnails.Count > 0) db.Thumbnails.RemoveRange(mf.Thumbnails); db.Thumbnails.Add(new Thumbnail { MediaFileId = mf.Id, Jpeg = chosen, GeneratedAt = DateTimeOffset.UtcNow }); await db.SaveChangesAsync(); return (true, chosen, null); } private static async Task<(bool ok, byte[]? jpeg, double avgLuma, string? err)> TryMakeThumbAsync(string videoPath, double tSeconds, int width, int jpegQuality) { var tmp = Path.Combine(Path.GetTempPath(), $"thumb_{Guid.NewGuid():N}.jpg"); try { var ts = tSeconds.ToString("0.###", CultureInfo.InvariantCulture); var q = Math.Clamp(jpegQuality / 5, 1, 5); var args = $"-y -ss {ts} -i \"{videoPath}\" -frames:v 1 -vf \"scale={width}:-1:flags=lanczos\" -q:v {q} \"{tmp}\""; var (code, _, stderr) = await Proc.RunAsync("ffmpeg", args, TimeSpan.FromSeconds(20)); if (code != 0 || !File.Exists(tmp)) return (false, null, 0, $"ffmpeg failed (code {code}). {stderr}"); using var bmp = new Bitmap(tmp); double sum = 0; long count = 0; for (int y = 0; y < bmp.Height; y++) for (int x = 0; x < bmp.Width; x++) { var c = bmp.GetPixel(x, y); var l = 0.2126 * c.R + 0.7152 * c.G + 0.0722 * c.B; sum += l; count++; } var avg = count > 0 ? sum / count : 0.0; var bytes = await File.ReadAllBytesAsync(tmp); return (true, bytes, avg, null); } catch (Exception ex) { return (false, null, 0, ex.Message); } finally { try { if (File.Exists(tmp)) File.Delete(tmp); } catch { } } } private static async Task<(ProbeParser.Result? meta, string? err)> ProbeForMetaAsync(string path) { var (code, stdout, stderr) = await Proc.RunAsync("ffprobe", $"-v error -hide_banner -print_format json -show_format -show_streams \"{path}\"", TimeSpan.FromSeconds(30)); if (code != 0) return (null, $"ffprobe failed (code {code}). {stderr}"); return (ProbeParser.Parse(stdout), null); } }