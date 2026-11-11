
using SmartX.Shared;
using System.Globalization;
using static System.Runtime.InteropServices.JavaScript.JSType;

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
