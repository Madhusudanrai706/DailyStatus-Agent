using System.Net;
using System.Text.Json;
using DailyStatus.Agents;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace DailyStatus.Api;

public static class AdditionalEvidenceEndpoints
{
    public static IEndpointRouteBuilder MapAdditionalEvidenceEndpoints(
        this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/additional-evidence");

        group.MapGet("/pull-requests", async (
            HttpContext context,
            AzureDevOpsWorkItemAgent agent,
            CancellationToken ct) =>
        {
            if (!LocalOnly(context))
                return Results.NotFound();

            try
            {
                var result = await agent.GetPullRequestsAsync(ct);
                return Results.Json(new
                {
                    source = "AzureDevOpsPullRequests",
                    status = "ReadOnlyEvidence",
                    evidence = result,
                    requiresHumanApproval = true
                });
            }
            catch (HttpRequestException ex)
            {
                var upstream = ex.StatusCode.HasValue
                    ? ((int)ex.StatusCode.Value).ToString()
                    : "unknown";

                Console.Error.WriteLine(
                    $"PR_DIAGNOSTIC: Azure DevOps HTTP {upstream}");

                return Results.Problem(
                    "Unable to retrieve Azure DevOps pull requests.",
                    statusCode: 502);
            }
        });

        group.MapGet("/calendar", (HttpContext context) =>
        {
            if (!LocalOnly(context))
                return Results.NotFound();

            var root = Path.GetFullPath(
                Path.Combine(
                    context.RequestServices
                        .GetRequiredService<IWebHostEnvironment>()
                        .ContentRootPath,
                    ".."));

            var directory = Path.Combine(root, "data", "calendar-evidence");

            if (!Directory.Exists(directory))
                return Results.NotFound(new
                {
                    warning = "No local calendar evidence collected."
                });

            var file = Directory
                .EnumerateFiles(directory, "calendar-*.json")
                .OrderByDescending(Path.GetFileName)
                .FirstOrDefault();

            if (file is null)
                return Results.NotFound();

            using var document = JsonDocument.Parse(
                File.ReadAllText(file));

            return Results.Json(new
            {
                source = "OutlookLocalCalendar",
                evidence = document.RootElement.Clone(),
                classification = "ScheduledOnly",
                requiresHumanApproval = true
            });
        });

        return app;
    }

    private static bool LocalOnly(HttpContext context)
    {
        var environment = context.RequestServices
            .GetRequiredService<IWebHostEnvironment>();

        var remote = context.Connection.RemoteIpAddress;

        return environment.IsDevelopment()
            && remote is not null
            && IPAddress.IsLoopback(remote);
    }
}

