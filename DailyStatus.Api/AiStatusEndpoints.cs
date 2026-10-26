using System.Net.Http.Json;
using System.Text.Json;

namespace DailyStatus.Api;

public static class AiStatusEndpoints
{
    public static void MapAiStatusEndpoints(this WebApplication app)
    {
        app.MapPost("/api/status/ai/generate", async (
            DateOnly date,
            IConfiguration config,
            IHttpClientFactory factory,
            HttpContext context,
            CancellationToken ct) =>
        {
            var endpoint = config["AzureAI:Endpoint"];
            var key = config["AzureAI:Key"];
            var deployment = config["AzureAI:Deployment"];

            if (string.IsNullOrWhiteSpace(endpoint) ||
                string.IsNullOrWhiteSpace(key) ||
                string.IsNullOrWhiteSpace(deployment))
                return Results.Problem(
                    "Azure AI configuration missing.", statusCode: 503);

            using var client = factory.CreateClient();

            var sourceUrl =
                $"{context.Request.Scheme}://{context.Request.Host}" +
                $"/api/status/advanced?date={date:yyyy-MM-dd}";

            using var sourceResponse = await client.GetAsync(sourceUrl, ct);

            if (!sourceResponse.IsSuccessStatusCode)
                return Results.Problem(
                    "Evidence retrieval failed.", statusCode: 502);

            var evidence = await sourceResponse.Content
                .ReadFromJsonAsync<JsonElement>(cancellationToken: ct);

            var evidenceText =
                evidence.GetProperty("summary").GetString() ?? "";

            if (string.IsNullOrWhiteSpace(evidenceText))
                return Results.Ok(new
                {
                    reportDate = date,
                    status = "Draft",
                    aiSummary = "No verified activities found.",
                    evidence
                });

            var payload = new
            {
                model = deployment,
                instructions =
                    "Write a professional daily status report using only " +
                    "the supplied Azure DevOps evidence. " +
                    "Do not invent completed work or blockers. " +
                    "Separate Activity Summary, Current Status, and Evidence.",
                input =
                    $"Report date: {date:yyyy-MM-dd}\n" +
                    $"Verified evidence:\n{evidenceText}",
                max_output_tokens = 700
            };

            using var request =
                new HttpRequestMessage(HttpMethod.Post, endpoint);

            request.Headers.Add("api-key", key);
            request.Content = JsonContent.Create(payload);

            using var response = await client.SendAsync(request, ct);

            if (!response.IsSuccessStatusCode)
                return Results.Problem(
                    $"Azure AI returned HTTP {(int)response.StatusCode}.",
                    statusCode: 502);

            using var stream =
                await response.Content.ReadAsStreamAsync(ct);

            using var document =
                await JsonDocument.ParseAsync(
                    stream, cancellationToken: ct);

            var textParts = new List<string>();

            if (document.RootElement.TryGetProperty(
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
                            textParts.Add(text.GetString() ?? "");
                        }
                    }
                }
            }

            return Results.Ok(new
            {
                reportDate = date,
                status = "Draft",
                modelDeployment = deployment,
                aiSummary = string.Join("\n", textParts),
                evidence
            });
        });
    }
}
