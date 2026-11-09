using System.Security.Cryptography;
using System.Text;
using AppUpdateServer.Services;
using Microsoft.AspNetCore.Mvc;

namespace AppUpdateServer.Api;

public record UpdateCheckRequest(string AppIdentity, int StateSeed);

public record SetTagRequest(string? Tag);

public record UpdateCheckResponse(
    string Version,
    string DownloadUrl,
    string ReleaseNotes,
    string Sha256Hash,
    string State
);

[ApiController]
[Route("api/update")]
public class UpdateController(
    UpdateService updates,
    IConfiguration config,
    ILogger<UpdateController> logger
) : ControllerBase
{
    [HttpPost]
    public async Task<IActionResult> CheckUpdate([FromBody] UpdateCheckRequest request)
    {
        var appInfo = ParseAppIdentity(request.AppIdentity);
        if (appInfo is null)
            return BadRequest("Invalid app identity");

        var slug = UpdateService.GenerateSlug(appInfo.Value.Name);
        var latest = await updates.GetLatestVersionAsync(slug);

        if (latest is null)
        {
            logger.LogWarning("Update check: app not found for slug {Slug}", slug);
            return NotFound("Application not found");
        }

        return Ok(
            new UpdateCheckResponse(
                Version: latest.VersionString,
                DownloadUrl: $"/api/update/download/{slug}/{latest.VersionString}",
                ReleaseNotes: latest.ReleaseNotes,
                Sha256Hash: latest.Sha256Hash,
                State: TransformState(request.StateSeed)
            )
        );
    }

    [HttpGet("download/{slug}/{version}")]
    public async Task<IActionResult> Download(string slug, string version)
    {
        var result = await updates.GetBinaryAsync(slug, version);
        if (result is null)
            return NotFound();

        var (stream, fileName, contentType) = result.Value;
        return File(stream, contentType, fileName);
    }

    /// <summary>
    /// Public, read-only list of the latest version of every app carrying
    /// <paramref name="tag"/>, for download pages. The tag is required so the
    /// full app list stays behind the management API.
    /// </summary>
    [HttpGet("catalog")]
    public async Task<IActionResult> Catalog([FromQuery] string? tag)
    {
        if (UpdateService.NormalizeTag(tag) is null)
            return BadRequest("tag is required");

        return Ok(await updates.GetCatalogAsync(tag!));
    }

    [HttpPost("upload")]
    public async Task<IActionResult> Upload()
    {
        if (!IsAuthorized())
            return Unauthorized();

        var form = Request.Form;
        var appName = form["appName"].ToString();
        var version = form["version"].ToString();
        // "releaseNotes" is what older aus CLI versions send.
        var notes = form["notes"].ToString();
        if (string.IsNullOrEmpty(notes))
            notes = form["releaseNotes"].ToString();
        var file = form.Files["updateFile"];
        // Optional. When sent it (re)tags the app; when absent the tag is left alone.
        var tag = form.ContainsKey("tag") ? form["tag"].ToString() : null;

        if (string.IsNullOrWhiteSpace(appName) || string.IsNullOrWhiteSpace(version))
            return BadRequest("appName and version are required");

        if (!UpdateService.IsValidVersionString(version))
            return BadRequest(UpdateService.InvalidVersionMessage);

        if (file is null || file.Length == 0)
            return BadRequest("updateFile is required");

        if (!UpdateService.IsValidTag(tag))
            return BadRequest(UpdateService.InvalidTagMessage);

        var slug = UpdateService.GenerateSlug(appName);
        var existing = await updates.GetAppBySlugAsync(slug);
        if (existing is null)
        {
            logger.LogInformation("Auto-creating app record for {Name}", appName);
            await updates.CreateAppAsync(appName, tag);
        }
        else if (tag is not null)
        {
            await updates.SetTagAsync(slug, tag);
        }

        await using var stream = file.OpenReadStream();
        var result = await updates.UploadVersionAsync(slug, version, notes, stream, file.FileName);

        if (!result.Success)
            return BadRequest(result.Error);

        return Ok(result.Version);
    }

    private bool IsAuthorized()
    {
        var expected = config["Auth:ApiKey"];
        if (string.IsNullOrEmpty(expected))
        {
            logger.LogWarning("No API key configured — upload rejected");
            return false;
        }
        var provided = Request.Headers["X-API-Key"].ToString();
        return CryptographicOperations.FixedTimeEquals(
            Encoding.UTF8.GetBytes(provided),
            Encoding.UTF8.GetBytes(expected)
        );
    }

    private static (string Name, string Version, string Arch)? ParseAppIdentity(string identity)
    {
        var dict = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var clean = identity.Trim('{', '}', ' ');
        foreach (var part in clean.Split(','))
        {
            var kv = part.Trim().Split('=', 2);
            if (kv.Length == 2)
                dict[kv[0].Trim()] = kv[1].Trim();
        }

        if (!dict.TryGetValue("Name", out var name) || string.IsNullOrEmpty(name))
            return null;

        dict.TryGetValue("Version", out var ver);
        dict.TryGetValue("ProcessorArchitecture", out var arch);
        return (name, ver ?? "", arch ?? "");
    }

    private static string TransformState(int seed)
    {
        var bytes = BitConverter.GetBytes(seed);
        if (!BitConverter.IsLittleEndian)
            Array.Reverse(bytes);
        var hash = SHA256.HashData(bytes);
        return Convert.ToBase64String(hash[..8]);
    }

    [HttpGet("apps")]
    public async Task<IActionResult> ListApps()
    {
        if (!IsAuthorized())
            return Unauthorized();

        var apps = await updates.GetAllAppsAsync();
        return Ok(apps);
    }

    [HttpDelete("apps/{slug}")]
    public async Task<IActionResult> DeleteApp(string slug)
    {
        if (!IsAuthorized())
            return Unauthorized();

        var app = await updates.GetAppBySlugAsync(slug);
        if (app is null)
            return NotFound($"App '{slug}' not found");

        await updates.DeleteAppAsync(app.Id);
        return Ok();
    }

    [HttpDelete("apps/{slug}/versions/{versionId:int}")]
    public async Task<IActionResult> DeleteVersion(string slug, int versionId)
    {
        if (!IsAuthorized())
            return Unauthorized();

        var result = await updates.DeleteVersionAsync(slug, versionId);
        if (!result)
            return NotFound($"Version {versionId} not found for app '{slug}'");

        return Ok();
    }

    [HttpPost("apps/{slug}/rollback")]
    public async Task<IActionResult> Rollback(string slug)
    {
        if (!IsAuthorized())
            return Unauthorized();

        var versions = await updates.GetVersionHistoryAsync(slug);
        if (versions.Count == 0)
            return NotFound("No versions found");

        if (versions.Count == 1)
            return BadRequest("Only one version exists, cannot rollback");

        await updates.DeleteVersionAsync(slug, versions[0].Id);
        return Ok(versions[1]);
    }

    /// <summary>Sets an app's tag; a null or blank tag clears it.</summary>
    [HttpPut("apps/{slug}/tag")]
    public async Task<IActionResult> SetTag(string slug, [FromBody] SetTagRequest request)
    {
        if (!IsAuthorized())
            return Unauthorized();

        if (!UpdateService.IsValidTag(request.Tag))
            return BadRequest(UpdateService.InvalidTagMessage);

        if (!await updates.SetTagAsync(slug, request.Tag))
            return NotFound($"App '{slug}' not found");

        return Ok(await updates.GetAppBySlugAsync(slug));
    }

    [HttpGet("apps/{slug}/versions")]
    public async Task<IActionResult> ListVersions(string slug)
    {
        if (!IsAuthorized())
            return Unauthorized();

        var versions = await updates.GetVersionHistoryAsync(slug);
        return Ok(versions);
    }
}
