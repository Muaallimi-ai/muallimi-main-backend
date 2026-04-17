using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Muallimi.Api.StudentExperience.HomeDashboard;
using Muallimi.Api.StudentExperience.StudentSession;
using Muallimi.Infrastructure.Persistence;

namespace Muallimi.Api.StudentExperience.TutorExposure;

/// <summary>
/// T075 (US4) — GET /student/tutor/voice/playback/{reference}.
///
/// Streams the synthesised AI tutor audio for a previously answered voice
/// turn as a chunked binary response. Authorisation is scoped to the
/// session that owns the playback reference: the request MUST come from
/// the same tenant + session that produced the original tutor turn (looked
/// up via <see cref="VoicePlaybackReference"/> on <c>TutorChatMessage</c>).
///
/// Cross-tenant or cross-session lookups return 404 to avoid leaking
/// existence. The blob is sourced from the <see cref="IVoiceBlobStore"/>;
/// production swaps the in-memory store for the MinIO/S3 adapter.
/// </summary>
public static class VoicePlaybackStreamEndpoint
{
    public const string Route = "/api/student/tutor/voice/playback/{reference}";

    public static IEndpointRouteBuilder MapVoicePlaybackStream(this IEndpointRouteBuilder routes)
    {
        routes.MapGet(Route, HandleAsync)
            .WithName("StudentTutorVoicePlayback")
            .WithTags("StudentExperience");
        return routes;
    }

    public static async Task<IResult> HandleAsync(
        string reference,
        HttpContext http,
        IStudentSessionRepository sessions,
        IVoiceCaptureRepository voiceCaptures,
        MuallimiDbContext db,
        CancellationToken ct)
    {
        if (!SessionStartEndpoint.TryGetTenantId(http, out var tenantId))
            return Results.Unauthorized();

        var sessionHeader = http.Request.Headers["X-Session-Id"].ToString();
        if (!Guid.TryParse(sessionHeader, out var sessionId))
            return Results.Unauthorized();

        var session = await sessions.FindAsync(sessionId, ct);
        if (session is null || session.TenantId != tenantId)
            return Results.NotFound();

        var blobReference = Uri.UnescapeDataString(reference ?? string.Empty);
        if (string.IsNullOrWhiteSpace(blobReference))
            return Results.NotFound();

        // Authorisation: the playback reference MUST belong to a tutor turn in
        // the caller's tenant + session. EF query filters scope tenant
        // automatically; the explicit session check guards against stale ids.
        var owningTurn = await db.TutorChatMessages
            .AsNoTracking()
            .FirstOrDefaultAsync(
                m => m.VoicePlaybackReference == blobReference && m.StudentSessionId == session.Id,
                ct);
        if (owningTurn is null) return Results.NotFound();

        var blob = await voiceCaptures.GetCapturedBlobAsync(blobReference, ct);
        if (blob is null) return Results.NotFound();

        http.Response.Headers["X-Correlation-Id"] = session.CorrelationId.ToString();
        http.Response.Headers["X-Voice-Profile-Source"] = Phase2AiTutorVoiceProfiles.Source;
        http.Response.Headers["Cache-Control"] = "private, no-store";
        // Chunked transfer is implicit when no Content-Length is set on a
        // streaming response.
        return Results.File(blob.Content, contentType: blob.ContentType, enableRangeProcessing: true);
    }
}
