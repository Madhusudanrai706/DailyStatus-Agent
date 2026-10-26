using System.Text.Json;
using System.Text.Json.Nodes;

namespace DailyStatus.Api;

public static class LocalDraftReviewEndpoints
{
    public static void MapLocalDraftReviewEndpoints(this WebApplication app)
    {
        if (!app.Environment.IsDevelopment())
            return;

        var group = app.MapGroup("/api/local-review");

        group.AddEndpointFilter(async (context, next) =>
        {
            var ip = context.HttpContext.Connection.RemoteIpAddress;

            if (ip is null || !System.Net.IPAddress.IsLoopback(ip))
                return Results.NotFound();

            return await next(context);
        });

        group.MapPost("/{id:guid}/approve", async (
            Guid id,
            HttpContext context,
            CancellationToken ct) =>
        {
            return await Review(id, "Approved", context, app, ct);
        });

        group.MapPost("/{id:guid}/reject", async (
            Guid id,
            HttpContext context,
            CancellationToken ct) =>
        {
            return await Review(id, "Rejected", context, app, ct);
        });
    }

    private static async Task<IResult> Review(
        Guid id,
        string decision,
        HttpContext context,
        WebApplication app,
        CancellationToken ct)
    {
        if (!context.Request.Headers.TryGetValue(
                "X-Review-Confirmed", out var confirmation) ||
            confirmation.ToString() != "true")
        {
            return Results.BadRequest(new {
                error = "Explicit reviewer confirmation required."
            });
        }

        var root = Path.GetFullPath(Path.Combine(
            app.Environment.ContentRootPath, ".."));

        var directory = Path.Combine(root, "data", "review-drafts");
        var file = Path.Combine(directory, $"draft-{id}.json");

        if (!File.Exists(file))
            return Results.NotFound(new { error = "Draft not found" });

        var json = JsonNode.Parse(
            await File.ReadAllTextAsync(file, ct))?.AsObject();

        if (json is null)
            return Results.Problem("Invalid draft");

        if (json["Status"]?.ToString() != "PendingReview")
            return Results.Conflict(new {
                error = "Only PendingReview drafts can be reviewed"
            });

        var evidence = json["IncludedActivityIds"]?.AsArray();

        if (evidence is null || evidence.Count == 0)
            return Results.BadRequest(new {
                error = "Draft has no linked evidence"
            });

        // Validate all linked evidence against the source snapshot.
        if (decision == "Approved")
        {
            var evidenceFile = json["EvidenceFile"]?.ToString();

            if (string.IsNullOrWhiteSpace(evidenceFile) ||
                evidenceFile != Path.GetFileName(evidenceFile) ||
                !evidenceFile.StartsWith("combined-", StringComparison.Ordinal) ||
                !evidenceFile.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
            {
                return Results.BadRequest(new {
                    error = "Valid evidence source file required."
                });
            }

            var evidencePath = Path.Combine(
                root, "data", "combined-evidence", evidenceFile);

            if (!File.Exists(evidencePath))
                return Results.BadRequest(new {
                    error = "Evidence source file missing."
                });

            using var evidenceDocument = JsonDocument.Parse(
                await File.ReadAllTextAsync(evidencePath, ct));

            var validIds = evidenceDocument.RootElement
                .GetProperty("Activities")
                .EnumerateArray()
                .Select(a => a.GetProperty("ActivityId").ToString())
                .ToHashSet(StringComparer.Ordinal);

            var selectedIds = evidence!
                .Select(x => x?.ToString() ?? "")
                .ToArray();

            if (selectedIds.Any(string.IsNullOrWhiteSpace) ||
                selectedIds.Distinct(StringComparer.Ordinal).Count()
                    != selectedIds.Length ||
                selectedIds.Any(id => !validIds.Contains(id)))
            {
                return Results.BadRequest(new {
                    error = "Invalid or duplicate evidence IDs."
                });
            }
        }
        json["Status"] = decision;
        json["Approved"] = decision == "Approved";
        json["Published"] = false;
        json["ReviewedAt"] = DateTimeOffset.UtcNow.ToString("O");
        json["ReviewNote"] = "Explicitly confirmed local development review";
        json["ReviewConfirmed"] = true;
        json["ReviewSource"] = "LocalDevelopmentAPI";

        var temp = file + "." + Guid.NewGuid() + ".tmp";

        await File.WriteAllTextAsync(
            temp,
            json.ToJsonString(new JsonSerializerOptions {
                WriteIndented = true
            }),
            ct);

        File.Move(temp, file, true);

        return Results.Ok(new {
            draftId = id,
            status = decision,
            approved = decision == "Approved",
            published = false,
            emailSent = false
        });
    }
}
