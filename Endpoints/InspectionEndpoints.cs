using Aveva.Sds.StoreVisualizer.Models;
using Aveva.Sds.StoreVisualizer.Services;

namespace Aveva.Sds.StoreVisualizer.Endpoints;

/// <summary>Maps the read-only store inspection endpoints.</summary>
public static class InspectionEndpoints
{
    /// <summary>Maps all store inspection routes onto the application.</summary>
    public static IEndpointRouteBuilder MapStoreInspectionEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapPost("/api/sessions", async Task<IResult> (
            OpenSessionRequest request,
            StoreInspectionService inspectionService,
            CancellationToken cancellationToken) =>
        {
            if (string.IsNullOrWhiteSpace(request.RootPath))
            {
                return Results.BadRequest(new { error = "RootPath is required." });
            }

            try
            {
                return Results.Ok(await inspectionService.OpenAsync(request.RootPath, cancellationToken).ConfigureAwait(false));
            }
            catch (DirectoryNotFoundException)
            {
                return Results.NotFound(new { error = "The selected folder does not exist." });
            }
            catch (UnauthorizedAccessException)
            {
                return Results.BadRequest(new { error = "The selected folder cannot be read." });
            }
            catch (ArgumentException exception)
            {
                return Results.BadRequest(new { error = exception.Message });
            }
        })
        .WithName("OpenStoreInspectionSession")
        .WithSummary("Open a read-only inspection session for a local folder")
        .Produces<StoreSessionResponse>()
        .Produces(StatusCodes.Status400BadRequest)
        .Produces(StatusCodes.Status404NotFound);

        endpoints.MapGet("/api/sessions/{sessionId}", (
            string sessionId,
            StoreInspectionService inspectionService) =>
        {
            return inspectionService.TryGetSession(sessionId, out var session)
                ? Results.Ok(session.Response)
                : Results.NotFound(new { error = "The inspection session was not found." });
        })
        .WithName("GetStoreInspectionSession")
        .WithSummary("Get the current inspection session")
        .Produces<StoreSessionResponse>()
        .Produces(StatusCodes.Status404NotFound);

        endpoints.MapPost("/api/sessions/{sessionId}/refresh", async Task<IResult> (
            string sessionId,
            StoreInspectionService inspectionService,
            CancellationToken cancellationToken) =>
        {
            try
            {
                var session = await inspectionService.RefreshAsync(sessionId, cancellationToken).ConfigureAwait(false);
                return session is null
                    ? Results.NotFound(new { error = "The inspection session was not found." })
                    : Results.Ok(session.Response);
            }
            catch (DirectoryNotFoundException)
            {
                return Results.NotFound(new { error = "The inspected folder no longer exists." });
            }
        })
        .WithName("RefreshStoreInspectionSession")
        .WithSummary("Refresh a read-only inspection session")
        .Produces<StoreSessionResponse>()
        .Produces(StatusCodes.Status404NotFound);

        endpoints.MapGet("/api/sessions/{sessionId}/files/{fileId}", (
            string sessionId,
            string fileId,
            StoreInspectionService inspectionService) =>
        {
            return inspectionService.TryGetFile(sessionId, fileId, out var file)
                ? Results.Ok(file.Detail)
                : Results.NotFound(new { error = "The requested store file was not found." });
        })
        .WithName("GetStoreFileDetail")
        .WithSummary("Get decoded details for one store file")
        .Produces<StoreFileDetailResponse>()
        .Produces(StatusCodes.Status404NotFound);

        endpoints.MapGet("/api/sessions/{sessionId}/files/{fileId}/raw", async Task<IResult> (
            string sessionId,
            string fileId,
            long offset,
            int length,
            StoreInspectionService inspectionService,
            CancellationToken cancellationToken) =>
        {
            if (offset < 0 || length is < 1 or > 65_536)
            {
                return Results.BadRequest(new { error = "Offset must be non-negative and length must be between 1 and 65536." });
            }

            try
            {
                var raw = await inspectionService.ReadRawAsync(sessionId, fileId, offset, length, cancellationToken).ConfigureAwait(false);
                return raw is null
                    ? Results.NotFound(new { error = "The requested store file was not found." })
                    : Results.Ok(raw);
            }
            catch (ArgumentOutOfRangeException exception)
            {
                return Results.BadRequest(new { error = exception.Message });
            }
        })
        .WithName("ReadStoreFileBytes")
        .WithSummary("Read a bounded raw byte range from a store file")
        .Produces<RawRangeResponse>()
        .Produces(StatusCodes.Status400BadRequest)
        .Produces(StatusCodes.Status404NotFound);

        endpoints.MapDelete("/api/sessions/{sessionId}", (
            string sessionId,
            StoreInspectionService inspectionService) =>
        {
            return inspectionService.RemoveSession(sessionId)
                ? Results.NoContent()
                : Results.NotFound(new { error = "The inspection session was not found." });
        })
        .WithName("CloseStoreInspectionSession")
        .WithSummary("Close an inspection session")
        .Produces(StatusCodes.Status204NoContent)
        .Produces(StatusCodes.Status404NotFound);

        return endpoints;
    }
}
