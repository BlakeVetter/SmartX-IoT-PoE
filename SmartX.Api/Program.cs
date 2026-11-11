
using System.Globalization;
using SmartX.Shared;

var builder = WebApplication.CreateBuilder(args);

// ==========================================
// SMART-X API CONFIGURATION
// ==========================================

builder.Services.AddCors(options =>
{
    options.AddPolicy("SmartXClient", policy =>
    {
        policy.WithOrigins("http://localhost:5200")
              .AllowAnyHeader()
              .AllowAnyMethod();
    });
});

// Part 1 - Sensor Data Ingestion
builder.Services.AddSingleton<SmartXStore>();

// Part 2 - Real-Time Processing
builder.Services.AddSingleton<RealTimeEngine>();

// Final PoE - Mesh Network and Routing
builder.Services.AddSingleton<MeshNetworkEngine>();

var app = builder.Build();

app.UseCors("SmartXClient");

// ==========================================
// API HEALTH CHECK
// ==========================================

app.MapGet("/api/health", () =>
{
    return Results.Ok(new
    {
        message = "Smart-X API is running successfully.",
        checkedAtUtc = DateTimeOffset.UtcNow
    });
});

// ==========================================
// PART 1 - DASHBOARD AND DEVICES
// ==========================================

app.MapGet("/api/dashboard", (SmartXStore store) =>
{
    return Results.Ok(store.Snapshot());
});

app.MapGet("/api/devices", (SmartXStore store) =>
{
    return Results.Ok(store.Snapshot().Devices);
});

app.MapGet("/api/uploads", (SmartXStore store) =>
{
    return Results.Ok(store.Snapshot().Uploads);
});

// ==========================================
// DEVICE REGISTRATION
// ==========================================

app.MapPost("/api/devices",
    (RegisterDeviceRequest request, SmartXStore store) =>
    {
        bool success = store.TryRegister(
            request,
            out var device,
            out var error
        );

        if (!success)
        {
            return Results.BadRequest(new
            {
                message = error
            });
        }

        return Results.Created(
            $"/api/devices/{Uri.EscapeDataString(device!.DeviceId)}",
            device
        );
    });

// ==========================================
// FLOAT TELEMETRY INGESTION
// ==========================================

app.MapPost("/api/telemetry/float",
    (TelemetryPacket<float> packet, SmartXStore store) =>
    {
        if (!float.IsFinite(packet.Value))
        {
            return Results.BadRequest(new
            {
                message = "Float value must be finite."
            });
        }

        string metric = packet.Metric ?? "";

        bool isMoisture = metric.Contains(
            "moisture",
            StringComparison.OrdinalIgnoreCase
        );

        bool isAlert;

        if (isMoisture)
        {
            isAlert = packet.Value < 20;
        }
        else
        {
            isAlert = packet.Value > 100;
        }

        bool success = store.TryRecord(
            packet,
            "float",
            packet.Value.ToString(
                "0.0",
                CultureInfo.InvariantCulture
            ),
            packet.Value,
            isAlert,
            out var error
        );

        if (!success)
        {
            return Results.BadRequest(new
            {
                message = error
            });
        }

        return Results.Ok(new
        {
            message = "Float telemetry accepted."
        });
    });

// ==========================================
// INTEGER TELEMETRY INGESTION
// ==========================================

app.MapPost("/api/telemetry/int",
    (TelemetryPacket<int> packet, SmartXStore store) =>
    {
        string metric = packet.Metric ?? "";

        bool isPower = metric.Contains(
            "power",
            StringComparison.OrdinalIgnoreCase
        );

        bool isAlert = isPower && packet.Value > 800;

        bool success = store.TryRecord(
            packet,
            "integer",
            packet.Value.ToString(
                CultureInfo.InvariantCulture
            ),
            packet.Value,
            isAlert,
            out var error
        );

        if (!success)
        {
            return Results.BadRequest(new
            {
                message = error
            });
        }

        return Results.Ok(new
        {
            message = "Integer telemetry accepted."
        });
    });

// ==========================================
// BOOLEAN TELEMETRY INGESTION
// ==========================================

app.MapPost("/api/telemetry/bool",
    (TelemetryPacket<bool> packet, SmartXStore store) =>
    {
        string displayValue = packet.Value ? "ON" : "OFF";

        bool success = store.TryRecord(
            packet,
            "boolean",
            displayValue,
            null,
            false,
            out var error
        );

        if (!success)
        {
            return Results.BadRequest(new
            {
                message = error
            });
        }

        return Results.Ok(new
        {
            message = "Boolean telemetry accepted."
        });
    });

// ==========================================
// MOCK SENSOR DATA SIMULATION
// ==========================================

app.MapPost("/api/simulate", (SmartXStore store) =>
{
    int generated = store.SimulateRound();

    return Results.Ok(new
    {
        message = "Sensor simulation completed.",
        generated
    });
});

