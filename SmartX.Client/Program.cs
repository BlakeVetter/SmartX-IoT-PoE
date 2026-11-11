
using Microsoft.AspNetCore.Components.Web;
using Microsoft.AspNetCore.Components.WebAssembly.Hosting;
using SmartX.Client;

var builder = WebAssemblyHostBuilder.CreateDefault(args);

builder.RootComponents.Add<App>("#app");
builder.RootComponents.Add<HeadOutlet>("head::after");

// Connect Blazor to the Smart-X API.
builder.Services.AddScoped(_ => new HttpClient
{
    BaseAddress = new Uri("http://localhost:5010/")
});

await builder.Build().RunAsync();
