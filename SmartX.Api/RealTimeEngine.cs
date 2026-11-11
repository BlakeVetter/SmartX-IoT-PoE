
using SmartX.Shared;

// Part 2: in-memory real-time engine for the assessment simulator.
internal sealed class RealTimeEngine
{
    private sealed class DeviceState(IoTDevice device)
    {
        public IoTDevice Device { get; } = device;
        public DateTimeOffset LastSeenUtc { get; set; } = device.RegisteredAtUtc;
        public bool PumpOn { get; set; }
        public bool RelayOn { get; set; }
        public bool ValveOpen { get; set; }
    }

    private readonly SmartXStore store;
    private readonly object gate = new();

    // Required Part 2 data structures.
    private readonly Queue<TelemetryReading> incoming = new();
    private readonly PriorityQueue<StreamAlert, int> urgentAlerts = new();
    private readonly Stack<Guid> undoStack = new();
    private readonly Dictionary<string, DeviceState> devices =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly SortedDictionary<long, TelemetryReading> timeline = new();
    private readonly HashSet<string> activeErrors =
        new(StringComparer.OrdinalIgnoreCase);

    // Avoid processing the same Part 1 packet twice while polling.
    private readonly HashSet<string> seen = new();
    private readonly Queue<string> seenOrder = new();
    private readonly List<DeviceCommandEntry> commands = new();
    private readonly List<StreamAlert> alertHistory = new();
    private int processedPackets;

    public RealTimeEngine(SmartXStore store) => this.store = store;

    public CommandStreamSnapshot GetSnapshot()
    {
        lock (gate)
        {
            Synchronize();
            var now = DateTimeOffset.UtcNow;

            var liveDevices = devices.Values
                .OrderBy(d => d.Device.DeviceId)
                .Select(d => new DeviceLiveStatus(
                    d.Device.DeviceId, d.Device.Location, d.Device.Category,
                    now - d.LastSeenUtc <= TimeSpan.FromSeconds(90),
                    d.PumpOn, d.RelayOn, d.ValveOpen,
                    activeErrors.Count(k => k.StartsWith(
                        d.Device.DeviceId + "|", StringComparison.OrdinalIgnoreCase))))
                .ToList();

            return new CommandStreamSnapshot(
                liveDevices,
                timeline.Values.Reverse().Take(35).ToList(),
                commands.Take(25).ToList(),
                alertHistory.Take(25)
                    .Select(a => a with
                    {
                        IsActive = activeErrors.Contains(AlertKey(a.DeviceId, a.Metric))
                    }).ToList(),
                BuildSuggestions(),
                processedPackets,
                incoming.Count,
                activeErrors.Count,
                undoStack.Count > 0,
                now);
        }
    }

    public bool TryExecute(DeviceCommandRequest request, out string error)
    {
        error = "";
        lock (gate)
        {
            Synchronize();
            var deviceId = request.DeviceId?.Trim() ?? "";
            var action = request.Action?.Trim().ToUpperInvariant() ?? "";

            if (!devices.TryGetValue(deviceId, out var state))
            {
                error = "Select a registered device.";
                return false;
            }

            bool valid = state.Device.Category switch
            {
                "Environmental" => action is "PUMP_ON" or "PUMP_OFF",
                "Power" => action is "RELAY_ON" or "RELAY_OFF",
                "Actuator" => action is "VALVE_OPEN" or "VALVE_CLOSE",
                _ => false
            };

            if (!valid)
            {
                error = "Command is not valid for this device category.";
                return false;
            }

            bool previous = CurrentValue(state, action);
            bool next = action is "PUMP_ON" or "RELAY_ON" or "VALVE_OPEN";
            SetValue(state, action, next);

            var command = new DeviceCommandEntry(
                Guid.NewGuid(), state.Device.DeviceId, action,
                previous ? "ON" : "OFF", next ? "ON" : "OFF",
                DateTimeOffset.UtcNow, false);

            commands.Insert(0, command);
            undoStack.Push(command.Id);
            if (commands.Count > 100) commands.RemoveAt(commands.Count - 1);
            return true;
        }
    }

    public bool TryUndo(out string error)
    {
        error = "";
        lock (gate)
        {
            while (undoStack.Count > 0)
            {
                Guid id = undoStack.Pop();
                int index = commands.FindIndex(c => c.Id == id && !c.Undone);
                if (index < 0) continue;

                var command = commands[index];
                if (!devices.TryGetValue(command.DeviceId, out var state))
                    continue;

                SetValue(state, command.Action, command.PreviousState == "ON");
                commands[index] = command with { Undone = true };
                return true;
            }

            error = "There is no manual command left to undo.";
            return false;
        }
    }