// ==========================================
// DEVICE FILE AND IMAGE UPLOADS
// ==========================================

app.MapPost("/api/uploads", async (
    HttpRequest request,
    SmartXStore store,
    IWebHostEnvironment environment) =>
{
    if (!request.HasFormContentType)
    {
        return Results.BadRequest(new
        {
            message = "Multipart form data is required."
        });
    }

    var form = await request.ReadFormAsync();

    string deviceId = form["deviceId"].ToString();

    var file = form.Files.GetFile("file");

    if (!store.DeviceExists(deviceId))
    {
        return Results.BadRequest(new
        {
            message = "Device is not registered."
        });
    }

    if (file is null)
    {
        return Results.BadRequest(new
        {
            message = "No file was selected."
        });
    }

    const long maximumFileSize = 5 * 1024 * 1024;

    if (file.Length <= 0 || file.Length > maximumFileSize)
    {
        return Results.BadRequest(new
        {
            message = "File must be between 1 byte and 5 MB."
        });
    }

    string extension = Path.GetExtension(file.FileName)
        .ToLowerInvariant();

    string[] allowedExtensions =
    {
        ".png",
        ".jpg",
        ".jpeg",
        ".pdf",
        ".txt",
        ".log"
    };

    if (!allowedExtensions.Contains(extension))
    {
        return Results.BadRequest(new
        {
            message = "Unsupported file type."
        });
    }

    string directory = Path.Combine(
        environment.ContentRootPath,
        "Uploads"
    );

    Directory.CreateDirectory(directory);

    string storedName = $"{Guid.NewGuid():N}{extension}";

    string fullPath = Path.Combine(
        directory,
        storedName
    );

    await using (var stream = File.Create(fullPath))
    {
        await file.CopyToAsync(stream);
    }

    var upload = new UploadInfo(
        deviceId.Trim().ToUpperInvariant(),
        Path.GetFileName(file.FileName),
        storedName,
        file.Length,
        DateTimeOffset.UtcNow
    );

    store.RecordUpload(upload);

    return Results.Ok(upload);

}).DisableAntiforgery();

// ==========================================
// PART 2 - REAL-TIME PROCESSING ENDPOINTS
// ==========================================

// Includes:
// Real-time telemetry processing
// FIFO and priority queues
// Manual device commands
// Undo stack
// Historical telemetry
// Predictive suggestions

app.MapRealTimeEndpoints();

// ==========================================
// FINAL POE - MESH NETWORK ENDPOINTS
// ==========================================

// Includes:
// Network topology
// Dijkstra shortest path
// Prim minimum spanning tree
// Node failure simulation
// Self-healing route recalculation
// Network event history

app.MapMeshEndpoints();

