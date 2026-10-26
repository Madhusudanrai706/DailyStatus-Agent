using DailyStatus.Agents;

namespace DailyStatus.Api;

public static class AzureDevOpsClassificationEndpoints
{
    public static void MapAzureDevOpsClassificationEndpoints(
        this WebApplication app)
    {
        app.MapGet(
            "/api/azure-devops/workitems/{id:int}/classification",
            async (
                int id,
                DateTimeOffset windowStart,
                DateTimeOffset windowEnd,
                AzureDevOpsWorkItemAgent agent,
                CancellationToken cancellationToken) =>
            {
                if (id <= 0 || windowStart > windowEnd)
                    return Results.BadRequest(new
                    {
                        error = "Invalid work item ID or reporting window."
                    });

                try
                {
                    var item = await agent.GetWorkItemAsync(
                        id, cancellationToken);

                    var revisions = await agent.GetWorkItemRevisionsAsync(
                        id, cancellationToken);

                    // Initial state mapping; project-specific values
                    // must be verified before production use.
                    var mappings = new Dictionary<string, string>(
                        StringComparer.OrdinalIgnoreCase)
                    {
                        ["Done"] = "Completed",
                        ["Closed"] = "Completed",
                        ["Resolved"] = "Completed",
                        ["Active"] = "InProgress",
                        ["In Progress"] = "InProgress",
                        ["Blocked"] = "Blocked"
                    };

                    var result =
                        AzureDevOpsActivityClassifier.ClassifyFromRevisions(
                            item,
                            revisions,
                            windowStart,
                            windowEnd,
                            mappings);

                    return Results.Ok(result);
                }
                catch (HttpRequestException)
                {
                    return Results.Problem(
                        "Unable to retrieve Azure DevOps evidence.",
                        statusCode: 502);
                }
            });
    }
}