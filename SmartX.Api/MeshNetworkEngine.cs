
using SmartX.Shared;

internal sealed class MeshNetworkEngine
{
    private const string GatewayId = "SMARTX-GATEWAY";

    private sealed record Edge(
        string Source,
        string Target,
        double Cost
    );

    private readonly SmartXStore store;
    private readonly object gate = new();

    // Graph nodes and weighted edges.
    private readonly Dictionary<string, MeshNode> nodes =
        new(StringComparer.OrdinalIgnoreCase);

    private readonly List<Edge> edges = new();
    private readonly List<MeshEvent> events = new();

    public MeshNetworkEngine(SmartXStore store)
    {
        this.store = store;

        // Central IoT gateway.
        nodes[GatewayId] = new MeshNode(
            GatewayId,
            "Gateway",
            "Central Processing Gateway",
            "Gateway",
            500, 80,
            true, true
        );

        SynchronizeDevices();

        // Simulated bidirectional wireless connections.
        // Multiple paths enable automatic rerouting.
        Connect(GatewayId, "ESP32-FARM-A", 4);
        Connect(GatewayId, "ESP32-FARM-B", 5);
        Connect(GatewayId, "ESP32-GRID-1", 9);
        Connect(GatewayId, "ESP32-VALVE-1", 10);

        Connect("ESP32-FARM-A", "ESP32-FARM-B", 3);
        Connect("ESP32-FARM-A", "ESP32-GRID-1", 4);
        Connect("ESP32-FARM-B", "ESP32-GRID-1", 7);
        Connect("ESP32-FARM-B", "ESP32-VALVE-1", 3);
        Connect("ESP32-GRID-1", "ESP32-VALVE-1", 6);

        AddEvent("INITIALIZED",
            "Mesh network initialised with redundant routing paths.");
    }

    // =====================================
    // NETWORK SNAPSHOT
    // =====================================

    public MeshSnapshot GetSnapshot()
    {
        lock (gate)
        {
            SynchronizeDevices();

            var graph = BuildGraph();
            var reachable = FindReachable(graph);
            var mst = CalculateMst(graph);

            var links = edges.Select(e => new MeshLink(
                e.Source,
                e.Target,
                e.Cost,
                nodes[e.Source].IsOnline &&
                    nodes[e.Target].IsOnline,
                mst.EdgeKeys.Contains(EdgeKey(
                    e.Source, e.Target))
            )).ToList();

            return new MeshSnapshot(
                nodes.Values
                    .OrderByDescending(n => n.IsGateway)
                    .ThenBy(n => n.Id)
                    .ToList(),
                links,
                events.Take(30).ToList(),
                nodes.Values.Count(n => n.IsOnline),
                reachable.Count,
                Math.Round(mst.Cost, 2),
                DateTimeOffset.UtcNow
            );
        }
    }

    // =====================================
    // DIJKSTRA SHORTEST PATH
    // =====================================

    public bool TryGetRoute(string deviceId, out MeshRoute route)
    {
        lock (gate)
        {
            SynchronizeDevices();

            string id = deviceId?.Trim() ?? "";

            if (!nodes.ContainsKey(id))
            {
                route = Unreachable(id);
                return false;
            }

            route = CalculateRoute(id);
            return true;
        }
    }

    private MeshRoute CalculateRoute(string source)
    {
        var graph = BuildGraph();

        if (!graph.ContainsKey(source) ||
            !graph.ContainsKey(GatewayId))
        {
            return Unreachable(source);
        }

        var distances = new Dictionary<string, double>(
            StringComparer.OrdinalIgnoreCase);

        var previous = new Dictionary<string, string>(
            StringComparer.OrdinalIgnoreCase);

        var frontier = new PriorityQueue<string, double>();

        distances[source] = 0;
        frontier.Enqueue(source, 0);

        while (frontier.TryDequeue(
            out var current, out var currentCost))
        {
            if (currentCost > distances[current])
                continue;

            if (current.Equals(GatewayId,
                StringComparison.OrdinalIgnoreCase))
                break;

            foreach (var neighbor in graph[current])
            {
                double newCost = currentCost + neighbor.Cost;

                if (!distances.TryGetValue(
                    neighbor.To, out var existing) ||
                    newCost < existing)
                {
                    distances[neighbor.To] = newCost;
                    previous[neighbor.To] = current;

                    frontier.Enqueue(neighbor.To, newCost);
                }
            }
        }

        if (!distances.TryGetValue(GatewayId, out var total))
            return Unreachable(source);

        // Reconstruct the optimal path.
        var path = new List<string> { GatewayId };

        while (!path[^1].Equals(source,
            StringComparison.OrdinalIgnoreCase))
        {
            if (!previous.TryGetValue(
                path[^1], out var parent))
                return Unreachable(source);

            path.Add(parent);
        }

        path.Reverse();

        return new MeshRoute(
            source,
            GatewayId,
            path,
            Math.Round(total, 2),
            true,
            path.Count - 1
        );
    }

