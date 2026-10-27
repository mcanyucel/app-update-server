using System.Security.Cryptography;
using System.Text;
using AppUpdateServer.Data;
using AppUpdateServer.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace AppUpdateServer.Tests;

public class UpdateServiceTests : IDisposable
{
    private readonly string _root;
    private readonly string _binariesPath;
    private readonly AppDbContext _db;
    private readonly UpdateService _service;

    public UpdateServiceTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "aus-tests", Guid.NewGuid().ToString("N"));
        _binariesPath = Path.Combine(_root, "binaries");
        Directory.CreateDirectory(_binariesPath);

        _db = new AppDbContext(
            new DbContextOptionsBuilder<AppDbContext>()
                .UseSqlite($"Data Source={Path.Combine(_root, "test.db")}")
                .Options
        );
        _db.Database.EnsureCreated();

        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(
                new Dictionary<string, string?> { ["Storage:BinariesPath"] = _binariesPath }
            )
            .Build();

        _service = new UpdateService(_db, config, NullLogger<UpdateService>.Instance);
    }

    public void Dispose()
    {
        _db.Dispose();
        SqliteConnectionPool.Clear();
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
        GC.SuppressFinalize(this);
    }

    // ── Upload ────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Upload_StoresBinaryAndRecordsItsHash()
    {
        await _service.CreateAppAsync("My App");

        var result = await _service.UploadVersionAsync(
            "my-app",
            "1.0.0",
            "first cut",
            Body("v1"),
            "MyApp.exe"
        );

        Assert.True(result.Success);
        Assert.Equal(Sha256("v1"), result.Version!.Sha256Hash);
        Assert.Equal("first cut", result.Version.ReleaseNotes);
        Assert.Equal("v1", await ReadBinary(result.Version.FileName));
    }

    [Fact]
    public async Task Upload_ToUnknownApp_Fails()
    {
        var result = await _service.UploadVersionAsync(
            "nope",
            "1.0.0",
            "",
            Body("v1"),
            "MyApp.exe"
        );

        Assert.False(result.Success);
        Assert.Contains("not found", result.Error);
    }

    /// <summary>
    /// Regression: the replaced binary used to be deleted after the new one had
    /// already been written to the same path, leaving a row pointing at nothing.
    /// </summary>
    [Fact]
    public async Task ReUploadingAVersion_UnderTheSameFileName_KeepsTheNewBinary()
    {
        await _service.CreateAppAsync("My App");
        await _service.UploadVersionAsync("my-app", "1.0.0", "first", Body("v1"), "MyApp.exe");

        var result = await _service.UploadVersionAsync(
            "my-app",
            "1.0.0",
            "second",
            Body("v1 changed"),
            "MyApp.exe"
        );

        Assert.True(result.Success);
        Assert.Equal(Sha256("v1 changed"), result.Version!.Sha256Hash);
        Assert.Equal("v1 changed", await ReadBinary(result.Version.FileName));
        Assert.Single(await _service.GetVersionHistoryAsync("my-app"));

        var binary = await _service.GetBinaryAsync("my-app", "1.0.0");
        Assert.NotNull(binary);
        await binary.Value.stream.DisposeAsync();
    }

    [Fact]
    public async Task ReUploadingAVersion_UnderADifferentFileName_RemovesTheOldBinary()
    {
        await _service.CreateAppAsync("My App");
        var first = await _service.UploadVersionAsync(
            "my-app",
            "1.0.0",
            "",
            Body("v1"),
            "MyApp.exe"
        );

        var second = await _service.UploadVersionAsync(
            "my-app",
            "1.0.0",
            "",
            Body("v1 changed"),
            "MyApp.msi"
        );

        Assert.False(File.Exists(Path.Combine(_binariesPath, first.Version!.FileName)));
        Assert.True(File.Exists(Path.Combine(_binariesPath, second.Version!.FileName)));
    }

    [Fact]
    public async Task Upload_LeavesNoTempFilesBehind()
    {
        await _service.CreateAppAsync("My App");
        await _service.UploadVersionAsync("my-app", "1.0.0", "", Body("v1"), "MyApp.exe");

        Assert.Empty(Directory.GetFiles(_binariesPath, "*.tmp"));
    }

    // ── Version string validation ─────────────────────────────────────────────

    [Theory]
    [InlineData("../../../tmp/evil")]
    [InlineData("..")]
    [InlineData("foo/bar")]
    [InlineData("foo\\bar")]
    [InlineData(".hidden")]
    [InlineData("")]
    [InlineData("   ")]
    public async Task Upload_RejectsVersionStringsThatCouldEscapeTheBinariesDirectory(
        string versionString
    )
    {
        await _service.CreateAppAsync("My App");

        var result = await _service.UploadVersionAsync(
            "my-app",
            versionString,
            "",
            Body("v1"),
            "MyApp.exe"
        );

        Assert.False(result.Success);
        Assert.Empty(Directory.GetFiles(_binariesPath));
        Assert.DoesNotContain(Directory.GetDirectories(_root), d => d != _binariesPath);
    }

    [Theory]
    [InlineData("1.0.0")]
    [InlineData("3.2.0.0")]
    [InlineData("1.0.0-beta+build.7")]
    [InlineData("2024_10_26")]
    public async Task Upload_AcceptsOrdinaryVersionStrings(string versionString)
    {
        await _service.CreateAppAsync("My App");

        var result = await _service.UploadVersionAsync(
            "my-app",
            versionString,
            "",
            Body("v1"),
            "MyApp.exe"
        );

        Assert.True(result.Success, result.Error);
        Assert.Equal(versionString, result.Version!.VersionString);
    }

    // ── Deletion ──────────────────────────────────────────────────────────────

    /// <summary>
    /// Regression: deletion used to look the version up by id alone, so a
    /// request addressed to one app could delete another app's version.
    /// </summary>
    [Fact]
    public async Task DeleteVersion_WillNotDeleteAVersionBelongingToAnotherApp()
    {
        await _service.CreateAppAsync("My App");
        await _service.CreateAppAsync("Other App");
        var mine = await _service.UploadVersionAsync(
            "my-app",
            "1.0.0",
            "",
            Body("v1"),
            "MyApp.exe"
        );

        var deleted = await _service.DeleteVersionAsync("other-app", mine.Version!.Id);

        Assert.False(deleted);
        Assert.Single(await _service.GetVersionHistoryAsync("my-app"));
        Assert.True(File.Exists(Path.Combine(_binariesPath, mine.Version.FileName)));
    }

    [Fact]
    public async Task DeleteVersion_RemovesTheRowAndTheBinary()
    {
        await _service.CreateAppAsync("My App");
        var version = await _service.UploadVersionAsync(
            "my-app",
            "1.0.0",
            "",
            Body("v1"),
            "MyApp.exe"
        );

        var deleted = await _service.DeleteVersionAsync("my-app", version.Version!.Id);

        Assert.True(deleted);
        Assert.Empty(await _service.GetVersionHistoryAsync("my-app"));
        Assert.False(File.Exists(Path.Combine(_binariesPath, version.Version.FileName)));
    }

    [Fact]
    public async Task DeleteApp_RemovesEveryBinaryItOwns()
    {
        var app = await _service.CreateAppAsync("My App");
        await _service.UploadVersionAsync("my-app", "1.0.0", "", Body("v1"), "MyApp.exe");
        await _service.UploadVersionAsync("my-app", "1.1.0", "", Body("v2"), "MyApp.exe");

        var deleted = await _service.DeleteAppAsync(app.Id);

        Assert.True(deleted);
        Assert.Empty(Directory.GetFiles(_binariesPath));
        Assert.Null(await _service.GetAppBySlugAsync("my-app"));
    }

    // ── Queries ───────────────────────────────────────────────────────────────

    [Fact]
    public async Task GetLatestVersion_ReturnsTheMostRecentlyUploadedVersion()
    {
        await _service.CreateAppAsync("My App");
        await _service.UploadVersionAsync("my-app", "1.0.0", "", Body("v1"), "MyApp.exe");
        await _service.UploadVersionAsync("my-app", "1.1.0", "", Body("v2"), "MyApp.exe");

        var latest = await _service.GetLatestVersionAsync("my-app");

        Assert.Equal("1.1.0", latest!.VersionString);
    }

    [Fact]
    public async Task GetBinary_ReturnsNullWhenTheFileIsMissingFromDisk()
    {
        await _service.CreateAppAsync("My App");
        var version = await _service.UploadVersionAsync(
            "my-app",
            "1.0.0",
            "",
            Body("v1"),
            "MyApp.exe"
        );
        File.Delete(Path.Combine(_binariesPath, version.Version!.FileName));

        Assert.Null(await _service.GetBinaryAsync("my-app", "1.0.0"));
    }

    [Fact]
    public async Task CreateApp_GivesTheSecondAppWithAColllidingNameItsOwnSlug()
    {
        var first = await _service.CreateAppAsync("My App");
        var second = await _service.CreateAppAsync("my app");

        Assert.Equal("my-app", first.Slug);
        Assert.Equal("my-app-1", second.Slug);
    }

    [Theory]
    [InlineData("My App", "my-app")]
    [InlineData("  Desktop Client  ", "desktop-client")]
    [InlineData("App (v2)!", "app-v2")]
    public void GenerateSlug_NormalisesNames(string name, string expected) =>
        Assert.Equal(expected, UpdateService.GenerateSlug(name));

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static Stream Body(string content) => new MemoryStream(Encoding.UTF8.GetBytes(content));

    private static string Sha256(string content) =>
        Convert
            .ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(content)))
            .ToLowerInvariant();

    private async Task<string> ReadBinary(string fileName) =>
        await File.ReadAllTextAsync(Path.Combine(_binariesPath, fileName));
}

internal static class SqliteConnectionPool
{
    /// <summary>
    /// SQLite keeps the database file open in a pooled connection, which stops
    /// the temp directory from being deleted on Windows.
    /// </summary>
    public static void Clear() => Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
}
