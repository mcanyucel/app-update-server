using System.CommandLine;
using System.Net.Http.Json;
using Spectre.Console;

// ── Shared options ────────────────────────────────────────────────────────────

var serverOption = new Option<string>("--server", "Server base URL") { IsRequired = true };
var apiKeyOption = new Option<string>("--api-key", "API key for authentication")
{
    IsRequired = true,
};
var appOption = new Option<string>("--app", "Application slug") { IsRequired = true };
var versionOption = new Option<string>("--version", "Version string") { IsRequired = true };
var fileOption = new Option<FileInfo>("--file", "Path to the installer file") { IsRequired = true };
var notesOption = new Option<string>("--notes", () => "", "Release notes");
var versionIdOption = new Option<int>("--version-id", "Version ID") { IsRequired = true };

// ── Upload ────────────────────────────────────────────────────────────────────

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

        PrintHeader(app, version, file.Name, FormatSize(file.Length), server);

        await AnsiConsole
            .Progress()
            .StartAsync(async ctx =>
            {
                var task = ctx.AddTask("Uploading", maxValue: file.Length);
                using var http = MakeClient(apiKey);

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

                var response = await http.PostAsync(
                    $"{server.TrimEnd('/')}/api/update/upload",
                    content
                );
                var body = await response.Content.ReadAsStringAsync();

                AnsiConsole.WriteLine();
                if (response.IsSuccessStatusCode)
                    AnsiConsole.MarkupLine($"[green]Success![/] Version {version} uploaded.");
                else
                    AnsiConsole.MarkupLine($"[red]Failed:[/] {response.StatusCode} — {body}");
            });
    },
    serverOption,
    apiKeyOption,
    appOption,
    versionOption,
    fileOption,
    notesOption
);

// ── List apps ─────────────────────────────────────────────────────────────────

var listCommand = new Command("list", "List all tracked applications");
listCommand.AddOption(serverOption);
listCommand.AddOption(apiKeyOption);

listCommand.SetHandler(
    async (server, apiKey) =>
    {
        using var http = MakeClient(apiKey);
        var response = await http.GetAsync($"{server.TrimEnd('/')}/api/update/apps");

        if (!response.IsSuccessStatusCode)
        {
            AnsiConsole.MarkupLine($"[red]Failed:[/] {response.StatusCode}");
            return;
        }

        var apps = await response.Content.ReadFromJsonAsync<List<AppSummary>>();
        if (apps is null || apps.Count == 0)
        {
            AnsiConsole.MarkupLine("[yellow]No apps registered.[/]");
            return;
        }

        var table = new Table();
        table.AddColumn("Name");
        table.AddColumn("Slug");
        table.AddColumn("Latest Version");
        table.AddColumn("Uploaded");
        table.AddColumn("Versions");

        foreach (var app in apps)
        {
            table.AddRow(
                app.Name,
                app.Slug,
                app.Latest?.VersionString ?? "[grey]none[/]",
                app.Latest?.UploadedAt.ToString("yyyy-MM-dd HH:mm") ?? "-",
                app.TotalVersions.ToString()
            );
        }

        AnsiConsole.Write(table);
    },
    serverOption,
    apiKeyOption
);

// ── List versions ─────────────────────────────────────────────────────────────

var listVersionsCommand = new Command("versions", "List versions for an app");
listVersionsCommand.AddOption(serverOption);
listVersionsCommand.AddOption(apiKeyOption);
listVersionsCommand.AddOption(appOption);

listVersionsCommand.SetHandler(
    async (server, apiKey, app) =>
    {
        using var http = MakeClient(apiKey);
        var response = await http.GetAsync($"{server.TrimEnd('/')}/api/update/apps/{app}/versions");

        if (!response.IsSuccessStatusCode)
        {
            AnsiConsole.MarkupLine($"[red]Failed:[/] {response.StatusCode}");
            return;
        }

        var versions = await response.Content.ReadFromJsonAsync<List<VersionSummary>>();
        if (versions is null || versions.Count == 0)
        {
            AnsiConsole.MarkupLine("[yellow]No versions found.[/]");
            return;
        }

        var table = new Table();
        table.AddColumn("ID");
        table.AddColumn("Version");
        table.AddColumn("Uploaded");
        table.AddColumn("Size");
        table.AddColumn("Notes");

        foreach (var (v, i) in versions.Select((v, i) => (v, i)))
        {
            var versionStr = i == 0 ? $"[green]{v.VersionString} (latest)[/]" : v.VersionString;
            table.AddRow(
                v.Id.ToString(),
                versionStr,
                v.UploadedAt.ToString("yyyy-MM-dd HH:mm"),
                FormatSize(v.FileSizeBytes),
                v.ReleaseNotes
            );
        }

        AnsiConsole.Write(table);
    },
    serverOption,
    apiKeyOption,
    appOption
);

