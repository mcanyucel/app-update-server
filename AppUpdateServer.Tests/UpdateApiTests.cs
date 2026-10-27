using System.Net;
using System.Net.Http.Json;
using System.Text;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;

namespace AppUpdateServer.Tests;

/// <summary>
/// Boots the real pipeline, so these cover what the service-level tests cannot:
/// form field names, the API key check and the forward-auth middleware.
/// </summary>
public class UpdateApiTests : IClassFixture<UpdateServerFactory>
{
    private const string ApiKey = "test-key";

    private readonly UpdateServerFactory _factory;

    public UpdateApiTests(UpdateServerFactory factory) => _factory = factory;

    private HttpClient Client(string? apiKey = ApiKey)
    {
        var client = _factory.CreateClient();
        if (apiKey is not null)
            client.DefaultRequestHeaders.Add("X-API-Key", apiKey);
        return client;
    }

    private static MultipartFormDataContent UploadForm(
        string appName,
        string version,
        string? notes = null,
        string notesFieldName = "notes",
        string content = "binary"
    )
    {
        var form = new MultipartFormDataContent
        {
            { new StringContent(appName), "appName" },
            { new StringContent(version), "version" },
            { new ByteArrayContent(Encoding.UTF8.GetBytes(content)), "updateFile", "MyApp.exe" },
        };
        if (notes is not null)
            form.Add(new StringContent(notes), notesFieldName);
        return form;
    }

