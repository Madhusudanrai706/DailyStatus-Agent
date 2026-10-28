using System.Globalization;
using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace DailyStatus.Api;

public static class EvidenceHistoryEndpoints
{
    public static IEndpointRouteBuilder MapEvidenceHistoryEndpoints(
        this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/evidence-history", (
            HttpContext context,
            string? date) =>
        {
            var environment = context.RequestServices
                .GetRequiredService<IWebHostEnvironment>();

            if (!environment.IsDevelopment() ||
                context.Connection.RemoteIpAddress is not { } ip ||
                !IPAddress.IsLoopback(ip))
                return Results.NotFound();

            if (!DateOnly.TryParseExact(
                    date,
                    "yyyy-MM-dd",
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.None,
                    out var selectedDate))
                return Results.BadRequest(new
                {
                    error = "Use date=yyyy-MM-dd"
                });

            var contentRoot = Path.GetFullPath(
                environment.ContentRootPath);

            var solutionRoot = Directory.Exists(
                Path.Combine(contentRoot, "DailyStatus.Api"))
                ? contentRoot
                : Path.GetFullPath(Path.Combine(contentRoot, ".."));

            var directory = Path.Combine(
                solutionRoot, "data", "combined-evidence");

            if (!Directory.Exists(directory))
                return Results.Ok(new
                {
                    date = selectedDate.ToString("yyyy-MM-dd"),
                    total = 0,
                    activities = Array.Empty<object>()
                });

            var files = Directory.EnumerateFiles(
                    directory, "combined-*.json")
                .OrderByDescending(Path.GetFileName)
                .ToArray();

            var activities = new Dictionary<string, object>();
            var snapshotsRead = 0;

            foreach (var file in files)
            {
                try
                {
                    using var doc = JsonDocument.Parse(
                        File.ReadAllText(file));

                    var root = doc.RootElement;

                    if (!root.TryGetProperty(
                            "Activities", out var records) ||
                        records.ValueKind != JsonValueKind.Array)
                        continue;

                    snapshotsRead++;

                    foreach (var record in records.EnumerateArray())
                    {
                        if (!record.TryGetProperty(
                                "OccurredAt", out var occurred))
                            continue;

                        var timestamp = occurred.ToString();

                        if (!DateTimeOffset.TryParse(
                                timestamp,
                                CultureInfo.InvariantCulture,
                                DateTimeStyles.None,
                                out var occurredAt))
                            continue;

                        if (DateOnly.FromDateTime(
                                occurredAt.LocalDateTime) != selectedDate)
                            continue;

                        if (!record.TryGetProperty(
                                "ActivityId", out var activityId))
                            continue;

                        var id = activityId.ToString();

                        if (string.IsNullOrWhiteSpace(id))
                            continue;

                        if (record.TryGetProperty("SourceType", out var source) &&
                            source.ToString() == "OutlookCalendar" &&
                            record.TryGetProperty("Title", out var title))
                        {
                            var normalizedTitle = title.ToString().Trim().ToLowerInvariant();
                            id = "CALENDAR|" + normalizedTitle + "|" +
                                 occurredAt.ToString("yyyy-MM-ddTHH:mm:ss");
                        }

                        if (!activities.ContainsKey(id))
                            activities[id] = record.Clone();
                    }
                }
                catch (JsonException)
                {
                    // Ignore malformed historical snapshots.
                }
                catch (IOException)
                {
                    // Ignore snapshots being written by sync.
                }
            }

            return Results.Ok(new
            {
                date = selectedDate.ToString("yyyy-MM-dd"),
                total = activities.Count,
                snapshotsRead,
                activities = activities.Values
            });
        });

        return app;
    }
}