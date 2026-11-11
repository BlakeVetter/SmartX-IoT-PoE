
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
    });

// ==========================================
// INTEGER TELEMETRY
// ==========================================

app.MapPost("/api/telemetry/int",
    (TelemetryPacket<int> packet, SmartXStore store) =>
    {
        bool alert = (packet.Metric ?? "").Contains(
            "power", StringComparison.OrdinalIgnoreCase)
            && packet.Value > 800;

        bool success = store.TryRecord(
            packet,
            "integer",
            packet.Value.ToString(CultureInfo.InvariantCulture),
            packet.Value,
            alert,
            out var error);

        return success
            ? Results.Ok(new { message = "Integer telemetry accepted." })
            : Results.BadRequest(new { message = error });
    });

// ==========================================
// BOOLEAN TELEMETRY
// ==========================================

app.MapPost("/api/telemetry/bool",
    (TelemetryPacket<bool> packet, SmartXStore store) =>
    {
        bool success = store.TryRecord(
            packet,
            "boolean",
            packet.Value ? "ON" : "OFF",
            null,
            false,
            out var error);

        return success
            ? Results.Ok(new { message = "Boolean telemetry accepted." })
            : Results.BadRequest(new { message = error });
    });

// ==========================================
// SENSOR SIMULATION
// ==========================================

app.MapPost("/api/simulate", (SmartXStore store) =>
    Results.Ok(new
    {
        generated = store.SimulateRound()
    }));

// ==========================================
// FILE UPLOADS
// ==========================================

app.MapPost("/api/uploads", async (
    HttpRequest request,
    SmartXStore store,
    IWebHostEnvironment environment) =>
{
    if (!request.HasFormContentType)
        return Results.BadRequest(new
        {
            message = "Multipart form data required."
        });

    var form = await request.ReadFormAsync();

    string deviceId = form["deviceId"].ToString();
    var file = form.Files.GetFile("file");

    if (!store.DeviceExists(deviceId))
        return Results.BadRequest(new
        {
            message = "Device not registered."
        });

    if (file is null || file.Length == 0 ||
        file.Length > 5 * 1024 * 1024)
        return Results.BadRequest(new
        {
            message = "File must be between 1 byte and 5 MB."
        });

    string extension = Path.GetExtension(file.FileName)
        .ToLowerInvariant();

    if (extension is not
        (".png" or ".jpg" or ".jpeg" or ".pdf" or ".txt" or ".log"))
        return Results.BadRequest(new
        {
            message = "Unsupported file extension."
        });

    string directory = Path.Combine(
        environment.ContentRootPath, "Uploads");

    Directory.CreateDirectory(directory);

    string storedName = $"{Guid.NewGuid():N}{extension}";
    string fullPath = Path.Combine(directory, storedName);

    await using (var stream = File.Create(fullPath))
    {
        await file.CopyToAsync(stream);
    }

    var upload = new UploadInfo(
        deviceId.Trim().ToUpperInvariant(),
        Path.GetFileName(file.FileName),
        storedName,
        file.Length,
        DateTimeOffset.UtcNow);

    store.RecordUpload(upload);

    return Results.Ok(upload);

}).DisableAntiforgery();

// ==========================================
// PART 2 API ENDPOINTS
// ==========================================

// Registers the command stream, manual override
// and undo endpoints from RealTimeEndpoints.cs.
app.MapRealTimeEndpoints();

// ==========================================
// START SERVER
// ==========================================

app.Run("http://localhost:5010");


// ==========================================
// SMART-X IN-MEMORY SENSOR DATA STORE
// ==========================================

internal sealed class SmartXStore
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
            new("ESP32-FARM-A",
                "Hydroponic Farm / Zone A",
                "Environmental"),
            out _, out _);

        TryRegister(
            new("ESP32-FARM-B",
                "Hydroponic Farm / Zone B",
                "Environmental"),
            out _, out _);

        TryRegister(
            new("ESP32-GRID-1",
                "Smart Grid / Main Panel",
                "Power"),
            out _, out _);

        TryRegister(
            new("ESP32-VALVE-1",
                "Farm / Valve Rack",
                "Actuator"),
            out _, out _);

        for (int i = 0; i < 5; i++)
        {
            SimulateRound();
        }
    }

    // ======================================
    // DEVICE REGISTRATION
    // ======================================

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
                error = "Device ID already exists.";
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
            return !string.IsNullOrWhiteSpace(id) &&
                   devices.ContainsKey(id.Trim());
        }
    }

    // ======================================
    // GENERIC TELEMETRY INGESTION
    // ======================================

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

            var reading = new TelemetryReading(
                packet.DeviceId.Trim().ToUpperInvariant(),
                packet.Metric.Trim(),
                dataType,
                displayValue,
                numericValue,
                DateTimeOffset.UtcNow,
                isAlert);

            readings.Add(reading);

            totalPackets++;

            if (isAlert)
                totalAlerts++;

            if (readings.Count > 1000)
            {
                readings.RemoveRange(
                    0, readings.Count - 1000);
            }
        }

        return true;
    }

    // ======================================
    // SIMULATED SENSOR DATA
    // ======================================

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
            bool accepted = false;

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
                            watts.ToString(CultureInfo.InvariantCulture),
                            watts,
                            watts > 800,
                            out _);

                        break;
                    }

                case "Actuator":
                    {
                        bool valveOpen =
                            Random.Shared.Next(2) == 1;

                        accepted = TryRecord(
                            new TelemetryPacket<bool>(
                                device.DeviceId,
                                "Valve Open",
                                valveOpen),
                            "boolean",
                            valveOpen ? "ON" : "OFF",
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

    // ======================================
    // DEVICE UPLOAD HISTORY
    // ======================================

    public void RecordUpload(UploadInfo upload)
    {
        lock (gate)
        {
            uploads.Add(upload);
        }
    }

    // ======================================
    // DASHBOARD SNAPSHOT
    // ======================================

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