    [Fact]
    public async Task Health_ReportsHealthy()
    {
        var response = await Client(apiKey: null).GetAsync("/health");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("Healthy", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Upload_WithoutAnApiKey_IsUnauthorized()
    {
        var response = await Client(apiKey: null)
            .PostAsync("/api/update/upload", UploadForm("Unauthorized App", "1.0.0"));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Upload_WithTheWrongApiKey_IsUnauthorized()
    {
        var response = await Client("wrong-key")
            .PostAsync("/api/update/upload", UploadForm("Unauthorized App", "1.0.0"));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Upload_StoresReleaseNotesFromTheNotesField()
    {
        var response = await Client()
            .PostAsync(
                "/api/update/upload",
                UploadForm("Notes App", "1.0.0", notes: "from the cli")
            );

        response.EnsureSuccessStatusCode();
        var version = await response.Content.ReadFromJsonAsync<VersionPayload>();
        Assert.Equal("from the cli", version!.ReleaseNotes);
    }

    /// <summary>
    /// Regression: the CLI posted "releaseNotes" while the server only read
    /// "notes", so every upload from a build pipeline lost its notes. Older
    /// installed CLI versions still send the old name.
    /// </summary>
    [Fact]
    public async Task Upload_StillAcceptsTheLegacyReleaseNotesField()
    {
        var response = await Client()
            .PostAsync(
                "/api/update/upload",
                UploadForm(
                    "Legacy Notes App",
                    "1.0.0",
                    notes: "from an older cli",
                    notesFieldName: "releaseNotes"
                )
            );

        response.EnsureSuccessStatusCode();
        var version = await response.Content.ReadFromJsonAsync<VersionPayload>();
        Assert.Equal("from an older cli", version!.ReleaseNotes);
    }

    [Fact]
    public async Task Upload_RejectsAVersionStringThatWouldEscapeTheBinariesDirectory()
    {
        var response = await Client()
            .PostAsync("/api/update/upload", UploadForm("Traversal App", "../../../tmp/evil"));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task CheckUpdateThenDownload_RoundTripsTheBinary()
    {
        await Client()
            .PostAsync(
                "/api/update/upload",
                UploadForm("Round Trip App", "2.0.0", content: "installer bytes")
            );

        var check = await Client(apiKey: null)
            .PostAsJsonAsync(
                "/api/update",
                new
                {
                    appIdentity = "{ Name=Round Trip App, Version=1.0.0, ProcessorArchitecture=MSIL }",
                    stateSeed = 42,
                }
            );

        check.EnsureSuccessStatusCode();
        var payload = await check.Content.ReadFromJsonAsync<CheckPayload>();
        Assert.Equal("2.0.0", payload!.Version);
        Assert.Equal("/api/update/download/round-trip-app/2.0.0", payload.DownloadUrl);

        var download = await Client(apiKey: null).GetAsync(payload.DownloadUrl);

        download.EnsureSuccessStatusCode();
        Assert.Equal("installer bytes", await download.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task CheckUpdate_ForAnUnknownApp_IsNotFound()
    {
        var response = await Client(apiKey: null)
            .PostAsJsonAsync(
                "/api/update",
                new { appIdentity = "{ Name=Nothing Here }", stateSeed = 1 }
            );

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task DeleteVersion_AddressedToTheWrongApp_IsNotFound()
    {
        var upload = await Client()
            .PostAsync("/api/update/upload", UploadForm("Victim App", "1.0.0"));
        var version = await upload.Content.ReadFromJsonAsync<VersionPayload>();
        await Client().PostAsync("/api/update/upload", UploadForm("Attacker App", "1.0.0"));

        var response = await Client()
            .DeleteAsync($"/api/update/apps/attacker-app/versions/{version!.Id}");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);

        var versions = await Client()
            .GetFromJsonAsync<List<VersionPayload>>("/api/update/apps/victim-app/versions");
        Assert.Single(versions!);
    }

    private record VersionPayload(int Id, string VersionString, string ReleaseNotes);

    private record CheckPayload(string Version, string DownloadUrl, string Sha256Hash);
}

/// <summary>Forward auth on, with a proxy range the test client never matches.</summary>
public class ForwardAuthTests : IClassFixture<ForwardAuthServerFactory>
{
    private readonly ForwardAuthServerFactory _factory;

    public ForwardAuthTests(ForwardAuthServerFactory factory) => _factory = factory;

    [Fact]
    public async Task UiRoutes_RejectARemoteUserHeaderFromOutsideTheTrustedProxyRange()
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Add("Remote-User", "attacker");

        var response = await client.GetAsync("/");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task HealthAndPublicApiStayReachableWithoutAuthentication()
    {
        var client = _factory.CreateClient();

        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/health")).StatusCode);

        var check = await client.PostAsJsonAsync(
            "/api/update",
            new { appIdentity = "{ Name=Unknown App }", stateSeed = 1 }
        );
        // Not found rather than unauthorized: the middleware let it through.
        Assert.Equal(HttpStatusCode.NotFound, check.StatusCode);
    }

    [Fact]
    public async Task UploadStaysReachableForTheCliBehindItsApiKey()
    {
        var client = _factory.CreateClient();

        var response = await client.PostAsync("/api/update/upload", new MultipartFormDataContent());

        // Unauthorized from the API key check, not a 401 page from forward auth.
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }
}

public class UpdateServerFactory : WebApplicationFactory<Program>, IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "aus-tests",
        Guid.NewGuid().ToString("N")
    );

    protected virtual Dictionary<string, string?> Settings =>
        new()
        {
            ["ConnectionStrings:Default"] = $"Data Source={Path.Combine(_root, "test.db")}",
            ["Storage:BinariesPath"] = Path.Combine(_root, "binaries"),
            ["Auth:ApiKey"] = "test-key",
            ["Auth:ForwardAuth"] = "false",
        };

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        Directory.CreateDirectory(_root);
        builder.UseEnvironment("Testing");

        // UseSetting lands in host configuration, which Program can read before
        // the host is built; ConfigureAppConfiguration alone would be too late
        // for the data protection key path.
        foreach (var (key, value) in Settings)
            builder.UseSetting(key, value);

        builder.ConfigureAppConfiguration(config => config.AddInMemoryCollection(Settings));
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        if (!disposing)
            return;

        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }
}

public class ForwardAuthServerFactory : UpdateServerFactory
{
    protected override Dictionary<string, string?> Settings
    {
        get
        {
            var settings = base.Settings;
            settings["Auth:ForwardAuth"] = "true";
            // The in-memory test client has no remote address, so it can never
            // be mistaken for the proxy.
            settings["Auth:TrustedProxies"] = "172.18.0.0/16, 10.0.0.5";
            return settings;
        }
    }
}
