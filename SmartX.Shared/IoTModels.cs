
namespace SmartX.Shared;

public sealed record RegisterDeviceRequest(
    string DeviceId,
    string Location,
    string Category
);

public sealed record IoTDevice(
    string DeviceId,
    string Location,
    string Category,
    DateTimeOffset RegisteredAtUtc
);

// Generic class for strongly typed sensor values.
public sealed record TelemetryPacket<T>(
    string DeviceId,
    string Metric,
    T Value
);

public sealed record TelemetryReading(
    string DeviceId,
    string Metric,
    string DataType,
    string DisplayValue,
    double? NumericValue,
    DateTimeOffset TimestampUtc,
    bool IsAlert
);

public sealed record UploadInfo(
    string DeviceId,
    string OriginalFileName,
    string StoredFileName,
    long SizeBytes,
    DateTimeOffset UploadedAtUtc
);

public sealed record DashboardSnapshot(
    List<IoTDevice> Devices,
    List<TelemetryReading> RecentReadings,
    List<UploadInfo> Uploads,
    int TotalPackets,
    int TotalAlerts,
    DateTimeOffset ServerTimeUtc
);
