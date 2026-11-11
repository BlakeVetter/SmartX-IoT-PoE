
namespace SmartX.Shared;

// A gateway or ESP32 node within the simulated mesh.
public sealed record MeshNode(
    string Id,
    string Label,
    string Location,
    string Category,
    double X,
    double Y,
    bool IsOnline,
    bool IsGateway
);

// Weighted connection between two mesh nodes.
public sealed record MeshLink(
    string Source,
    string Target,
    double Cost,
    bool IsActive,
    bool IsMstEdge
);

// Result of Dijkstra's shortest-path algorithm.
public sealed record MeshRoute(
    string Source,
    string Destination,
    List<string> Path,
    double TotalCost,
    bool IsReachable,
    int Hops
);

// History of network failures and recovery events.
public sealed record MeshEvent(
    DateTimeOffset TimestampUtc,
    string Type,
    string Message
);

// Complete response for the topology dashboard.
public sealed record MeshSnapshot(
    List<MeshNode> Nodes,
    List<MeshLink> Links,
    List<MeshEvent> Events,
    int OnlineCount,
    int ConnectedCount,
    double MstTotalCost,
    DateTimeOffset UpdatedAtUtc
);

// Request to simulate a node going online or offline.
public sealed record MeshStatusRequest(
    bool IsOnline
);
