using AppUpdateServer.Data;
using AppUpdateServer.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace AppUpdateServer.Services;

public record AppSummary(
    int Id,
    string Name,
    string Slug,
    VersionSummary? Latest,
    int TotalVersions
);

public record VersionSummary(
    int Id,
    string VersionString,
    string Sha256Hash,
    string ReleaseNotes,
    DateTime UploadedAt,
    long FileSizeBytes,
    string FileName
);

public record UploadResult(bool Success, string? Error, VersionSummary? Version);

public class UpdateService(AppDbContext db, IConfiguration config, ILogger<UpdateService> logger)
{
    public const long DefaultMaxUploadBytes = 1024L * 1024 * 1024; // 1 GiB

    private string BinariesPath => GetBinariesPath(config);

    public long MaxUploadBytes => GetMaxUploadBytes(config);

    public static string GetBinariesPath(IConfiguration config) =>
        config["Storage:BinariesPath"] ?? "/data/binaries";

    public static long GetMaxUploadBytes(IConfiguration config) =>
        config.GetValue<long?>("Storage:MaxUploadBytes") ?? DefaultMaxUploadBytes;

    public async Task<List<AppSummary>> GetAllAppsAsync()
    {
        var apps = await db
            .Apps.Include(a => a.Versions)
            .OrderBy(a => a.Name)
            .AsNoTracking()
            .ToListAsync();

        return apps.Select(ToSummary).ToList();
    }

    public async Task<AppSummary?> GetAppBySlugAsync(string slug)
    {
        var app = await db
            .Apps.Include(a => a.Versions)
            .AsNoTracking()
            .FirstOrDefaultAsync(a => a.Slug == slug);

        return app is null ? null : ToSummary(app);
    }

    public async Task<List<VersionSummary>> GetVersionHistoryAsync(string slug)
    {
        var app = await db.Apps.AsNoTracking().FirstOrDefaultAsync(a => a.Slug == slug);

        if (app is null)
            return [];

        return await db
            .Versions.Where(v => v.AppId == app.Id)
            .OrderByDescending(v => v.UploadedAt)
            .Select(v => ToVersionSummary(v))
            .ToListAsync();
    }

    public async Task<AppVersion?> GetLatestVersionAsync(string slug)
    {
        var app = await db.Apps.AsNoTracking().FirstOrDefaultAsync(a => a.Slug == slug);
        if (app is null)
            return null;

        return await db
            .Versions.Where(v => v.AppId == app.Id)
            .OrderByDescending(v => v.UploadedAt)
            .AsNoTracking()
            .FirstOrDefaultAsync();
    }

    public async Task<App> CreateAppAsync(string name)
    {
        var slug = GenerateSlug(name);
        var baseSlug = slug;
        var counter = 1;
        while (await db.Apps.AnyAsync(a => a.Slug == slug))
            slug = $"{baseSlug}-{counter++}";

        var app = new App { Name = name, Slug = slug };
        db.Apps.Add(app);
        await db.SaveChangesAsync();
        return app;
    }

    public async Task<UploadResult> UploadVersionAsync(
        string slug,
        string versionString,
        string releaseNotes,
        Stream fileStream,
        string originalFileName
    )
    {
        var app = await db.Apps.FirstOrDefaultAsync(a => a.Slug == slug);
        if (app is null)
            return new UploadResult(false, $"App '{slug}' not found", null);

        if (!IsValidVersionString(versionString))
            return new UploadResult(false, InvalidVersionMessage, null);

        var safeFileName =
            $"{slug}-{versionString}-{SanitizeFileName(originalFileName)}";
        var destPath = Path.Combine(BinariesPath, safeFileName);
        Directory.CreateDirectory(BinariesPath);

        // Stage to a temp file: destPath may be the binary we are about to
        // replace, and hashing can still fail halfway through.
        var tempPath = Path.Combine(BinariesPath, $".upload-{Guid.NewGuid():N}.tmp");

        string hash;
        long size;

        try
        {
            using var sha256 = System.Security.Cryptography.SHA256.Create();
            await using (var dest = File.Create(tempPath))
            await using (
                var hashStream = new System.Security.Cryptography.CryptoStream(
                    dest,
                    sha256,
                    System.Security.Cryptography.CryptoStreamMode.Write
                )
            )
            {
                await fileStream.CopyToAsync(hashStream);
                await hashStream.FlushFinalBlockAsync();
            }

            hash = Convert.ToHexString(sha256.Hash!).ToLowerInvariant();
            size = new FileInfo(tempPath).Length;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to write binary for {Slug} {Version}", slug, versionString);
            TryDeleteFile(tempPath);
            return new UploadResult(false, "Failed to save file", null);
        }

        var existing = await db.Versions.FirstOrDefaultAsync(v =>
            v.AppId == app.Id && v.VersionString == versionString
        );
        var previousFileName = existing?.FileName;

        try
        {
            File.Move(tempPath, destPath, overwrite: true);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to store binary for {Slug} {Version}", slug, versionString);
            TryDeleteFile(tempPath);
            return new UploadResult(false, "Failed to save file", null);
        }

        if (existing is not null)
        {
            existing.FileName = safeFileName;
            existing.Sha256Hash = hash;
            existing.ReleaseNotes = releaseNotes;
            existing.UploadedAt = DateTime.UtcNow;
            existing.FileSizeBytes = size;
        }
        else
        {
            db.Versions.Add(
                new AppVersion
                {
                    AppId = app.Id,
                    VersionString = versionString,
                    FileName = safeFileName,
                    Sha256Hash = hash,
                    ReleaseNotes = releaseNotes,
                    UploadedAt = DateTime.UtcNow,
                    FileSizeBytes = size,
                }
            );
        }

        await db.SaveChangesAsync();

        // Safe to drop the replaced binary only now, and only when the new
        // upload did not already land on the same path.
        if (previousFileName is not null && previousFileName != safeFileName)
            DeleteBinaryFile(previousFileName);

        var saved = await db
            .Versions.Where(v => v.AppId == app.Id && v.VersionString == versionString)
            .AsNoTracking()
            .FirstAsync();

        return new UploadResult(true, null, ToVersionSummary(saved));
    }