    private static MeshRoute Unreachable(string source) =>
        new(source, GatewayId, new(), 0, false, 0);

    // =====================================
    // PRIM MINIMUM SPANNING TREE
    // =====================================

    private static (
        HashSet<string> EdgeKeys,
        double Cost
    ) CalculateMst(
        Dictionary<string, List<(string To, double Cost)>> graph)
    {
        var chosen = new HashSet<string>(
            StringComparer.OrdinalIgnoreCase);

        var visited = new HashSet<string>(
            StringComparer.OrdinalIgnoreCase);

        var frontier =
            new PriorityQueue<(string From, string To, double Cost), double>();

        double total = 0;

        if (!graph.ContainsKey(GatewayId))
            return (chosen, total);

        visited.Add(GatewayId);
        EnqueueNeighbors(GatewayId);

        while (frontier.TryDequeue(out var edge, out _))
        {
            if (!visited.Add(edge.To))
                continue;

            chosen.Add(EdgeKey(edge.From, edge.To));
            total += edge.Cost;

            EnqueueNeighbors(edge.To);
        }

        return (chosen, total);

        void EnqueueNeighbors(string from)
        {
            foreach (var neighbor in graph[from])
            {
                if (!visited.Contains(neighbor.To))
                {
                    frontier.Enqueue(
                        (from, neighbor.To, neighbor.Cost),
                        neighbor.Cost
                    );
                }
            }
        }
    }

    // =====================================
    // BFS: GATEWAY REACHABILITY
    // =====================================

    private static HashSet<string> FindReachable(
        Dictionary<string, List<(string To, double Cost)>> graph)
    {
        var visited = new HashSet<string>(
            StringComparer.OrdinalIgnoreCase);

        if (!graph.ContainsKey(GatewayId))
            return visited;

        var queue = new Queue<string>();

        queue.Enqueue(GatewayId);
        visited.Add(GatewayId);

        while (queue.Count > 0)
        {
            var current = queue.Dequeue();

            foreach (var neighbor in graph[current])
            {
                if (visited.Add(neighbor.To))
                    queue.Enqueue(neighbor.To);
            }
        }

        return visited;
    }

    // =====================================
    // GENERATE ACTIVE ADJACENCY LIST
    // =====================================

    private Dictionary<string,
        List<(string To, double Cost)>> BuildGraph()
    {
        var graph = new Dictionary<string,
            List<(string To, double Cost)>>(
                StringComparer.OrdinalIgnoreCase);

        foreach (var node in nodes.Values.Where(n => n.IsOnline))
        {
            graph[node.Id] = new();
        }

        foreach (var edge in edges)
        {
            if (!graph.ContainsKey(edge.Source) ||
                !graph.ContainsKey(edge.Target))
                continue;

            // Undirected mesh connection.
            graph[edge.Source].Add(
                (edge.Target, edge.Cost));

            graph[edge.Target].Add(
                (edge.Source, edge.Cost));
        }

        return graph;
    }

    // =====================================
    // NODE FAILURE AND SELF-HEALING
    // =====================================

    public bool TrySetOnline(
        string deviceId,
        bool online,
        out string message)
    {
        lock (gate)
        {
            SynchronizeDevices();

            message = "";
            string id = deviceId?.Trim() ?? "";

            if (!nodes.TryGetValue(id, out var node))
            {
                message = "Node does not exist.";
                return false;
            }

            if (node.IsGateway)
            {
                message = "The central gateway cannot be disabled.";
                return false;
            }

            if (node.IsOnline == online)
            {
                message = "Node is already in the requested state.";
                return true;
            }

            nodes[id] = node with { IsOnline = online };

            AddEvent(
                online ? "RESTORED" : "FAILURE",
                online
                    ? $"{id} has rejoined the mesh."
                    : $"{id} has gone offline."
            );

            int connected = FindReachable(BuildGraph()).Count;

            AddEvent(
                "REROUTED",
                $"Routing recalculated. {connected} nodes " +
                "can currently reach the gateway."
            );

            message = "Network topology updated successfully.";
            return true;
        }
    }

