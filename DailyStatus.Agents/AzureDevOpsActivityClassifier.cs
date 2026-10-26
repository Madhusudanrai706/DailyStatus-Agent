using System.Text.Json;

namespace DailyStatus.Agents;

public sealed record AdoActivityEvidence(
    int WorkItemId,
    string Title,
    string WorkItemType,
    string State,
    string Category,
    string EvidenceId,
    string SourceUrl,
    DateTimeOffset? ChangedAt,
    string ClassificationReason,
    bool RequiresReview
);

public static class AzureDevOpsActivityClassifier
{
        public static AdoActivityEvidence ClassifyFromRevisions(
        JsonElement item,
        JsonElement revisions,
        DateTimeOffset windowStart,
        DateTimeOffset windowEnd,
        IReadOnlyDictionary<string, string> stateMappings)
    {
        var result = Classify(
            item, windowStart, windowEnd, stateMappings);

        if (revisions.ValueKind != JsonValueKind.Object ||
            !revisions.TryGetProperty("value", out var entries) ||
            entries.ValueKind != JsonValueKind.Array)
            return result with
            {
                Category = "NeedsReview",
                RequiresReview = true,
                ClassificationReason = "Revision evidence unavailable."
            };

        string? previousState = null;
        bool completedTransition = false;
        bool blockerEvidence = false;

        foreach (var revision in entries.EnumerateArray()
            .OrderBy(r =>
                r.TryGetProperty("rev", out var number)
                    ? number.GetInt32() : 0))
        {
            if (!revision.TryGetProperty("fields", out var fields) ||
                fields.ValueKind != JsonValueKind.Object)
                continue;

            string Read(string key) =>
                fields.TryGetProperty(key, out var value)
                    ? value.ToString()
                    : "";

            var state = Read("System.State");

            if (!DateTimeOffset.TryParse(
                Read("System.ChangedDate"), out var changed))
            {
                previousState = state;
                continue;
            }

            if (changed >= windowStart && changed <= windowEnd)
            {
                if (stateMappings.TryGetValue(state, out var category) &&
                    category == "Completed" &&
                    previousState != state)
                {
                    completedTransition = true;
                }

                if (stateMappings.TryGetValue(state, out var mapped) &&
                    mapped == "Blocked")
                {
                    var reason = Read("System.Reason");
                    blockerEvidence = !string.IsNullOrWhiteSpace(reason);
                }
            }

            previousState = state;
        }

        if (completedTransition)
            return result with
            {
                Category = "Completed",
                RequiresReview = false,
                ClassificationReason =
                    "Confirmed completed-state transition in reporting window."
            };

        if (blockerEvidence)
            return result with
            {
                Category = "Blocked",
                RequiresReview = true,
                ClassificationReason =
                    "Blocked state with reason; human confirmation required."
            };

        return result;
    }
public static AdoActivityEvidence Classify(
        JsonElement item,
        DateTimeOffset windowStart,
        DateTimeOffset windowEnd,
        IReadOnlyDictionary<string, string>? stateMappings = null)
    {
        if (windowEnd < windowStart)
            throw new ArgumentException("Invalid reporting window.");

        var id = item.TryGetProperty("id", out var idValue)
            && idValue.ValueKind == JsonValueKind.Number
            ? idValue.GetInt32() : 0;

        var fields = item.TryGetProperty("fields", out var f)
            ? f : default;

        string Field(string name)
        {
            if (fields.ValueKind != JsonValueKind.Object ||
                !fields.TryGetProperty(name, out var value))
                return "";

            return value.ValueKind == JsonValueKind.String
                ? value.GetString() ?? ""
                : value.ToString();
        }

        var title = Field("System.Title");
        var state = Field("System.State");
        var type = Field("System.WorkItemType");

        DateTimeOffset? changedAt = null;

        if (DateTimeOffset.TryParse(
            Field("System.ChangedDate"), out var parsed))
            changedAt = parsed;

        var sourceUrl = item.TryGetProperty("_links", out var links)
            && links.ValueKind == JsonValueKind.Object
            && links.TryGetProperty("html", out var html)
            && html.ValueKind == JsonValueKind.Object
            && html.TryGetProperty("href", out var href)
            ? href.GetString() ?? ""
            : "";

        var category = "NeedsReview";
        var reason = "State mapping or reporting-window evidence is insufficient.";

        var inWindow = changedAt.HasValue &&
            changedAt.Value >= windowStart &&
            changedAt.Value <= windowEnd;

        if (inWindow && stateMappings is not null &&
            stateMappings.TryGetValue(state, out var mapped))
        {
            if (mapped is "Completed" or "InProgress" or "Blocked")
            {
                category = mapped;
                reason = "Configured state mapping; verify revision history for transitions.";
            }
        }

        // Never claim completion from a current-state snapshot alone.
        if (category == "Completed")
        {
            category = "NeedsReview";
            reason = "Completion requires a confirmed in-window state transition from revisions.";
        }

        var evidenceId = id > 0
            ? $"ADO-WI-{id}"
            : "ADO-WI-UNKNOWN";

        return new AdoActivityEvidence(
            id,
            title,
            type,
            state,
            category,
            evidenceId,
            sourceUrl,
            changedAt,
            reason,
            category == "NeedsReview"
        );
    }
}