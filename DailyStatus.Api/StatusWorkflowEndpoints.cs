using System.Text.Json;
using System.Text.Json.Serialization;
using DailyStatus.Agents;

namespace DailyStatus.Api;

public static class StatusWorkflowEndpoints
{
    static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() }
    };

    public static void MapStatusWorkflow(this WebApplication app)
    {
        var group = app.MapGroup("/api/status/workflow");

// Temporary development-only restriction.
// Production requires authenticated reviewer identity.
group.AddEndpointFilter(async (context, next) =>
{
    var http = context.HttpContext;
    var ip = http.Connection.RemoteIpAddress;

    if (!app.Environment.IsDevelopment() ||
        ip is null ||
        !System.Net.IPAddress.IsLoopback(ip))
    {
        return Results.NotFound();
    }

    return await next(context);
});

        group.MapPost("/generate", async (
            DateOnly date,
            IHttpClientFactory httpFactory,
            HttpContext context,
            CancellationToken ct) =>
        {
            var url = $"{context.Request.Scheme}://{context.Request.Host}/api/status/ai/generate?date={date:yyyy-MM-dd}";

            using var client = httpFactory.CreateClient();
            using var response = await client.PostAsync(url, null, ct);

            if (!response.IsSuccessStatusCode)
                return Results.Problem("Report generation failed.", statusCode: 502);

            var report = await response.Content.ReadFromJsonAsync<JsonElement>(
                cancellationToken: ct);

            var id = Guid.NewGuid();

            var workflow = new WorkflowReport(
                id,
                date,
                "Draft",
                report.GetProperty("aiSummary").GetString() ?? "",
                report.GetProperty("evidence").GetProperty("activityCount").GetInt32(),
                DateTimeOffset.UtcNow,
                null,
                null
            );

            var directory = Path.Combine(
                app.Environment.ContentRootPath, "WorkflowReports");

            Directory.CreateDirectory(directory);

            await Save(directory, workflow, ct);

            return Results.Ok(workflow);
        });

        group.MapGet("/reports", async () =>
        {
            var directory = Path.Combine(
                app.Environment.ContentRootPath, "WorkflowReports");

            if (!Directory.Exists(directory))
                return Results.Ok(Array.Empty<WorkflowReport>());

            var reports = new List<WorkflowReport>();

            foreach (var file in Directory.GetFiles(directory, "*.json"))
            {
                var report = JsonSerializer.Deserialize<WorkflowReport>(
                    await File.ReadAllTextAsync(file), JsonOptions);

                if (report != null)
                    reports.Add(report);
            }

            return Results.Ok(
                reports.OrderByDescending(x => x.CreatedAt));
        });

        group.MapGet("/{id:guid}", async (Guid id) =>
        {
            var report = await Load(app, id);

            return report == null
                ? Results.NotFound()
                : Results.Ok(report);
        });

        group.MapPost("/{id:guid}/approve", async (
            Guid id,
            CancellationToken ct) =>
        {
            var report = await Load(app, id);

            if (report == null)
                return Results.NotFound();

            if (report.Status != "Draft")
                return Results.Conflict(new
                {
                    error = "Only Draft reports can be approved."
                });

            var updated = report with
            {
                Status = "Approved",
                ReviewedAt = DateTimeOffset.UtcNow,
                ReviewNote = "Approved by API user"
            };

            await Save(GetDirectory(app), updated, ct);

            return Results.Ok(updated);
        });

        group.MapPost("/{id:guid}/reject", async (
            Guid id,
            CancellationToken ct) =>
        {
            var report = await Load(app, id);

            if (report == null)
                return Results.NotFound();

            if (report.Status != "Draft")
                return Results.Conflict(new
                {
                    error = "Only Draft reports can be rejected."
                });

            var updated = report with
            {
                Status = "Rejected",
                ReviewedAt = DateTimeOffset.UtcNow,
                ReviewNote = "Rejected by API user"
            };

            await Save(GetDirectory(app), updated, ct);

            return Results.Ok(updated);
        });
    }

    static string GetDirectory(WebApplication app) =>
        Path.Combine(app.Environment.ContentRootPath, "WorkflowReports");

    static async Task<WorkflowReport?> Load(
        WebApplication app, Guid id)
    {
        var file = Path.Combine(GetDirectory(app), $"{id}.json");

        if (!File.Exists(file))
            return null;

        return JsonSerializer.Deserialize<WorkflowReport>(
            await File.ReadAllTextAsync(file), JsonOptions);
    }

    static async Task Save(
        string directory,
        WorkflowReport report,
        CancellationToken ct)
    {
        Directory.CreateDirectory(directory);

        var file = Path.Combine(directory, $"{report.ReportId}.json");

        await File.WriteAllTextAsync(
            file,
            JsonSerializer.Serialize(report, JsonOptions),
            ct);
    }
}

public record WorkflowReport(
    Guid ReportId,
    DateOnly ReportDate,
    string Status,
    string Summary,
    int ActivityCount,
    DateTimeOffset CreatedAt,
    DateTimeOffset? ReviewedAt,
    string? ReviewNote);




