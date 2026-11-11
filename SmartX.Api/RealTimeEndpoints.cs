
using SmartX.Shared;

internal static class RealTimeEndpoints
{
    public static IEndpointRouteBuilder MapRealTimeEndpoints(
        this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/stream", (RealTimeEngine engine) =>
            Results.Ok(engine.GetSnapshot()));

        app.MapPost("/api/stream/commands",
            (DeviceCommandRequest request, RealTimeEngine engine) =>
            {
                if (!engine.TryExecute(request, out var error))
                    return Results.BadRequest(new { message = error });

                return Results.Ok(new { message = "Command issued successfully." });
            });

        app.MapPost("/api/stream/undo", (RealTimeEngine engine) =>
        {
            if (!engine.TryUndo(out var error))
                return Results.BadRequest(new { message = error });

            return Results.Ok(new { message = "Last command was undone." });
        });

        return app;
    }
}
