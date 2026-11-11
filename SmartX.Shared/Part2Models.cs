
namespace SmartX.Shared;

// Request sent when a user issues a manual device command.
public sealed record DeviceCommandRequest(
    string DeviceId,
    string Action
);

// Information displayed in the live device registry.
public sealed record DeviceLiveStatus(
    string DeviceId,
    string Location,
    string Category,
    bool IsOnline,
    bool PumpOn,
    bool RelayOn,
    bool ValveOpen,
    int ActiveAlerts
);

// Historical manual command, including undo information.
public sealed record DeviceCommandEntry(
    Guid Id,
    string DeviceId,
    string Action,
    string PreviousState,
    string NewState,
    DateTimeOffset TimestampUtc,
    bool Undone
);

// Priority alert generated from abnormal telemetry.
public sealed record StreamAlert(
    Guid Id,
    string DeviceId,
    string Metric,
    string Severity,
    string Message,
    DateTimeOffset TimestampUtc,
    bool IsActive
);

// Rule-based recommendation from readings or command patterns.
public sealed record ActionSuggestion(
    string DeviceId,
    string Action,
    string Reason
);

// Complete data contract for the Part 2 dashboard.
public sealed record CommandStreamSnapshot(
    List<DeviceLiveStatus> Devices,
    List<TelemetryReading> Timeline,
    List<DeviceCommandEntry> Commands,
    List<StreamAlert> Alerts,
    List<ActionSuggestion> Suggestions,
    int ProcessedPackets,
    int QueuedPackets,
    int ActiveAlertCount,
    bool CanUndo,
    DateTimeOffset ServerTimeUtc
);
