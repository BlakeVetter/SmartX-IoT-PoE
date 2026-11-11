
using System.Globalization;
using SmartX.Shared;

var builder = WebApplication.CreateBuilder(args);

// Allow our Blazor WebAssembly client to access this API.
builder.Services.AddCors(options =>
{
    options.AddPolicy("SmartXClient", policy =>
        policy.WithOrigins("http://localhost:5200")
              .AllowAnyHeader()
              .AllowAnyMethod());
});

// In-memory simulation database.
builder.Services.AddSingleton<SmartXStore>();

var app = builder.Build();

app.UseCors("SmartXClient");

// API health.
app.MapGet("/api/health", () => Results.Ok(new
{
    message = "Smart-X API is online",
    checkedAtUtc = DateTimeOffset.UtcNow
}));

// Dashboard and device endpoints.
app.MapGet("/api/dashboard", (SmartXStore store) =>
    Results.Ok(store.Snapshot()));

app.MapGet("/api/devices", (SmartXStore store) =>
    Results.Ok(store.Snapshot().Devices));

app.MapGet("/api/uploads", (SmartXStore store) =>
    Results.Ok(store.Snapshot().Uploads));

// Register a new IoT device.
app.MapPost("/api/devices",
    (RegisterDeviceRequest request, SmartXStore store) =>
    {
        if (!store.TryRegister(request, out var device, out var error))
            return Results.BadRequest(new { message = error });

        return Results.Created(
            $"/api/devices/{Uri.EscapeDataString(device!.DeviceId)}",
            device);
    });

// FLOAT telemetry: environmental sensor readings.
app.MapPost("/api/telemetry/float",
    (TelemetryPacket<float> packet, SmartXStore store) =>
    {
        if (!float.IsFinite(packet.Value))
            return Results.BadRequest(new
            {
                message = "Float value must be finite."
            });

        bool alert = packet.Metric.Contains(
            "moisture",
            StringComparison.OrdinalIgnoreCase)
                ? packet.Value < 20
                : packet.Value > 100;

        bool accepted = store.TryRecord(
            packet,
            "float",
            packet.Value.ToString("0.0", CultureInfo.InvariantCulture),
            packet.Value,
            alert,
            out var error);

        return accepted
            ? Results.Ok(new { message = "Float telemetry accepted." })
            : Results.BadRequest(new { message = error });
    });

// INTEGER telemetry: power consumption.
app.MapPost("/api/telemetry/int",
    (TelemetryPacket<int> packet, SmartXStore store) =>
    {
        bool alert = packet.Metric.Contains(
            "power",
            StringComparison.OrdinalIgnoreCase)
                && packet.Value > 800;

        bool accepted = store.TryRecord(
            packet,
            "integer",
            packet.Value.ToString(CultureInfo.InvariantCulture),
            packet.Value,
            alert,
            out var error);

        return accepted
            ? Results.Ok(new { message = "Integer telemetry accepted." })
            : Results.BadRequest(new { message = error });
    });

// BOOLEAN telemetry: switch and valve states.
app.MapPost("/api/telemetry/bool",
    (TelemetryPacket<bool> packet, SmartXStore store) =>
    {
        bool accepted = store.TryRecord(
            packet,
            "boolean",
            packet.Value ? "ON" : "OFF",
            null,
            false,
            out var error);

        return accepted
            ? Results.Ok(new { message = "Boolean telemetry accepted." })
            : Results.BadRequest(new { message = error });
    });

// Generate sample readings from every registered sensor.
app.MapPost("/api/simulate", (SmartXStore store) =>
    Results.Ok(new
    {
        generated = store.SimulateRound()
    }));

// Upload a physical device image, configuration or log.
app.MapPost("/api/uploads", async (
    HttpRequest request,
    SmartXStore store,
    IWebHostEnvironment environment) =>
{
    if (!request.HasFormContentType)
        return Results.BadRequest(new
        {
            message = "Use multipart/form-data."
        });

    var form = await request.ReadFormAsync();
    var deviceId = form["deviceId"].ToString();
    var file = form.Files.GetFile("file");

    if (!store.DeviceExists(deviceId))
        return Results.BadRequest(new
        {
            message = "Select a registered device first."
        });

    if (file is null || file.Length == 0 ||
        file.Length > 5 * 1024 * 1024)
        return Results.BadRequest(new
        {
            message = "File must be between 1 byte and 5 MB."
        });

    var extension = Path.GetExtension(file.FileName)
        .ToLowerInvariant();

    if (extension is not
        (".png" or ".jpg" or ".jpeg" or ".pdf" or ".txt" or ".log"))
        return Results.BadRequest(new
        {
            message = "Unsupported file type."
        });

    var directory = Path.Combine(
        environment.ContentRootPath,
        "Uploads");

    Directory.CreateDirectory(directory);

    var storedName = $"{Guid.NewGuid():N}{extension}";
    var filePath = Path.Combine(directory, storedName);

    await using (var stream = File.Create(filePath))
    {
        await file.CopyToAsync(stream);
    }

    var saved = new UploadInfo(
        deviceId.Trim().ToUpperInvariant(),
        Path.GetFileName(file.FileName),
        storedName,
        file.Length,
        DateTimeOffset.UtcNow);

    store.RecordUpload(saved);

    return Results.Ok(saved);
}).DisableAntiforgery();

// Fixed local development address.
app.Run("http://localhost:5010");


