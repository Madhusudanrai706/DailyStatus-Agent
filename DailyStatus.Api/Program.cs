using DailyStatus.Api;
using System.Text.Json.Serialization;

var builder = WebApplication.CreateBuilder(args);

builder.Services.ConfigureHttpJsonOptions(options =>
{
    options.SerializerOptions.Converters.Add(
        new JsonStringEnumConverter());
});

builder.Services.AddHttpClient<DailyStatus.Agents.AzureDevOpsWorkItemAgent>();

var app = builder.Build();

// STEP 119: Restrict legacy delivery routes to local development.
app.Use(async (context, next) =>
{
    if (context.Request.Path.StartsWithSegments("/api/delivery"))
    {
        var ip = context.Connection.RemoteIpAddress;

        if (!app.Environment.IsDevelopment() ||
            ip is null ||
            !System.Net.IPAddress.IsLoopback(ip))
        {
            context.Response.StatusCode = 404;
            return;
        }
    }

    await next();
});
// MapDeliverySecurityGuard
app.MapAzureDevOpsClassificationEndpoints();
app.MapOutlookEvidenceEndpoints();
app.MapAdditionalEvidenceEndpoints();

// BRICKRED_LOCAL_API_GUARD
// Development-only protection: reject non-loopback clients.
if (app.Environment.IsDevelopment())
{
    app.Use(async (context, next) =>
    {
        var remoteIp = context.Connection.RemoteIpAddress;

        if (remoteIp is null ||
            !System.Net.IPAddress.IsLoopback(remoteIp))
        {
            context.Response.StatusCode = 403;
            await context.Response.WriteAsync(
                "Local access only.");
            return;
        }

        await next();
    });
}

app.MapGet("/health", () =>
    Results.Ok(new
    {
        status = "Healthy",
        service = "Brickred Daily Status Agent",
        timestamp = DateTimeOffset.UtcNow
    }));

app.MapPost("/api/status/generate",
    (GenerateStatusRequest request) =>
    {
        if (request.WindowStart > request.WindowEnd)
        {
            return Results.BadRequest(new
            {
                error = "WindowStart cannot be after WindowEnd"
            });
        }

        var report = new StatusReport(
            Guid.NewGuid(),
            request.WindowStart,
            request.WindowEnd,
            "Draft",
            new List<StatusActivity>()
        );

        return Results.Ok(report);
    });

app.MapGet("/api/azure-devops/workitems/{id:int}",
    async (
        int id,
        DailyStatus.Agents.AzureDevOpsWorkItemAgent agent,
        CancellationToken cancellationToken) =>
    {
        try
        {
            var workItem = await agent.GetWorkItemAsync(
                id, cancellationToken);

            return Results.Ok(workItem);
        }
        catch (HttpRequestException)
        {
            return Results.Problem(
                "Unable to retrieve the Azure DevOps work item.",
                statusCode: 502);
        }
    });
app.MapGet("/api/azure-devops/workitems/{id:int}/summary",
    async (
        int id,
        DailyStatus.Agents.AzureDevOpsWorkItemAgent agent,
        CancellationToken cancellationToken) =>
    {
        if (id <= 0)
            return Results.BadRequest(new { error = "Invalid work item ID" });

        try
        {
            var item = await agent.GetWorkItemAsync(
                id, cancellationToken);

            var fields = item.GetProperty("fields");

            string? ReadField(string name)
            {
                return fields.TryGetProperty(name, out var value)
                    ? value.ToString()
                    : null;
            }

            string? assignedTo = null;

            if (fields.TryGetProperty(
                "System.AssignedTo", out var assigned))
            {
                if (assigned.ValueKind ==
                    System.Text.Json.JsonValueKind.Object &&
                    assigned.TryGetProperty(
                        "displayName", out var displayName))
                {
                    assignedTo = displayName.GetString();
                }
            }

            return Results.Ok(new
            {
                id = item.GetProperty("id").GetInt32(),
                title = ReadField("System.Title"),
                state = ReadField("System.State"),
                type = ReadField("System.WorkItemType"),
                assignedTo,
                createdDate = ReadField("System.CreatedDate"),
                changedDate = ReadField("System.ChangedDate"),
                priority = ReadField("Microsoft.VSTS.Common.Priority")
            });
        }
        catch (HttpRequestException)
        {
            return Results.Problem(
                "Unable to retrieve work item.",
                statusCode: 502);
        }
    });
app.MapGet("/api/azure-devops/workitems",
    async (
        DailyStatus.Agents.AzureDevOpsWorkItemAgent agent,
        CancellationToken cancellationToken) =>
    {
        try
        {
            var result = await agent.GetWorkItemsAsync(
                cancellationToken);

            return Results.Ok(result);
        }
        catch (HttpRequestException)
        {
            return Results.Problem(
                "Unable to query Azure DevOps work items.",
                statusCode: 502);
        }
    });
