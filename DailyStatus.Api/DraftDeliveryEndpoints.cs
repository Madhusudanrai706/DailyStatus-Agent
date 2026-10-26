using System.Text.Json;

namespace DailyStatus.Api;

public static class DraftDeliveryEndpoints
{
    public static void MapDraftDeliveryEndpoints(this WebApplication app)
    {
        if (!app.Environment.IsDevelopment())
            return;

        var group = app.MapGroup("/api/draft-delivery");

        group.AddEndpointFilter(async (context, next) =>
        {
            var ip = context.HttpContext.Connection.RemoteIpAddress;

            if (ip is null || !System.Net.IPAddress.IsLoopback(ip))
                return Results.NotFound();

            return await next(context);
        });

        group.MapGet("/history", (IWebHostEnvironment env) =>
        {
            var directory = Path.Combine(
                Path.GetFullPath(Path.Combine(env.ContentRootPath, "..")),
                "data", "draft-delivery");

            if (!Directory.Exists(directory))
                return Results.Ok(Array.Empty<object>());

            var records = new List<JsonElement>();

            foreach (var file in Directory.GetFiles(directory, "*.json"))
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(file));
                records.Add(doc.RootElement.Clone());
            }

            return Results.Ok(records.OrderByDescending(x =>
                x.TryGetProperty("PreparedAt", out var date)
                    ? date.ToString()
                    : ""));
        });

        group.MapPost("/prepare/{id:guid}",
            async (Guid id, IWebHostEnvironment env, CancellationToken ct) =>
        {
            var root = Path.GetFullPath(
                Path.Combine(env.ContentRootPath, ".."));

            var draftPath = Path.Combine(
                root, "data", "review-drafts", $"draft-{id}.json");

            if (!File.Exists(draftPath))
                return Results.NotFound(new { error = "Draft not found" });

            using var doc = JsonDocument.Parse(
                await File.ReadAllTextAsync(draftPath, ct));

            var draft = doc.RootElement;

            string Read(string key) =>
                draft.TryGetProperty(key, out var value)
                    ? value.ToString()
                    : "";

            if (Read("Status") != "Approved" ||
                Read("Approved") != "True" ||
                Read("ReviewConfirmed") != "True" ||
                Read("Published") != "False")
            {
                return Results.Conflict(new {
                    error = "Confirmed approval required"
                });
            }

            if (!draft.TryGetProperty("IncludedActivityIds", out var ids) ||
                ids.ValueKind != JsonValueKind.Array ||
                ids.GetArrayLength() == 0)
            {
                return Results.BadRequest(new {
                    error = "Evidence IDs required"
                });
            }

            var evidenceFile = Read("EvidenceFile");

            if (string.IsNullOrWhiteSpace(evidenceFile) ||
                evidenceFile != Path.GetFileName(evidenceFile) ||
                !evidenceFile.StartsWith("combined-", StringComparison.Ordinal) ||
                !evidenceFile.EndsWith(".json", StringComparison.Ordinal))
            {
                return Results.BadRequest(new {
                    error = "Invalid evidence filename"
                });
            }

            var evidencePath = Path.Combine(
                root, "data", "combined-evidence", evidenceFile);

            if (!File.Exists(evidencePath))
                return Results.BadRequest(new {
                    error = "Evidence file missing"
                });

            // Revalidate evidence IDs immediately before preparation.
            using var evidenceDoc = JsonDocument.Parse(
                await File.ReadAllTextAsync(evidencePath, ct));

            var validIds = evidenceDoc.RootElement
                .GetProperty("Activities")
                .EnumerateArray()
                .Select(x => x.GetProperty("ActivityId").ToString())
                .ToHashSet(StringComparer.Ordinal);

            var selectedIds = ids.EnumerateArray()
                .Select(x => x.ValueKind == JsonValueKind.String
                    ? x.GetString() ?? ""
                    : "")
                .ToArray();

            if (selectedIds.Any(string.IsNullOrWhiteSpace) ||
                selectedIds.Distinct(StringComparer.Ordinal).Count()
                    != selectedIds.Length ||
                selectedIds.Any(x => !validIds.Contains(x)))
            {
                return Results.BadRequest(new {
                    error = "Invalid or duplicate linked evidence IDs"
                });
            }

            var directory = Path.Combine(root, "data", "draft-delivery");
            Directory.CreateDirectory(directory);

            var path = Path.Combine(
                directory, $"draft-{id}-delivery.json");

            var record = new
            {
                DeliveryId = Guid.NewGuid(),
                DraftId = id,
                Status = "Prepared",
                DeliveryStatus = "NotSent",
                Channel = "Unconfigured",
                Published = false,
                EmailSent = false,
                PreparedAt = DateTimeOffset.UtcNow,
                EvidenceFile = evidenceFile,
                EvidenceCount = ids.GetArrayLength()
            };

            try
            {
                // CreateNew prevents concurrent requests from overwriting.
                await using var stream = new FileStream(
                    path, FileMode.CreateNew, FileAccess.Write,
                    FileShare.None, 4096, useAsync: true);

                await JsonSerializer.SerializeAsync(
                    stream, record,
                    new JsonSerializerOptions { WriteIndented = true }, ct);

                await stream.FlushAsync(ct);

                return Results.Ok(record);
            }
            catch (IOException) when (File.Exists(path))
            {
                // Another request already created this delivery.
                // Wait briefly if the first writer is still finishing.
                for (var attempt = 0; attempt < 20; attempt++)
                {
                    try
                    {
                        using var existing = JsonDocument.Parse(
                            await File.ReadAllTextAsync(path, ct));

                        return Results.Ok(new {
                            alreadyPrepared = true,
                            delivery = existing.RootElement.Clone()
                        });
                    }
                    catch (IOException) when (attempt < 19)
                    {
                        await Task.Delay(50, ct);
                    }
                    catch (JsonException) when (attempt < 19)
                    {
                        await Task.Delay(50, ct);
                    }
                }

                return Results.Conflict(new {
                    error = "Existing delivery record is not readable"
                });
            }
        });
    }
}