// ========================================
// SMART-X IN-MEMORY INGESTION ENGINE
// ========================================

sealed class SmartXStore
{
    private readonly object gate = new();

    private readonly Dictionary<string, IoTDevice> devices =
        new(StringComparer.OrdinalIgnoreCase);

    private readonly List<TelemetryReading> readings = new();
    private readonly List<UploadInfo> uploads = new();

    private int totalPackets;
    private int totalAlerts;

    public SmartXStore()
    {
        TryRegister(
            new("ESP32-FARM-A", "Hydroponic Farm / Zone A",
                "Environmental"),
            out _, out _);

        TryRegister(
            new("ESP32-FARM-B", "Hydroponic Farm / Zone B",
                "Environmental"),
            out _, out _);

        TryRegister(
            new("ESP32-GRID-1", "Smart Grid / Main Panel",
                "Power"),
            out _, out _);

        TryRegister(
            new("ESP32-VALVE-1", "Farm / Valve Rack",
                "Actuator"),
            out _, out _);

        // Seed sample readings on startup.
        for (int i = 0; i < 5; i++)
            SimulateRound();
    }

    public bool TryRegister(
        RegisterDeviceRequest request,
        out IoTDevice? device,
        out string error)
    {
        device = null;
        error = "";

        if (string.IsNullOrWhiteSpace(request.DeviceId) ||
            request.DeviceId.Trim().Length > 48 ||
            string.IsNullOrWhiteSpace(request.Location) ||
            request.Location.Trim().Length > 100 ||
            request.Category is not
                ("Environmental" or "Power" or "Actuator"))
        {
            error = "Invalid device ID, location or category.";
            return false;
        }

        string id = request.DeviceId.Trim().ToUpperInvariant();

        lock (gate)
        {
            if (devices.ContainsKey(id))
            {
                error = "Device ID already registered.";
                return false;
            }

            device = new IoTDevice(
                id,
                request.Location.Trim(),
                request.Category,
                DateTimeOffset.UtcNow);

            devices.Add(id, device);
        }

        return true;
    }

    public bool DeviceExists(string id)
    {
        lock (gate)
        {
            return !string.IsNullOrWhiteSpace(id)
                && devices.ContainsKey(id.Trim());
        }
    }

    public bool TryRecord<T>(
        TelemetryPacket<T> packet,
        string dataType,
        string displayValue,
        double? numericValue,
        bool isAlert,
        out string error)
    {
        error = "";

        if (string.IsNullOrWhiteSpace(packet.DeviceId) ||
            string.IsNullOrWhiteSpace(packet.Metric) ||
            packet.Metric.Length > 70)
        {
            error = "Device ID and metric are required.";
            return false;
        }

        lock (gate)
        {
            if (!devices.ContainsKey(packet.DeviceId.Trim()))
            {
                error = "Device is not registered.";
                return false;
            }

            readings.Add(new TelemetryReading(
                packet.DeviceId.Trim().ToUpperInvariant(),
                packet.Metric.Trim(),
                dataType,
                displayValue,
                numericValue,
                DateTimeOffset.UtcNow,
                isAlert));

            totalPackets++;

            if (isAlert)
                totalAlerts++;

            if (readings.Count > 1000)
                readings.RemoveRange(0, readings.Count - 1000);
        }

        return true;
    }

    public int SimulateRound()
    {
        List<IoTDevice> registered;

        lock (gate)
        {
            registered = devices.Values.ToList();
        }

        int generated = 0;

        foreach (var device in registered)
        {
            bool accepted;

            switch (device.Category)
            {
                case "Environmental":
                    {
                        float moisture = (float)(
                            10 + Random.Shared.NextDouble() * 68);

                        accepted = TryRecord(
                            new TelemetryPacket<float>(
                                device.DeviceId,
                                "Soil Moisture (%)",
                                moisture),
                            "float",
                            moisture.ToString(
                                "0.0", CultureInfo.InvariantCulture),
                            moisture,
                            moisture < 20,
                            out _);

                        break;
                    }

                case "Power":
                    {
                        int watts = Random.Shared.Next(250, 1000);

                        accepted = TryRecord(
                            new TelemetryPacket<int>(
                                device.DeviceId,
                                "Power Usage (W)",
                                watts),
                            "integer",
                            watts.ToString(
                                CultureInfo.InvariantCulture),
                            watts,
                            watts > 800,
                            out _);

                        break;
                    }

                default:
                    {
                        bool isOpen = Random.Shared.Next(2) == 1;

                        accepted = TryRecord(
                            new TelemetryPacket<bool>(
                                device.DeviceId,
                                "Valve Open",
                                isOpen),
                            "boolean",
                            isOpen ? "ON" : "OFF",
                            null,
                            false,
                            out _);

                        break;
                    }
            }

            if (accepted)
                generated++;
        }

        return generated;
    }

    public void RecordUpload(UploadInfo upload)
    {
        lock (gate)
        {
            uploads.Add(upload);
        }
    }

    public DashboardSnapshot Snapshot()
    {
        lock (gate)
        {
            return new DashboardSnapshot(
                devices.Values
                    .OrderBy(d => d.DeviceId)
                    .ToList(),

                readings
                    .TakeLast(60)
                    .Reverse()
                    .ToList(),

                uploads
                    .AsEnumerable()
                    .Reverse()
                    .Take(20)
                    .ToList(),

                totalPackets,
                totalAlerts,
                DateTimeOffset.UtcNow);
        }
    }
}
