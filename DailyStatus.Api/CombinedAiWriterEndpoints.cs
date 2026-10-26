using System.Net.Http.Json;
using System.Text.Json;

namespace DailyStatus.Api;

public static class CombinedAiWriterEndpoints
{
    public static void MapCombinedAiWriterEndpoints(this WebApplication app)
    {
        if (!app.Environment.IsDevelopment())
            return;

        app.MapPost("/api/status/ai/generate-combined", async (
            CombinedAiRequest selection,
            IConfiguration config,
            IHttpClientFactory factory,
            HttpContext context,
            CancellationToken ct) =>
        {
            if (context.Connection.RemoteIpAddress is not { } ip ||
                !System.Net.IPAddress.IsLoopback(ip))
                return Results.NotFound();

            var root = Path.GetFullPath(Path.Combine(
                app.Environment.ContentRootPath, ".."));

            var directory = Path.Combine(
                root, "data", "combined-evidence");

            if (!Directory.Exists(directory))
                return Results.NotFound(new { error = "Evidence directory missing" });

            var latest = Directory.GetFiles(directory, "combined-*.json")
                .OrderByDescending(Path.GetFileName)
                .FirstOrDefault();

            if (latest is null)
                return Results.NotFound(new { error = "No evidence found" });

            using var document = JsonDocument.Parse(
                await File.ReadAllTextAsync(latest, ct));

            var rootElement = document.RootElement;

            var activities = rootElement.GetProperty("Activities")
                .EnumerateArray()
                .ToList();

            if (activities.Count == 0)
                return Results.BadRequest(new { error = "No activities" });

            var selectedIds = selection.IncludedIds?
                .Where(id => !string.IsNullOrWhiteSpace(id))
                .Distinct(StringComparer.Ordinal)
                .ToHashSet(StringComparer.Ordinal);

            if (selectedIds is null || selectedIds.Count == 0)
                return Results.BadRequest(new {
                    error = "Select at least one evidence record."
                });

            var availableIds = activities
                .Select(a => a.GetProperty("ActivityId").ToString())
                .ToHashSet(StringComparer.Ordinal);

            if (!selectedIds.IsSubsetOf(availableIds))
                return Results.BadRequest(new {
                    error = "Unknown evidence ID."
                });

            activities = activities
                .Where(a => selectedIds.Contains(
                    a.GetProperty("ActivityId").ToString()))
                .ToList();

            var evidenceLines = activities.Select(activity =>
            {
                string Get(string key) =>
                    activity.TryGetProperty(key, out var value)
                        ? value.ToString()
                        : "";

                return
                    $"ID: {Get("ActivityId")}\n" +
                    $"Source: {Get("SourceType")}\n" +
                    $"Category: {Get("Category")}\n" +
                    $"Title: {Get("Title")}\n" +
                    $"Summary: {Get("Summary")}\n" +
                    $"Requires confirmation: {Get("RequiresUserConfirmation")}";
            });

            var endpoint = config["AzureAI:Endpoint"];
            var key = config["AzureAI:Key"];
            var deployment = config["AzureAI:Deployment"];

            if (string.IsNullOrWhiteSpace(endpoint) ||
                string.IsNullOrWhiteSpace(key) ||
                string.IsNullOrWhiteSpace(deployment))
                return Results.Problem(
                    "Azure AI configuration missing.",
                    statusCode: 503);

            var payload = new
            {
                model = deployment,
                instructions =
                    "Write a professional daily status report using ONLY " +
                    "the supplied evidence. Never invent achievements. " +
                    "Azure DevOps InProgress means work in progress, not completed. " +
                    "Outlook email activity does not prove task completion. " +
                    "Put all confirmation-required activities in a separate " +
                    "'Needs Human Review' section. " +
                    "Include Activity Summary, Current Status, Evidence, " +
                    "Needs Human Review, and Blockers. " +
                    "Do not claim approval or delivery.",
                input =
                    "Combined evidence:\n" +
                    string.Join("\n---\n", evidenceLines),
                max_output_tokens = 1200
            };

            using var client = factory.CreateClient();

            using var request = new HttpRequestMessage(
                HttpMethod.Post, endpoint);

            request.Headers.Add("api-key", key);
            request.Content = JsonContent.Create(payload);

            using var response = await client.SendAsync(request, ct);

            if (!response.IsSuccessStatusCode)
                return Results.Problem(
                    $"Azure AI HTTP {(int)response.StatusCode}",
                    statusCode: 502);

            using var stream =
                await response.Content.ReadAsStreamAsync(ct);

            using var result = await JsonDocument.ParseAsync(
                stream, cancellationToken: ct);

            var parts = new List<string>();

            if (result.RootElement.TryGetProperty(
                "output", out var output))
            {
                foreach (var item in output.EnumerateArray())
                {
                    if (!item.TryGetProperty("content", out var content))
                        continue;

                    foreach (var part in content.EnumerateArray())
                    {
                        if (part.TryGetProperty("type", out var type) &&
                            type.GetString() == "output_text" &&
                            part.TryGetProperty("text", out var text))
                        {
                            parts.Add(text.GetString() ?? "");
                        }
                    }
                }
            }

            var summary = string.Join("\n", parts).Trim();

            if (string.IsNullOrWhiteSpace(summary))
                return Results.Problem(
                    "AI returned no report text.", statusCode: 502);

            return Results.Ok(new
            {
                status = "PendingReview",
                approved = false,
                published = false,
                evidenceCount = activities.Count,
                evidenceFile = Path.GetFileName(latest),
                aiSummary = summary
            });
        });
    }
}

public sealed record CombinedAiRequest(string[] IncludedIds);