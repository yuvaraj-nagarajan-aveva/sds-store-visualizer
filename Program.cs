using System.Text.Json.Serialization;
using Aveva.Sds.StoreVisualizer.Endpoints;
using Aveva.Sds.StoreVisualizer.Services;

var builder = WebApplication.CreateBuilder(args);

var configuredUrls = Environment.GetEnvironmentVariable("ASPNETCORE_URLS");
if (string.IsNullOrWhiteSpace(configuredUrls))
{
    builder.WebHost.UseUrls("http://127.0.0.1:5175");
}

builder.Services.ConfigureHttpJsonOptions(options =>
{
    options.SerializerOptions.Converters.Add(new JsonStringEnumConverter());
    options.SerializerOptions.WriteIndented = true;
});
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSingleton<StoreFileInspector>();
builder.Services.AddSingleton<StoreInspectionService>();

var app = builder.Build();

app.UseDefaultFiles();
app.UseStaticFiles();
app.MapStoreInspectionEndpoints();
app.MapFallbackToFile("index.html");

app.Run();

public partial class Program
{
}
