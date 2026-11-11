
using System.Globalization;
using SmartX.Shared;

var builder = WebApplication.CreateBuilder(args);

// ==========================================
// SMART-X API CONFIGURATION
// ==========================================

builder.Services.AddCors(options =>
{
    options.AddPolicy("SmartXClient", policy =>
        policy.WithOrigins("http://localhost:5200")
              .AllowAnyHeader()
              .AllowAnyMethod());
});

// Part 1: Sensor registry and ingestion.
builder.Services.AddSingleton<SmartXStore>();

// Part 2: Real-time processing and commands.
builder.Services.AddSingleton<RealTimeEngine>();

// Final PoE: Mesh topology, routing and self-healing.
builder.Services.AddSingleton<MeshNetworkEngine>();

var app = builder.Build();

app.UseCors("SmartXClient");

// ==========================================
// HEALTH AND DASHBOARD
// ==========================================

app.MapGet("/api/health", () => Results.Ok(new
{
    message = "Smart-X API is online",
    checkedAtUtc = DateTimeOffset.UtcNow
}));

app.MapGet("/api/dashboard", (SmartXStore store) =>
    Results.Ok(store.Snapshot()));

app.MapGet("/api/devices", (SmartXStore store) =>
    Results.Ok(store.Snapshot().Devices));

app.MapGet("/api/uploads", (SmartXStore store) =>
    Results.Ok(store.Snapshot().Uploads));

// ==========================================
// DEVICE REGISTRATION
// ==========================================

app.MapPost("/api/devices",
    (RegisterDeviceRequest request, SmartXStore store) =>
    {
        if (!store.TryRegister(request, out var device, out var error))
            return Results.BadRequest(new { message = error });

        return Results.Created(
            $"/api/devices/{Uri.EscapeDataString(device!.DeviceId)}",
            device);
    });

// ==========================================
// FLOAT TELEMETRY
// ==========================================

app.MapPost("/api/telemetry/float",
    (TelemetryPacket<float> packet, SmartXStore store) =>
    {
    if (!float.IsFinite(packet.Value))
        return Results.BadRequest(new
        {
            message = "Value must be finite."
        });

    bool isMoisture = (packet.Metric ?? "").Contains(
        "moisture", StringComparison.OrdinalIgnoreCase);

    bool alert = isMoisture
        ? packet.Value < 20
        : packet.Value > 100;

    bool success = store.TryRecord(
        packet,
        "float",
        packet.Value.ToString("0.0", CultureInfo.InvariantCulture),
        packet.Value,
        alert,
        out var error);

    return success
        ? Results.Ok(new { message = "Float telemetry accepted." })
        : Results.BadRequest(new { message = error });