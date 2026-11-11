
using SmartX.Shared;

internal static class MeshEndpoints
{
    public static IEndpointRouteBuilder MapMeshEndpoints(
        this IEndpointRouteBuilder app)
    {
        // Complete live network snapshot.
        app.MapGet("/api/mesh",
            (MeshNetworkEngine engine) =>
                Results.Ok(engine.GetSnapshot()));

        // Calculate shortest route from node to gateway.
        app.MapGet("/api/mesh/route/{deviceId}",
            (string deviceId, MeshNetworkEngine engine) =>
            {
                if (!engine.TryGetRoute(deviceId, out var route))
                    return Results.NotFound(new
                    {
                        message = "Mesh node not found."
                    });

                return Results.Ok(route);
            });

        // Simulate network failure or restoration.
        app.MapPut("/api/mesh/nodes/{deviceId}/status",
            (string deviceId,
             MeshStatusRequest request,
             MeshNetworkEngine engine) =>
            {
                if (!engine.TrySetOnline(
                    deviceId, request.IsOnline, out var message))
                {
                    return Results.BadRequest(new { message });
                }

                return Results.Ok(new { message });
            });

        // Restore every simulated network node.
        app.MapPost("/api/mesh/reset",
            (MeshNetworkEngine engine) =>
            {
                engine.Reset();

                return Results.Ok(new
                {
                    message = "Mesh network restored."
                });
            });

        return app;
    }
}