// ── Rollback ──────────────────────────────────────────────────────────────────

var rollbackCommand = new Command("rollback", "Delete the latest version, promoting the previous");
rollbackCommand.AddOption(serverOption);
rollbackCommand.AddOption(apiKeyOption);
rollbackCommand.AddOption(appOption);

rollbackCommand.SetHandler(
    async (server, apiKey, app) =>
    {
        using var http = MakeClient(apiKey);
        var response = await http.PostAsync(
            $"{server.TrimEnd('/')}/api/update/apps/{app}/rollback",
            null
        );
        var body = await response.Content.ReadAsStringAsync();

        if (response.IsSuccessStatusCode)
            AnsiConsole.MarkupLine($"[green]Rolled back.[/] Previous version is now current.");
        else
            AnsiConsole.MarkupLine($"[red]Failed:[/] {response.StatusCode} — {body}");
    },
    serverOption,
    apiKeyOption,
    appOption
);

// ── Delete version ────────────────────────────────────────────────────────────

var deleteVersionCommand = new Command("delete-version", "Delete a specific version by ID");
deleteVersionCommand.AddOption(serverOption);
deleteVersionCommand.AddOption(apiKeyOption);
deleteVersionCommand.AddOption(appOption);
deleteVersionCommand.AddOption(versionIdOption);

deleteVersionCommand.SetHandler(
    async (server, apiKey, app, versionId) =>
    {
        using var http = MakeClient(apiKey);
        var response = await http.DeleteAsync(
            $"{server.TrimEnd('/')}/api/update/apps/{app}/versions/{versionId}"
        );

        if (response.IsSuccessStatusCode)
            AnsiConsole.MarkupLine($"[green]Deleted[/] version {versionId}.");
        else
            AnsiConsole.MarkupLine($"[red]Failed:[/] {response.StatusCode}");
    },
    serverOption,
    apiKeyOption,
    appOption,
    versionIdOption
);

// ── Delete app ────────────────────────────────────────────────────────────────

var deleteAppCommand = new Command("delete-app", "Delete an app and all its versions");
deleteAppCommand.AddOption(serverOption);
deleteAppCommand.AddOption(apiKeyOption);
deleteAppCommand.AddOption(appOption);

deleteAppCommand.SetHandler(
    async (server, apiKey, app) =>
    {
        AnsiConsole.MarkupLine(
            $"[yellow]Warning:[/] This will delete app '{app}' and all its versions."
        );

        if (!AnsiConsole.Confirm("Are you sure?"))
            return;

        using var http = MakeClient(apiKey);
        var response = await http.DeleteAsync($"{server.TrimEnd('/')}/api/update/apps/{app}");

        if (response.IsSuccessStatusCode)
            AnsiConsole.MarkupLine($"[green]Deleted[/] app '{app}'.");
        else
            AnsiConsole.MarkupLine($"[red]Failed:[/] {response.StatusCode}");
    },
    serverOption,
    apiKeyOption,
    appOption
);

// ── Root command ──────────────────────────────────────────────────────────────

var rootCommand = new RootCommand("aus — App Update Server CLI")
{
    uploadCommand,
    listCommand,
    listVersionsCommand,
    rollbackCommand,
    deleteVersionCommand,
    deleteAppCommand,
};

return await rootCommand.InvokeAsync(args);

// ── Helpers ───────────────────────────────────────────────────────────────────

static HttpClient MakeClient(string apiKey)
{
    var http = new HttpClient { Timeout = TimeSpan.FromMinutes(30) };
    http.DefaultRequestHeaders.Add("X-API-Key", apiKey);
    return http;
}

static void PrintHeader(string app, string version, string fileName, string size, string server)
{
    AnsiConsole.MarkupLine($"[bold]App Update Server CLI[/]");
    AnsiConsole.MarkupLine($"  App:     [cyan]{app}[/]");
    AnsiConsole.MarkupLine($"  Version: [cyan]{version}[/]");
    AnsiConsole.MarkupLine($"  File:    [cyan]{fileName}[/] ({size})");
    AnsiConsole.MarkupLine($"  Server:  [cyan]{server}[/]");
    AnsiConsole.WriteLine();
}

static string FormatSize(long bytes) =>
    bytes switch
    {
        < 1024 => $"{bytes} B",
        < 1024 * 1024 => $"{bytes / 1024.0:F1} KB",
        _ => $"{bytes / 1024.0 / 1024.0:F1} MB",
    };

// ── DTOs (mirror server records) ──────────────────────────────────────────────

record AppSummary(int Id, string Name, string Slug, VersionSummary? Latest, int TotalVersions);

record VersionSummary(
    int Id,
    string VersionString,
    string Sha256Hash,
    string ReleaseNotes,
    DateTime UploadedAt,
    long FileSizeBytes,
    string FileName
);

// ── Progress stream ───────────────────────────────────────────────────────────

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
