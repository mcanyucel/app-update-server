using System.Text;
using AppUpdateServer.Services;
using Microsoft.Extensions.Configuration;

namespace AppUpdateServer.Tests;

public class ProgressReportingStreamTests
{
    [Fact]
    public async Task CopyingTheStream_ReportsEveryByteExactlyOnce()
    {
        var payload = Encoding.UTF8.GetBytes(new string('x', 100_000));
        var reports = new List<long>();

        await using var source = new MemoryStream(payload);
        await using var progress = new ProgressReportingStream(source, reports.Add);
        using var destination = new MemoryStream();

        await progress.CopyToAsync(destination, bufferSize: 4096);

        Assert.Equal(payload, destination.ToArray());
        Assert.NotEmpty(reports);
        Assert.Equal(payload.Length, reports[^1]);
        Assert.Equal(reports.OrderBy(r => r), reports); // monotonically increasing
    }

    [Fact]
    public async Task TheReportedTotal_MatchesWhatTheUploadActuallyStored()
    {
        var root = Path.Combine(Path.GetTempPath(), "aus-tests", Guid.NewGuid().ToString("N"));
        var binaries = Path.Combine(root, "binaries");
        Directory.CreateDirectory(binaries);

        try
        {
            var payload = Encoding.UTF8.GetBytes(new string('y', 50_000));
            long reported = 0;

            await using var source = new MemoryStream(payload);
            await using var progress = new ProgressReportingStream(source, b => reported = b);
            using var destination = new MemoryStream();
            await progress.CopyToAsync(destination);

            Assert.Equal(payload.Length, reported);
            Assert.Equal(payload.Length, destination.Length);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Theory]
    [InlineData("/data/binaries", "/data/keys")]
    [InlineData("/srv/app/files/", "/srv/app/files/../keys")]
    [InlineData("binaries", "./keys")]
    public void KeysPath_SitsBesideTheBinariesDirectory(string binariesPath, string expected)
    {
        var config = Config(("Storage:BinariesPath", binariesPath));

        Assert.Equal(
            Path.GetFullPath(expected),
            Path.GetFullPath(UpdateService.GetKeysPath(config))
        );
    }

    [Fact]
    public void KeysPath_PrefersAnExplicitSetting()
    {
        var config = Config(
            ("Storage:BinariesPath", "/data/binaries"),
            ("Storage:KeysPath", "/elsewhere/keys")
        );

        Assert.Equal("/elsewhere/keys", UpdateService.GetKeysPath(config));
    }

    [Fact]
    public void KeysPath_DefaultsNextToTheDefaultBinariesLocation() =>
        Assert.Equal(
            Path.GetFullPath("/data/keys"),
            Path.GetFullPath(UpdateService.GetKeysPath(Config()))
        );

    private static IConfiguration Config(params (string Key, string Value)[] settings) =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(settings.ToDictionary(s => s.Key, s => (string?)s.Value))
            .Build();
}