// ==========================================
// START SMART-X API SERVER
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

    private int totalPackets = 0;
    private int totalAlerts = 0;

    // ======================================
    // INITIALISE SAMPLE ESP32 DEVICES
    // ======================================

    public SmartXStore()
    {
        TryRegister(
            new RegisterDeviceRequest(
                "ESP32-FARM-A",
                "Hydroponic Farm / Zone A",
                "Environmental"
            ),
            out _,
            out _
        );

        TryRegister(
            new RegisterDeviceRequest(
                "ESP32-FARM-B",
                "Hydroponic Farm / Zone B",
                "Environmental"
            ),
            out _,
            out _
        );

        TryRegister(
            new RegisterDeviceRequest(
                "ESP32-GRID-1",
                "Smart Grid / Main Panel",
                "Power"
            ),
            out _,
            out _
        );

        TryRegister(
            new RegisterDeviceRequest(
                "ESP32-VALVE-1",
                "Farm / Valve Rack",
                "Actuator"
            ),
            out _,
            out _
        );

        // Seed initial sample readings.
        for (int i = 0; i < 5; i++)
        {
            SimulateRound();
        }
    }

    // ======================================
    // REGISTER IOT DEVICE
    // ======================================

    public bool TryRegister(
        RegisterDeviceRequest request,
        out IoTDevice? device,
        out string error)
    {
        device = null;
        error = "";

        if (string.IsNullOrWhiteSpace(request.DeviceId))
        {
            error = "Device ID is required.";
            return false;
        }

        if (request.DeviceId.Trim().Length > 48)
        {
            error = "Device ID is too long.";
            return false;
        }

        if (string.IsNullOrWhiteSpace(request.Location))
        {
            error = "Deployment location is required.";
            return false;
        }

        if (request.Location.Trim().Length > 100)
        {
            error = "Deployment location is too long.";
            return false;
        }

        if (request.Category is not
            ("Environmental" or "Power" or "Actuator"))
        {
            error = "Invalid sensor category.";
            return false;
        }

        string deviceId = request.DeviceId
            .Trim()
            .ToUpperInvariant();

        lock (gate)
        {
            if (devices.ContainsKey(deviceId))
            {
                error = "Device ID already registered.";
                return false;
            }

            device = new IoTDevice(
                deviceId,
                request.Location.Trim(),
                request.Category,
                DateTimeOffset.UtcNow
            );

            devices.Add(deviceId, device);
        }

        return true;
    }

    // ======================================
    // CHECK REGISTERED DEVICE
    // ======================================

    public bool DeviceExists(string id)
    {
        if (string.IsNullOrWhiteSpace(id))
        {
            return false;
        }

        lock (gate)
        {
            return devices.ContainsKey(id.Trim());
        }
    }

    // ======================================
    // GENERIC TELEMETRY PROCESSING
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

        if (string.IsNullOrWhiteSpace(packet.DeviceId))
        {
            error = "Device identifier is required.";
            return false;
        }

        if (string.IsNullOrWhiteSpace(packet.Metric))
        {
            error = "Telemetry metric is required.";
            return false;
        }

        if (packet.Metric.Length > 70)
        {
            error = "Metric name is too long.";
            return false;
        }

        string deviceId = packet.DeviceId
            .Trim()
            .ToUpperInvariant();

        lock (gate)
        {
            if (!devices.ContainsKey(deviceId))
            {
                error = "Device is not registered.";
                return false;
            }

            var reading = new TelemetryReading(
                deviceId,
                packet.Metric.Trim(),
                dataType,
                displayValue,
                numericValue,
                DateTimeOffset.UtcNow,
                isAlert
            );

            readings.Add(reading);

            totalPackets++;

            if (isAlert)
            {
                totalAlerts++;
            }

            // Limit in-memory telemetry history.
            if (readings.Count > 1000)
            {
                readings.RemoveRange(
                    0,
                    readings.Count - 1000
                );
            }
        }

        return true;
    }

    // ======================================
    // GENERATE MOCK ESP32 TELEMETRY
    // ======================================

    public int SimulateRound()
    {
        List<IoTDevice> registeredDevices;

        lock (gate)
        {
            registeredDevices = devices.Values.ToList();
        }

        int generated = 0;

        foreach (var device in registeredDevices)
        {
            bool accepted = false;

            switch (device.Category)
            {
                // Environmental moisture sensors
                case "Environmental":
                    {
                        float moisture = (float)(
                            10 + Random.Shared.NextDouble() * 68
                        );

                        var packet = new TelemetryPacket<float>(
                            device.DeviceId,
                            "Soil Moisture (%)",
                            moisture
                        );

                        accepted = TryRecord(
                            packet,
                            "float",
                            moisture.ToString(
                                "0.0",
                                CultureInfo.InvariantCulture
                            ),
                            moisture,
                            moisture < 20,
                            out _
                        );

                        break;
                    }

                // Smart-grid power meters
                case "Power":
                    {
                        int watts = Random.Shared.Next(
                            250,
                            1000
                        );

                        var packet = new TelemetryPacket<int>(
                            device.DeviceId,
                            "Power Usage (W)",
                            watts
                        );

                        accepted = TryRecord(
                            packet,
                            "integer",
                            watts.ToString(
                                CultureInfo.InvariantCulture
                            ),
                            watts,
                            watts > 800,
                            out _
                        );

                        break;
                    }

                // Smart valves and actuators
                case "Actuator":
                    {
                        bool valveOpen =
                            Random.Shared.Next(2) == 1;

                        var packet = new TelemetryPacket<bool>(
                            device.DeviceId,
                            "Valve Open",
                            valveOpen
                        );

                        accepted = TryRecord(
                            packet,
                            "boolean",
                            valveOpen ? "ON" : "OFF",
                            null,
                            false,
                            out _
                        );

                        break;
                    }
            }

            if (accepted)
            {
                generated++;
            }
        }

        return generated;
    }

    // ======================================
    // SAVE DEVICE ATTACHMENT INFORMATION
    // ======================================

    public void RecordUpload(UploadInfo upload)
    {
        lock (gate)
        {
            uploads.Add(upload);
        }
    }

    // ======================================
    // GET DASHBOARD DATA SNAPSHOT
    // ======================================

    public DashboardSnapshot Snapshot()
    {
        lock (gate)
        {
            List<IoTDevice> registeredDevices =
                devices.Values
                    .OrderBy(d => d.DeviceId)
                    .ToList();

            List<TelemetryReading> recentReadings =
                readings
                    .TakeLast(60)
                    .Reverse()
                    .ToList();

            List<UploadInfo> recentUploads =
                uploads
                    .AsEnumerable()
                    .Reverse()
                    .Take(20)
                    .ToList();

            return new DashboardSnapshot(
                registeredDevices,
                recentReadings,
                recentUploads,
                totalPackets,
                totalAlerts,
                DateTimeOffset.UtcNow
            );
        }
    }
}