    public void Reset()
    {
        lock (gate)
        {
            SynchronizeDevices();

            foreach (var id in nodes.Keys.ToList())
            {
                nodes[id] = nodes[id] with
                {
                    IsOnline = true
                };
            }

            events.Clear();

            AddEvent("RESET",
                "All mesh nodes restored and routes recalculated.");
        }
    }

    // =====================================
    // SYNCHRONISE PART 1 SENSOR REGISTRY
    // =====================================

    private void SynchronizeDevices()
    {
        var registered = store.Snapshot().Devices;

        int extras = nodes.Values.Count(n =>
            !n.IsGateway && !IsSeedNode(n.Id));

        foreach (var device in registered)
        {
            if (nodes.ContainsKey(device.DeviceId))
                continue;

            bool isSeed = IsSeedNode(device.DeviceId);

            var position = device.DeviceId.ToUpperInvariant() switch
            {
                "ESP32-FARM-A" => (X: 235.0, Y: 195.0),
                "ESP32-FARM-B" => (X: 765.0, Y: 195.0),
                "ESP32-GRID-1" => (X: 235.0, Y: 430.0),
                "ESP32-VALVE-1" => (X: 765.0, Y: 430.0),
                _ => ExtraPosition(extras++)
            };

            nodes[device.DeviceId] = new MeshNode(
                device.DeviceId,
                device.DeviceId.Replace("ESP32-", ""),
                device.Location,
                device.Category,
                position.X,
                position.Y,
                true,
                false
            );

            if (!isSeed)
            {
                // Newly registered sensors join the simulated mesh.
                var newNode = nodes[device.DeviceId];
                var gateway = nodes[GatewayId];

                Connect(
                    newNode.Id,
                    GatewayId,
                    Math.Round(
                        3 + Distance(newNode, gateway) / 120, 1)
                );

                var neighbor = nodes.Values
                    .Where(n => n.Id != newNode.Id &&
                                !n.IsGateway)
                    .OrderBy(n => Distance(newNode, n))
                    .FirstOrDefault();

                if (neighbor is not null)
                {
                    Connect(
                        newNode.Id,
                        neighbor.Id,
                        Math.Round(
                            2 + Distance(newNode, neighbor) / 120, 1)
                    );
                }

                AddEvent("JOINED",
                    $"{newNode.Id} joined the simulated mesh.");
            }
        }
    }

    private static bool IsSeedNode(string id) =>
        id.Equals("ESP32-FARM-A",
            StringComparison.OrdinalIgnoreCase) ||
        id.Equals("ESP32-FARM-B",
            StringComparison.OrdinalIgnoreCase) ||
        id.Equals("ESP32-GRID-1",
            StringComparison.OrdinalIgnoreCase) ||
        id.Equals("ESP32-VALVE-1",
            StringComparison.OrdinalIgnoreCase);

    private static (double X, double Y) ExtraPosition(int index)
    {
        return (index % 6) switch
        {
            0 => (110, 350),
            1 => (890, 350),
            2 => (110, 110),
            3 => (890, 110),
            4 => (500, 510),
            _ => (500, 290)
        };
    }

    private static double Distance(MeshNode a, MeshNode b)
    {
        double dx = a.X - b.X;
        double dy = a.Y - b.Y;

        return Math.Sqrt(dx * dx + dy * dy);
    }

    private void Connect(string source, string target, double cost)
    {
        if (!nodes.ContainsKey(source) ||
            !nodes.ContainsKey(target))
            return;

        if (edges.Any(e => EdgeKey(e.Source, e.Target) ==
                           EdgeKey(source, target)))
            return;

        edges.Add(new Edge(source, target, cost));
    }

    private static string EdgeKey(string a, string b) =>
        string.Compare(a, b, StringComparison.OrdinalIgnoreCase) <= 0
            ? $"{a}|{b}"
            : $"{b}|{a}";

    private void AddEvent(string type, string message)
    {
        events.Insert(0, new MeshEvent(
            DateTimeOffset.UtcNow,
            type,
            message
        ));

        if (events.Count > 50)
            events.RemoveAt(events.Count - 1);
    }
}
