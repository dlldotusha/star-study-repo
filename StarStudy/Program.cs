using Microsoft.AspNetCore.DataProtection;
using StarStudy.Components;
using StarStudy.Endpoints;
using StarStudy.Services;

var builder = WebApplication.CreateBuilder(args);

builder.Logging.ClearProviders();
builder.Logging.AddConsole();
builder.Logging.AddDebug();

builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents()
    .AddInteractiveWebAssemblyComponents();
builder.Services.AddSingleton<TestSessionStore>();
builder.Services.AddDataProtection()
    .PersistKeysToFileSystem(new DirectoryInfo(Path.Combine(builder.Environment.ContentRootPath, "DataProtectionKeys")));

var app = builder.Build();

if (app.Environment.IsDevelopment())
{
    app.UseWebAssemblyDebugging();
}
else
{
    app.UseExceptionHandler("/Error", createScopeForErrors: true);
    app.UseHsts();
}
app.UseStatusCodePagesWithReExecute("/not-found", createScopeForStatusCodePages: true);

var hasHttpsEndpoint = app.Urls.Any(url => url.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
    || !string.IsNullOrWhiteSpace(app.Configuration["HTTPS_PORT"])
    || !string.IsNullOrWhiteSpace(app.Configuration["ASPNETCORE_HTTPS_PORT"]);

if (hasHttpsEndpoint)
{
    app.UseHttpsRedirection();
}

app.UseAntiforgery();

app.MapGet("/healthz", () => Results.Ok(new { Status = "ok", App = "Star-Study" }));
app.MapTestApi();
app.MapAdminApi();
app.MapStaticAssets();
app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode()
    .AddInteractiveWebAssemblyRenderMode()
    .AddAdditionalAssemblies(
        typeof(StarStudy.Client._Imports).Assembly,
        typeof(StarStudy.Admin._Imports).Assembly);

app.Run();