    public async Task<bool> DeleteVersionAsync(string slug, int versionId)
    {
        var version = await db.Versions.FirstOrDefaultAsync(v =>
            v.Id == versionId && v.App.Slug == slug
        );
        if (version is null)
            return false;

        DeleteBinaryFile(version.FileName);
        db.Versions.Remove(version);
        await db.SaveChangesAsync();
        return true;
    }

    public async Task<bool> DeleteAppAsync(int appId)
    {
        var app = await db.Apps.Include(a => a.Versions).FirstOrDefaultAsync(a => a.Id == appId);
        if (app is null)
            return false;

        foreach (var v in app.Versions)
            DeleteBinaryFile(v.FileName);

        db.Apps.Remove(app);
        await db.SaveChangesAsync();
        return true;
    }

    public async Task<(Stream stream, string fileName, string contentType)?> GetBinaryAsync(
        string slug,
        string versionString
    )
    {
        var app = await db.Apps.FirstOrDefaultAsync(a => a.Slug == slug);
        if (app is null)
            return null;

        var version = await db
            .Versions.Where(v => v.AppId == app.Id && v.VersionString == versionString)
            .AsNoTracking()
            .FirstOrDefaultAsync();

        if (version is null)
            return null;

        var path = Path.Combine(BinariesPath, version.FileName);
        if (!File.Exists(path))
            return null;

        return (File.OpenRead(path), version.FileName, "application/octet-stream");
    }

    private static AppSummary ToSummary(App app)
    {
        var latest = app.Versions.OrderByDescending(v => v.UploadedAt).FirstOrDefault();

        return new AppSummary(
            app.Id,
            app.Name,
            app.Slug,
            latest is null ? null : ToVersionSummary(latest),
            app.Versions.Count
        );
    }

    private static VersionSummary ToVersionSummary(AppVersion v) =>
        new(
            v.Id,
            v.VersionString,
            v.Sha256Hash,
            v.ReleaseNotes,
            v.UploadedAt,
            v.FileSizeBytes,
            v.FileName
        );

    private void DeleteBinaryFile(string fileName)
    {
        var path = Path.Combine(BinariesPath, fileName);
        if (File.Exists(path))
            File.Delete(path);
    }

    private void TryDeleteFile(string path)
    {
        try
        {
            if (File.Exists(path))
                File.Delete(path);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Could not remove temp file {Path}", path);
        }
    }

    public static string GenerateSlug(string name) =>
        System
            .Text.RegularExpressions.Regex.Replace(
                name.ToLowerInvariant().Trim(),
                @"[^a-z0-9]+",
                "-"
            )
            .Trim('-');

    private static string SanitizeFileName(string name) =>
        System.Text.RegularExpressions.Regex.Replace(
            Path.GetFileName(name),
            @"[^a-zA-Z0-9.\-_]",
            "_"
        );

    public const string InvalidVersionMessage =
        "Version must be 1-64 characters of letters, digits, dot, dash, underscore or plus, "
        + "and start with a letter or digit";

    // The version string is part of the stored file name, so anything that
    // could escape BinariesPath (separators, "..") must never reach disk.
    public static bool IsValidVersionString(string versionString) =>
        !string.IsNullOrWhiteSpace(versionString)
        && System.Text.RegularExpressions.Regex.IsMatch(
            versionString,
            @"^[a-zA-Z0-9][a-zA-Z0-9.\-_+]{0,63}$"
        );
}
