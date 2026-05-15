using System.CommandLine;
using Spectre.Console;

var serverOption = new Option<string>("--server", "Server base URL") { IsRequired = true };
var apiKeyOption = new Option<string>("--api-key", "API key for authentication")
{
    IsRequired = true,
};
var appOption = new Option<string>("--app", "Application name") { IsRequired = true };
var versionOption = new Option<string>("--version", "Version string") { IsRequired = true };
var fileOption = new Option<FileInfo>("--file", "Path to the installer file") { IsRequired = true };
var notesOption = new Option<string>("--notes", () => "", "Release notes");

var uploadCommand = new Command("upload", "Upload a new version to the update server")
{
    serverOption,
    apiKeyOption,
    appOption,
    versionOption,
    fileOption,
    notesOption,
};

uploadCommand.SetHandler(
    async (server, apiKey, app, version, file, notes) =>
    {
        if (!file.Exists)
        {
            AnsiConsole.MarkupLine($"[red]Error:[/] File not found: {file.FullName}");
            return;
        }

        AnsiConsole.MarkupLine($"[bold]App Update Server CLI[/]");
        AnsiConsole.MarkupLine($"  App:     [cyan]{app}[/]");
        AnsiConsole.MarkupLine($"  Version: [cyan]{version}[/]");
        AnsiConsole.MarkupLine($"  File:    [cyan]{file.Name}[/] ({FormatSize(file.Length)})");
        AnsiConsole.MarkupLine($"  Server:  [cyan]{server}[/]");
        AnsiConsole.WriteLine();

        await AnsiConsole
            .Progress()
            .StartAsync(async ctx =>
            {
                var task = ctx.AddTask("Uploading", maxValue: file.Length);

                using var httpClient = new HttpClient();
                httpClient.DefaultRequestHeaders.Add("X-API-Key", apiKey);
                httpClient.Timeout = TimeSpan.FromMinutes(30);

                await using var fileStream = file.OpenRead();
                using var progressStream = new ProgressStream(
                    fileStream,
                    bytes => task.Increment(bytes)
                );

                using var content = new MultipartFormDataContent
                {
                    { new StringContent(app), "appName" },
                    { new StringContent(version), "version" },
                    { new StringContent(notes), "releaseNotes" },
                    { new StreamContent(progressStream), "updateFile", file.Name },
                };

                try
                {
                    var response = await httpClient.PostAsync(
                        $"{server.TrimEnd('/')}/api/update/upload",
                        content
                    );
                    var body = await response.Content.ReadAsStringAsync();

                    AnsiConsole.WriteLine();
                    if (response.IsSuccessStatusCode)
                        AnsiConsole.MarkupLine($"[green]Success![/] Version {version} uploaded.");
                    else
                        AnsiConsole.MarkupLine($"[red]Failed:[/] {response.StatusCode} — {body}");
                }
                catch (Exception ex)
                {
                    AnsiConsole.MarkupLine($"[red]Error:[/] {ex.Message}");
                }
            });
    },
    serverOption,
    apiKeyOption,
    appOption,
    versionOption,
    fileOption,
    notesOption
);

var rootCommand = new RootCommand("aus — App Update Server CLI") { uploadCommand };
return await rootCommand.InvokeAsync(args);

static string FormatSize(long bytes) =>
    bytes switch
    {
        < 1024 => $"{bytes} B",
        < 1024 * 1024 => $"{bytes / 1024.0:F1} KB",
        _ => $"{bytes / 1024.0 / 1024.0:F1} MB",
    };

public class ProgressStream(Stream inner, Action<long> onProgress) : Stream
{
    public override bool CanRead => inner.CanRead;
    public override bool CanSeek => inner.CanSeek;
    public override bool CanWrite => inner.CanWrite;
    public override long Length => inner.Length;
    public override long Position
    {
        get => inner.Position;
        set => inner.Position = value;
    }

    public override int Read(byte[] buffer, int offset, int count)
    {
        var bytesRead = inner.Read(buffer, offset, count);
        onProgress(bytesRead);
        return bytesRead;
    }

    public override async Task<int> ReadAsync(
        byte[] buffer,
        int offset,
        int count,
        CancellationToken ct
    )
    {
        var bytesRead = await inner.ReadAsync(buffer, offset, count, ct);
        onProgress(bytesRead);
        return bytesRead;
    }

    public override void Flush() => inner.Flush();

    public override long Seek(long offset, SeekOrigin origin) => inner.Seek(offset, origin);

    public override void SetLength(long value) => inner.SetLength(value);

    public override void Write(byte[] buffer, int offset, int count) =>
        inner.Write(buffer, offset, count);
}
