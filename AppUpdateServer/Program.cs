using System.Net;
using System.Net.Sockets;
using AppUpdateServer.Components;
using AppUpdateServer.Data;
using AppUpdateServer.Services;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.EntityFrameworkCore;
using Microsoft.FluentUI.AspNetCore.Components;

var builder = WebApplication.CreateBuilder(args);

// Installers are large: Kestrel (30 MB) and multipart form (128 MB) defaults
// are both well below a realistic upload, so lift them to one shared limit.
var maxUploadBytes = UpdateService.GetMaxUploadBytes(builder.Configuration);
builder.WebHost.ConfigureKestrel(opts => opts.Limits.MaxRequestBodySize = maxUploadBytes);
builder.Services.Configure<FormOptions>(opts =>
{
    opts.MultipartBodyLengthLimit = maxUploadBytes;
});

// Keep the keys on the data volume: the default location is inside the
// container, so recreating it invalidates every antiforgery token already
// issued to a browser. Falling back to ephemeral keys is better than
// crash-looping before /health can report why the volume is unusable.
var keysPath = UpdateService.GetKeysPath(builder.Configuration);
string? keysError = null;
try
{
    Directory.CreateDirectory(keysPath);
    builder
        .Services.AddDataProtection()
        .PersistKeysToFileSystem(new DirectoryInfo(keysPath))
        .SetApplicationName("AppUpdateServer");
}
catch (Exception ex)
{
    keysError = ex.Message;
}

builder.Services.AddDbContext<AppDbContext>(opts =>
    opts.UseSqlite(
        builder.Configuration.GetConnectionString("Default") ?? "Data Source=/data/updateserver.db"
    )
);

builder.Services.AddScoped<UpdateService>();
builder.Services.AddControllers();

// Fluent UI expects a default HttpClient to be registered on Blazor Server.
builder.Services.AddHttpClient();
builder.Services.AddFluentUIComponents();
builder.Services.AddHealthChecks().AddCheck<StorageHealthCheck>("storage");

// Add services to the container.
builder
    .Services.AddRazorComponents()
    .AddInteractiveServerComponents()
    // Browser uploads travel over the SignalR circuit in chunks. The 32 KB
    // default would make a large installer take tens of thousands of round
    // trips, so allow the larger buffer FluentInputFile is configured with.
    .AddHubOptions(opts => opts.MaximumReceiveMessageSize = 512 * 1024);

var app = builder.Build();

app.Logger.LogInformation("App Update Server {Version} starting", BuildInfo.DisplayWithCommit);

if (keysError is not null)
    app.Logger.LogWarning(
        "Could not persist data protection keys to '{KeysPath}' ({Error}). Antiforgery tokens "
            + "will be invalidated whenever the container is recreated.",
        keysPath,
        keysError
    );

// Perform auto-migration on start
using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
    db.Database.Migrate();
}

// Configure the HTTP request pipeline.
if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error", createScopeForErrors: true);
    // The default HSTS value is 30 days. You may want to change this for production scenarios, see https://aka.ms/aspnetcore-hsts.
    app.UseHsts();
}
app.UseStatusCodePagesWithReExecute("/not-found", createScopeForStatusCodePages: true);

// TLS is terminated at the reverse proxy, so the app itself only ever speaks
// plain HTTP — UseHttpsRedirection() would have nothing to redirect to.

var forwardAuth = app.Configuration.GetValue<bool>("Auth:ForwardAuth");
var trustedProxies = ParseTrustedProxies(app.Configuration["Auth:TrustedProxies"]);

if (forwardAuth && trustedProxies.Length == 0)
    app.Logger.LogWarning(
        "Auth:ForwardAuth is enabled but Auth:TrustedProxies is empty — anything that can reach "
            + "this container directly can authenticate by sending its own Remote-User header. "
            + "Set Auth:TrustedProxies to the address range of your reverse proxy."
    );

// Add a middleware to check forward-auth
app.Use(
    async (context, next) =>
    {
        if (forwardAuth && RequiresForwardAuth(context))
        {
            // The Remote-User header is only meaningful when the request actually
            // came from the proxy that authenticated it.
            if (trustedProxies.Length > 0 && !IsFromTrustedProxy(context, trustedProxies))
            {
                app.Logger.LogWarning(
                    "Rejected {Path}: Remote-User is only trusted from a configured proxy, not {Address}",
                    context.Request.Path,
                    context.Connection.RemoteIpAddress
                );
                context.Response.StatusCode = 401;
                await context.Response.WriteAsync("Unauthorized");
                return;
            }

            var remoteUser = context.Request.Headers["Remote-User"].ToString();
            if (string.IsNullOrEmpty(remoteUser))
            {
                context.Response.StatusCode = 401;
                await context.Response.WriteAsync("Unauthorized");
                return;
            }
        }
        await next();
    }
);

app.UseStaticFiles();
app.UseAntiforgery();

app.MapControllers();
app.MapHealthChecks("/health");

app.MapStaticAssets();
app.MapRazorComponents<App>().AddInteractiveServerRenderMode();

app.Run();

// Routes that installed apps, the CLI and container monitoring must reach
// without going through SSO. Upload and the management endpoints under
// /api/update carry their own X-API-Key check instead.
static bool RequiresForwardAuth(HttpContext context)
{
    var path = context.Request.Path.Value ?? "";
    if (path.StartsWith("/api/update/download") || path == "/health")
        return false;
    if (path == "/api/update" && HttpMethods.IsPost(context.Request.Method))
        return false;
    return path != "/api/update/upload";
}

static bool IsFromTrustedProxy(HttpContext context, IPNetwork[] trustedProxies)
{
    var address = context.Connection.RemoteIpAddress;
    if (address is null)
        return false;
    if (address.IsIPv4MappedToIPv6)
        address = address.MapToIPv4();
    return trustedProxies.Any(network => network.Contains(address));
}

// Accepts CIDR ranges ("172.18.0.0/16") and bare addresses ("10.0.0.5").
static IPNetwork[] ParseTrustedProxies(string? configured) =>
    (configured ?? "")
        .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
        .Select(entry =>
        {
            if (IPNetwork.TryParse(entry, out var network))
                return network;
            if (IPAddress.TryParse(entry, out var address))
                return new IPNetwork(
                    address,
                    address.AddressFamily == AddressFamily.InterNetworkV6 ? 128 : 32
                );
            throw new InvalidOperationException(
                $"Auth:TrustedProxies contains an invalid entry: '{entry}'"
            );
        })
        .ToArray();

// Exposed so the integration tests can spin the app up with WebApplicationFactory.
public partial class Program;
