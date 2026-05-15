using System.Security.Cryptography;
using System.Text;
using AppUpdateServer.Services;
using Microsoft.AspNetCore.Mvc;

namespace AppUpdateServer.Api;

public record UpdateCheckRequest(string AppIdentity, int StateSeed);

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

    [HttpPost("upload")]
    public async Task<IActionResult> Upload()
    {
        if (!IsAuthorized())
            return Unauthorized();

        var form = Request.Form;
        var appName = form["appName"].ToString();
        var version = form["version"].ToString();
        var notes = form["notes"].ToString();
        var file = form.Files["updateFile"];

        if (string.IsNullOrWhiteSpace(appName) || string.IsNullOrWhiteSpace(version))
            return BadRequest("appName and version are required");

        if (file is null || file.Length == 0)
            return BadRequest("updateFile is required");

        var slug = UpdateService.GenerateSlug(appName);
        var existing = await updates.GetAppBySlugAsync(slug);
        if (existing is null)
        {
            logger.LogInformation("Auto-creating app record for {Name}", appName);
            await updates.CreateAppAsync(appName);
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

        var result = await updates.DeleteVersionAsync(versionId);
        if (!result)
            return NotFound($"Version not found");

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

        await updates.DeleteVersionAsync(versions[0].Id);
        return Ok(versions[1]);
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
