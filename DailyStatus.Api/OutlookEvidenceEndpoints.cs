using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace DailyStatus.Api;

public static class OutlookEvidenceEndpoints
{
    public static IEndpointRouteBuilder MapOutlookEvidenceEndpoints(
        this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/outlook-evidence");

        group.MapGet("/summary", (HttpContext context) =>
        {
            if (!IsLocalDevelopment(context))
                return Results.NotFound();

            var file = GetLatestEvidenceFile(context);

            if (file is null)
                return Results.Ok(new
                {
                    source = "OutlookCOM",
                    status = "Unavailable",
                    warning = "No collected Outlook evidence"
                });

            try
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(file));
                var root = doc.RootElement;
                var counts = root.GetProperty("SourceCounts");

                return Results.Ok(new
                {
                    source = "OutlookCOM",
                    status = root.GetProperty("Status").GetString(),
                    mailCount = counts.GetProperty("Mail").GetInt32(),
                    calendarCount = counts.GetProperty("Calendar").GetInt32(),
                    warningCount = root.GetProperty("Warnings").GetArrayLength(),
                    evidenceFile = Path.GetFileName(file),
                    classification = "NeedsReview",
                    approvedForPublishing = false
                });
            }
            catch
            {
                return Results.Problem(
                    title: "Outlook evidence could not be parsed",
                    statusCode: 500);
            }
        });

        group.MapGet("/latest", (HttpContext context) =>
        {
            if (!IsLocalDevelopment(context))
                return Results.NotFound();

            var file = GetLatestEvidenceFile(context);

            if (file is null)
                return Results.NotFound(new
                {
                    warning = "Outlook evidence unavailable"
                });

            try
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(file));

                return Results.Json(new
                {
                    source = "OutlookCOM",
                    evidence = doc.RootElement.Clone(),
                    policy = new
                    {
                        mailClassification = "NeedsReview",
                        meetingOutcomes = "Unverified",
                        completedWork = "Requires verified ADO evidence",
                        requiresHumanApproval = true
                    }
                });
            }
            catch
            {
                return Results.Problem(
                    title: "Outlook evidence could not be loaded",
                    statusCode: 500);
            }
        });

        return app;
    }

    private static bool IsLocalDevelopment(HttpContext context)
    {
        var environment = context.RequestServices
            .GetRequiredService<IWebHostEnvironment>();

        var remote = context.Connection.RemoteIpAddress;

        return environment.IsDevelopment()
            && remote is not null
            && IPAddress.IsLoopback(remote);
    }

    private static string? GetLatestEvidenceFile(HttpContext context)
    {
        var environment = context.RequestServices
            .GetRequiredService<IWebHostEnvironment>();

        var contentRoot = Path.GetFullPath(
            environment.ContentRootPath);

        // Supports dotnet run from solution root or API directory.
        var solutionRoot = Directory.Exists(
            Path.Combine(contentRoot, "DailyStatus.Api"))
            ? contentRoot
            : Path.GetFullPath(Path.Combine(contentRoot, ".."));

        var directory = Path.Combine(
            solutionRoot, "data", "outlook-evidence");

        if (!Directory.Exists(directory))
            return null;

        return Directory
            .EnumerateFiles(directory, "outlook-*.json")
            .OrderByDescending(Path.GetFileName)
            .FirstOrDefault();
    }
}