    private void Synchronize()
    {
        // Part 1 already owns the device registry and telemetry data.
        var source = store.Snapshot();

        foreach (var device in source.Devices)
            if (!devices.ContainsKey(device.DeviceId))
                devices.Add(device.DeviceId, new DeviceState(device));

        foreach (var reading in source.RecentReadings.AsEnumerable().Reverse())
        {
            var key = $"{reading.DeviceId}|{reading.Metric}|{reading.TimestampUtc.UtcDateTime.Ticks}";
            if (!seen.Add(key)) continue;
            seenOrder.Enqueue(key);
            if (seenOrder.Count > 3000) seen.Remove(seenOrder.Dequeue());

            // FIFO path: every reading is queued in arrival order.
            incoming.Enqueue(reading);

            // Priority path: urgent events bypass FIFO processing.
            var alertKey = AlertKey(reading.DeviceId, reading.Metric);
            if (reading.IsAlert)
            {
                if (activeErrors.Add(alertKey))
                    urgentAlerts.Enqueue(new StreamAlert(
                        Guid.NewGuid(), reading.DeviceId, reading.Metric,
                        "CRITICAL", $"Abnormal {reading.Metric}: {reading.DisplayValue}",
                        reading.TimestampUtc, true), 0);
            }
            else
                activeErrors.Remove(alertKey);
        }

        // Process a bounded batch per refresh to demonstrate queue backlogs.
        int budget = 15;
        while (budget-- > 0 && incoming.Count > 0)
        {
            var reading = incoming.Dequeue();
            if (devices.TryGetValue(reading.DeviceId, out var state) &&
                reading.TimestampUtc > state.LastSeenUtc)
                state.LastSeenUtc = reading.TimestampUtc;

            long tick = reading.TimestampUtc.UtcDateTime.Ticks;
            while (timeline.ContainsKey(tick)) tick++;
            timeline[tick] = reading;
            if (timeline.Count > 500) timeline.Remove(timeline.First().Key);
            processedPackets++;
        }

        foreach (var state in devices.Values)
        {
            string offlineKey = AlertKey(state.Device.DeviceId, "Disconnected");
            if (DateTimeOffset.UtcNow - state.LastSeenUtc > TimeSpan.FromSeconds(90))
            {
                if (activeErrors.Add(offlineKey))
                    urgentAlerts.Enqueue(new StreamAlert(
                        Guid.NewGuid(), state.Device.DeviceId, "Disconnected",
                        "WARNING", "No telemetry received in 90 seconds.",
                        DateTimeOffset.UtcNow, true), 1);
            }
            else
                activeErrors.Remove(offlineKey);
        }

        // Lower priority number means higher urgency.
        while (urgentAlerts.TryDequeue(out var alert, out _))
        {
            alertHistory.Insert(0, alert);
            if (alertHistory.Count > 100) alertHistory.RemoveAt(alertHistory.Count - 1);
        }
    }

    private List<ActionSuggestion> BuildSuggestions()
    {
        var suggestions = new List<ActionSuggestion>();
        foreach (var state in devices.Values)
        {
            var id = state.Device.DeviceId;
            if (state.Device.Category == "Environmental" && !state.PumpOn &&
                activeErrors.Any(k => k.StartsWith(id + "|", StringComparison.OrdinalIgnoreCase)
                    && k.Contains("moisture", StringComparison.OrdinalIgnoreCase)))
                suggestions.Add(new(id, "PUMP_ON",
                    "Low moisture detected. Consider starting the water pump."));

            if (state.Device.Category == "Power" && state.RelayOn &&
                activeErrors.Any(k => k.StartsWith(id + "|", StringComparison.OrdinalIgnoreCase)
                    && k.Contains("power", StringComparison.OrdinalIgnoreCase)))
                suggestions.Add(new(id, "RELAY_OFF",
                    "High power consumption detected. Consider isolating the relay."));
        }

        // Historical command patterns: repeated commands receive a suggestion.
        foreach (var group in commands.Where(c => !c.Undone)
            .GroupBy(c => (c.DeviceId, c.Action)).Where(g => g.Count() >= 2))
        {
            if (!suggestions.Any(s => s.DeviceId == group.Key.DeviceId &&
                                      s.Action == group.Key.Action))
                suggestions.Add(new(group.Key.DeviceId, group.Key.Action,
                    "Frequently used manual action. Available as a quick command."));
        }

        return suggestions.Take(6).ToList();
    }

    private static string AlertKey(string id, string metric) => $"{id}|{metric}";

    private static bool CurrentValue(DeviceState device, string action) => action switch
    {
        "PUMP_ON" or "PUMP_OFF" => device.PumpOn,
        "RELAY_ON" or "RELAY_OFF" => device.RelayOn,
        _ => device.ValveOpen
    };

    private static void SetValue(DeviceState device, string action, bool value)
    {
        if (action.StartsWith("PUMP_", StringComparison.Ordinal))
            device.PumpOn = value;
        else if (action.StartsWith("RELAY_", StringComparison.Ordinal))
            device.RelayOn = value;
        else
            device.ValveOpen = value;
    }
}
