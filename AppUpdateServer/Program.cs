using AppUpdateServer.Components;
using AppUpdateServer.Data;
using AppUpdateServer.Services;
using Microsoft.EntityFrameworkCore;
using MudBlazor.Services;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddDbContext<AppDbContext>(opts =>
    opts.UseSqlite(
        builder.Configuration.GetConnectionString("Default") ?? "Data Source=/data/updateserver.db"
    )
);

builder.Services.AddScoped<UpdateService>();
builder.Services.AddControllers();
builder.Services.AddMudServices();

// Add services to the container.
builder.Services.AddRazorComponents().AddInteractiveServerComponents();

var app = builder.Build();

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
app.UseHttpsRedirection();

// Add a middleware to check forward-auth
app.Use(
    async (context, next) =>
    {
        var forwardAuth = app.Configuration.GetValue<bool>("Auth:ForwardAuth");
        if (forwardAuth)
        {
            // Only protect UI routes, not public API endpoints
            var path = context.Request.Path.Value ?? "";
            var isPublicApi =
                path.StartsWith("/api/update/download")
                || (path == "/api/update" && context.Request.Method == "POST");
            var isUploadApi = path == "/api/update/upload";

            if (!isPublicApi && !isUploadApi)
            {
                var remoteUser = context.Request.Headers["Remote-User"].ToString();
                if (string.IsNullOrEmpty(remoteUser))
                {
                    context.Response.StatusCode = 401;
                    await context.Response.WriteAsync("Unauthorized");
                    return;
                }
            }
        }
        await next();
    }
);

app.UseStaticFiles();
app.UseAntiforgery();

app.MapControllers();

app.MapStaticAssets();
app.MapRazorComponents<App>().AddInteractiveServerRenderMode();

app.Run();