app.MapGet("/api/status/azure-devops",
    async (
        DailyStatus.Agents.AzureDevOpsWorkItemAgent agent,
        CancellationToken cancellationToken) =>
    {
        try
        {
            var queryResult = await agent.GetWorkItemsAsync(
                cancellationToken);

            var tasks = new List<object>();
            int completed = 0;
            int inProgress = 0;
            int blocked = 0;
            int notStarted = 0;
            int other = 0;

            foreach (var reference in queryResult
                .GetProperty("workItems").EnumerateArray())
            {
                var id = reference.GetProperty("id").GetInt32();

                var item = await agent.GetWorkItemAsync(
                    id, cancellationToken);

                var fields = item.GetProperty("fields");

                string? ReadField(string name) =>
                    fields.TryGetProperty(name, out var value)
                        ? value.ToString()
                        : null;

                var state = ReadField("System.State") ?? "Unknown";
                var title = ReadField("System.Title") ?? "Untitled";

                var category = state.ToLowerInvariant() switch
                {
                    "new" or "to do" or "proposed" =>
                        "Not Started",

                    "active" or "in progress" or "committed" =>
                        "In Progress",

                    "closed" or "done" or "completed" =>
                        "Completed",

                    "blocked" =>
                        "Blocked",

                    _ => "Other"
                };

                switch (category)
                {
                    case "Completed": completed++; break;
                    case "In Progress": inProgress++; break;
                    case "Blocked": blocked++; break;
                    case "Not Started": notStarted++; break;
                    default: other++; break;
                }

                tasks.Add(new
                {
                    id,
                    title,
                    state,
                    category,
                    changedDate = ReadField("System.ChangedDate")
                });
            }

            return Results.Ok(new
            {
                totalTasks = tasks.Count,
                completed,
                inProgress,
                blocked,
                notStarted,
                other,
                tasks
            });
        }
        catch (HttpRequestException)
        {
            return Results.Problem(
                "Unable to retrieve Azure DevOps status.",
                statusCode: 502);
        }
    });
app.MapGet("/api/status/daily",
    async (
        DateOnly date,
        DailyStatus.Agents.AzureDevOpsWorkItemAgent agent,
        CancellationToken cancellationToken) =>
    {
        if (date > DateOnly.FromDateTime(DateTime.UtcNow))
            return Results.BadRequest(new { error = "Future dates are not supported." });

        try
        {
            var query = await agent.GetWorkItemsAsync(cancellationToken);
            var changedTasks = new List<object>();
            int total = 0;

            foreach (var reference in query.GetProperty("workItems").EnumerateArray())
            {
                var id = reference.GetProperty("id").GetInt32();
                var item = await agent.GetWorkItemAsync(id, cancellationToken);
                total++;

                var fields = item.GetProperty("fields");

                string? Field(string name) =>
                    fields.TryGetProperty(name, out var value)
                        ? value.ToString()
                        : null;

                var changed = Field("System.ChangedDate");

                if (!DateTimeOffset.TryParse(changed, out var changedAt))
                    continue;

                if (DateOnly.FromDateTime(changedAt.UtcDateTime) != date)
                    continue;

                var state = Field("System.State") ?? "Unknown";

                var category = state.ToLowerInvariant() switch
                {
                    "new" or "to do" or "proposed" => "Not Started",
                    "active" or "in progress" or "committed" => "In Progress",
                    "closed" or "done" or "completed" => "Completed",
                    "blocked" => "Blocked",
                    _ => "Other"
                };

                changedTasks.Add(new
                {
                    id,
                    title = Field("System.Title"),
                    state,
                    category,
                    changedDate = changedAt
                });
            }

            return Results.Ok(new
            {
                reportDate = date,
                dateBasis = "UTC",
                totalCurrentWorkItems = total,
                changedTaskCount = changedTasks.Count,
                changedTasks,
                note = "Current states of tasks whose latest change was on the selected UTC date. Not a historical activity log."
            });
        }
        catch (HttpRequestException)
        {
            return Results.Problem(
                "Unable to retrieve Azure DevOps work items.",
                statusCode: 502);
        }
    });
app.MapGet("/api/azure-devops/workitems/{id:int}/revisions",
    async (
        int id,
        DailyStatus.Agents.AzureDevOpsWorkItemAgent agent,
        CancellationToken cancellationToken) =>
    {
        if (id <= 0)
            return Results.BadRequest(new { error = "Invalid work item ID" });

        try
        {
            var revisions = await agent.GetWorkItemRevisionsAsync(
                id, cancellationToken);

            return Results.Ok(revisions);
        }
        catch (HttpRequestException)
        {
            return Results.Problem(
                "Unable to retrieve work item revisions.",
                statusCode: 502);
        }
    });
