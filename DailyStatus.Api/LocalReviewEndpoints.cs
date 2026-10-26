using System.Text.Json;

namespace DailyStatus.Api;

public static class LocalReviewEndpoints
{
    public static void MapLocalReviewEndpoints(this WebApplication app)
    {
        // Local development only; no public/production access.
        if (!app.Environment.IsDevelopment())
            return;

        static bool IsLocal(HttpContext context) =>
            context.Connection.RemoteIpAddress is { } ip &&
            System.Net.IPAddress.IsLoopback(ip);

        static string Root(WebApplication app) =>
            Path.GetFullPath(Path.Combine(
                app.Environment.ContentRootPath, ".."));

        app.MapGet("/api/evidence", (HttpContext context) =>
        {
            if (!IsLocal(context))
                return Results.NotFound();

            var directory = Path.Combine(
                Root(app), "data", "combined-evidence");

            if (!Directory.Exists(directory))
                return Results.NotFound(new { error = "Evidence missing" });

            var latest = Directory.GetFiles(directory, "combined-*.json")
                .OrderByDescending(Path.GetFileName)
                .FirstOrDefault();

            if (latest is null)
                return Results.NotFound(new { error = "Evidence missing" });

            return Results.File(
                latest,
                "application/json");
        });

        app.MapPost("/api/draft", async (
            HttpContext context,
            JsonElement request) =>
        {
            if (!IsLocal(context))
                return Results.NotFound();

            if (!request.TryGetProperty("text", out var textElement) ||
                textElement.ValueKind != JsonValueKind.String)
                return Results.BadRequest(new { error = "Draft text required" });

            var text = textElement.GetString()?.Trim();

            if (string.IsNullOrWhiteSpace(text) || text.Length > 100000)
                return Results.BadRequest(new { error = "Invalid draft text" });

            if (!request.TryGetProperty("includedIds", out var idsElement) ||
                idsElement.ValueKind != JsonValueKind.Array)
                return Results.BadRequest(new { error = "Included IDs required" });

            var directory = Path.Combine(
                Root(app), "data", "combined-evidence");

            if (!Directory.Exists(directory))
                return Results.NotFound(new { error = "Evidence missing" });

            var latest = Directory.GetFiles(directory, "combined-*.json")
                .OrderByDescending(Path.GetFileName)
                .FirstOrDefault();

            if (latest is null)
                return Results.NotFound(new { error = "Evidence missing" });

            using var document = JsonDocument.Parse(
                await File.ReadAllTextAsync(latest));

            var validIds = document.RootElement
                .GetProperty("Activities")
                .EnumerateArray()
                .Select(x => x.GetProperty("ActivityId").GetString())
                .Where(x => x is not null)
                .ToHashSet(StringComparer.Ordinal);

            var selected = new List<string>();

            foreach (var id in idsElement.EnumerateArray())
            {
                if (id.ValueKind != JsonValueKind.String)
                    return Results.BadRequest(new { error = "Invalid ID" });

                var value = id.GetString();

                if (value is null || !validIds.Contains(value))
                    return Results.BadRequest(new { error = "Unknown evidence ID" });

                if (!selected.Contains(value))
                    selected.Add(value);
            }

            var draftId = Guid.NewGuid().ToString();

            var draft = new
            {
                DraftId = draftId,
                CreatedAt = DateTimeOffset.UtcNow,
                Status = "PendingReview",
                Approved = false,
                Published = false,
                EvidenceFile = Path.GetFileName(latest),
                IncludedActivityIds = selected,
                Text = text,
                RequiresExplicitApproval = true
            };

            var output = Path.Combine(
                Root(app), "data", "review-drafts");

            Directory.CreateDirectory(output);

            var path = Path.Combine(output, $"draft-{draftId}.json");

            await File.WriteAllTextAsync(
                path,
                JsonSerializer.Serialize(
                    draft,
                    new JsonSerializerOptions { WriteIndented = true }));

            return Results.Ok(new
            {
                saved = true,
                draftId,
                status = "PendingReview"
            });
        });
    }
}
