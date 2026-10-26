using System.Text.Json;
using DailyStatus.Agents;

namespace DailyStatus.Api;

public static class AdvancedStatusEndpoints
{
    public static void MapAdvancedStatusEndpoints(this WebApplication app)
    {
        app.MapGet("/api/status/advanced", async (
            DateOnly date,
            AzureDevOpsWorkItemAgent agent,
            CancellationToken ct) =>
        {
            try
            {
                var query = await agent.GetWorkItemsAsync(ct);
                var activities = new List<Activity>();

                foreach (var reference in query.GetProperty("workItems").EnumerateArray())
                {
                    var id = reference.GetProperty("id").GetInt32();
                    var revisions = await agent.GetWorkItemRevisionsAsync(id, ct);

                    JsonElement? previous = null;

                    foreach (var revision in revisions.GetProperty("value").EnumerateArray())
                    {
                        var fields = revision.GetProperty("fields");

                        string? Read(JsonElement f, string key)
                        {
                            return f.TryGetProperty(key, out var value)
                                ? value.ValueKind == JsonValueKind.Object &&
                                  value.TryGetProperty("displayName", out var name)
                                    ? name.GetString()
                                    : value.ToString()
                                : null;
                        }

                        var changedText = Read(fields, "System.ChangedDate");

                        if (!DateTimeOffset.TryParse(changedText, out var changed))
                        {
                            previous = revision;
                            continue;
                        }

                        if (DateOnly.FromDateTime(changed.UtcDateTime) == date)
                        {
                            var title = Read(fields, "System.Title") ?? "Untitled";
                            var state = Read(fields, "System.State") ?? "Unknown";
                            var rev = revision.GetProperty("rev").GetInt32();

                            var changes = new List<string>();

                            if (previous is null)
                            {
                                changes.Add("Work item created");
                            }
                            else
                            {
                                var oldFields = previous.Value.GetProperty("fields");

                                foreach (var key in new[]
                                {
                                    "System.State",
                                    "System.Title",
                                    "System.AssignedTo",
                                    "Microsoft.VSTS.Common.Priority"
                                })
                                {
                                    var oldValue = Read(oldFields, key);
                                    var newValue = Read(fields, key);

                                    if (oldValue != newValue)
                                    {
                                        changes.Add(
                                            $"{key}: {oldValue ?? "(empty)"} -> {newValue ?? "(empty)"}");
                                    }
                                }
                            }

                            activities.Add(new Activity(
                                id, rev, title, state, changed, changes));
                        }

                        previous = revision;
                    }
                }

                var ordered = activities
                    .OrderBy(x => x.ChangedDate)
                    .ToList();

                var lines = ordered.Select(x =>
                    $"Task #{x.WorkItemId} - {x.Title}: " +
                    (x.Changes.Count > 0
                        ? string.Join("; ", x.Changes)
                        : "Revision recorded; tracked fields unchanged"));

                var report = new AdvancedReport(
                    Guid.NewGuid(),
                    date,
                    "Draft",
                    ordered.Count,
                    string.Join(Environment.NewLine, lines),
                    ordered);

                var directory = Path.Combine(
                    app.Environment.ContentRootPath, "Reports");

                Directory.CreateDirectory(directory);

                var filename = Path.Combine(
                    directory, $"{report.ReportId}.json");

                await File.WriteAllTextAsync(
                    filename,
                    JsonSerializer.Serialize(
                        report,
                        new JsonSerializerOptions { WriteIndented = true }),
                    ct);

                return Results.Ok(report);
            }
            catch (HttpRequestException)
            {
                return Results.Problem(
                    "Azure DevOps request failed.",
                    statusCode: 502);
            }
        });

        app.MapGet("/api/status/reports/{id:guid}", async (
            Guid id) =>
        {
            var filename = Path.Combine(
                app.Environment.ContentRootPath,
                "Reports",
                $"{id}.json");

            if (!File.Exists(filename))
                return Results.NotFound();

            var json = await File.ReadAllTextAsync(filename);

            return Results.Content(json, "application/json");
        });
    }
}

public record Activity(
    int WorkItemId,
    int Revision,
    string Title,
    string State,
    DateTimeOffset ChangedDate,
    List<string> Changes);

public record AdvancedReport(
    Guid ReportId,
    DateOnly ReportDate,
    string Status,
    int ActivityCount,
    string Summary,
    List<Activity> Activities);