app.MapGet("/api/status/history",
    async (
        DateOnly date,
        DailyStatus.Agents.AzureDevOpsWorkItemAgent agent,
        CancellationToken cancellationToken) =>
    {
        try
        {
            var query = await agent.GetWorkItemsAsync(cancellationToken);
            var activities = new List<object>();

            foreach (var reference in query.GetProperty("workItems").EnumerateArray())
            {
                var id = reference.GetProperty("id").GetInt32();

                var revisions = await agent.GetWorkItemRevisionsAsync(
                    id, cancellationToken);

                foreach (var revision in revisions.GetProperty("value").EnumerateArray())
                {
                    var fields = revision.GetProperty("fields");

                    string? Field(string name) =>
                        fields.TryGetProperty(name, out var value)
                            ? value.ToString()
                            : null;

                    if (!DateTimeOffset.TryParse(
                        Field("System.ChangedDate"), out var changedAt))
                        continue;

                    if (DateOnly.FromDateTime(changedAt.UtcDateTime) != date)
                        continue;

                    activities.Add(new
                    {
                        workItemId = id,
                        revision = revision.GetProperty("rev").GetInt32(),
                        title = Field("System.Title"),
                        state = Field("System.State"),
                        changedDate = changedAt,
                        evidenceSource = "Azure DevOps Work Item Revision"
                    });
                }
            }

            return Results.Ok(new
            {
                reportDate = date,
                dateBasis = "UTC",
                activityCount = activities.Count,
                activities,
                note = "Revision records on the selected UTC date. A revision does not necessarily mean task completion."
            });
        }
        catch (HttpRequestException)
        {
            return Results.Problem(
                "Unable to retrieve Azure DevOps revision history.",
                statusCode: 502);
        }
    });
app.MapGet("/api/status/write",
    async (
        DateOnly date,
        DailyStatus.Agents.AzureDevOpsWorkItemAgent agent,
        CancellationToken cancellationToken) =>
    {
        try
        {
            var query = await agent.GetWorkItemsAsync(cancellationToken);
            var lines = new List<string>();
            var evidence = new List<object>();

            foreach (var reference in query.GetProperty("workItems").EnumerateArray())
            {
                var id = reference.GetProperty("id").GetInt32();
                var revisions = await agent.GetWorkItemRevisionsAsync(
                    id, cancellationToken);

                foreach (var revision in revisions.GetProperty("value").EnumerateArray())
                {
                    var fields = revision.GetProperty("fields");

                    string? Field(string name) =>
                        fields.TryGetProperty(name, out var value)
                            ? value.ToString()
                            : null;

                    if (!DateTimeOffset.TryParse(
                        Field("System.ChangedDate"), out var changedAt))
                        continue;

                    if (DateOnly.FromDateTime(changedAt.UtcDateTime) != date)
                        continue;

                    var rev = revision.GetProperty("rev").GetInt32();
                    var title = Field("System.Title") ?? "Untitled";
                    var state = Field("System.State") ?? "Unknown";

                    var activity = rev == 1
                        ? $"Created work item #{id}: {title} (State: {state})."
                        : $"Updated work item #{id}: {title} (State at revision: {state}).";

                    lines.Add(activity);

                    evidence.Add(new
                    {
                        workItemId = id,
                        revision = rev,
                        changedDate = changedAt,
                        state,
                        evidenceSource = "Azure DevOps Revision History"
                    });
                }
            }

            var report = lines.Count == 0
                ? "No Azure DevOps work item revisions found for the selected UTC date."
                : string.Join(Environment.NewLine, lines);

            return Results.Ok(new
            {
                reportDate = date,
                dateBasis = "UTC",
                status = "Draft",
                title = $"Daily Status - {date:yyyy-MM-dd}",
                summary = report,
                evidenceCount = evidence.Count,
                evidence,
                note = "Draft generated from revision records. Updates are not automatically classified as completed work."
            });
        }
        catch (HttpRequestException)
        {
            return Results.Problem(
                "Unable to generate the evidence-based status draft.",
                statusCode: 502);
        }
    });
app.MapAdvancedStatusEndpoints();
app.MapStatusWorkflow();
app.UseDefaultFiles();
app.UseStaticFiles();
app.MapAiStatusEndpoints();
app.MapReportDeliveryEndpoints();

app.MapLocalReviewEndpoints();

app.MapCombinedAiWriterEndpoints();

app.MapLocalDraftReviewEndpoints();

app.MapDraftDeliveryEndpoints();




app.Run();

public record GenerateStatusRequest(
    DateOnly WindowStart,
    DateOnly WindowEnd
);

public record StatusActivity(
    string Source,
    string Title,
    string State
);

public record StatusReport(
    Guid ReportId,
    DateOnly WindowStart,
    DateOnly WindowEnd,
    string Status,
    List<StatusActivity> Activities
);
























