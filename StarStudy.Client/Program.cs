using Microsoft.AspNetCore.Components.WebAssembly.Hosting;
using StarStudy.Admin.Services;
using StarStudy.Client.Services;

var builder = WebAssemblyHostBuilder.CreateDefault(args);

builder.Services.AddScoped(_ => new HttpClient
{
    BaseAddress = new Uri(builder.HostEnvironment.BaseAddress)
});
builder.Services.AddScoped<TestApiClient>();
builder.Services.AddScoped<AdminApiClient>();

await builder.Build().RunAsync();
