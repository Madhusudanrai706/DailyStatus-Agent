using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Configuration;

namespace DailyStatus.Agents;

public sealed class AzureDevOpsWorkItemAgent
{
    private readonly HttpClient _http;
    private readonly IConfiguration _config;

    public AzureDevOpsWorkItemAgent(
        HttpClient http,
        IConfiguration config)
    {
        _http = http;
        _config = config;
    }

    public async Task<JsonElement> GetWorkItemAsync(
        int id,
        CancellationToken cancellationToken = default)
    {
        var organization = _config["AzureDevOps:Organization"];
        var project = _config["AzureDevOps:Project"];
        var pat = _config["AzureDevOps:Pat"];

        if (string.IsNullOrWhiteSpace(organization) ||
            string.IsNullOrWhiteSpace(project) ||
            string.IsNullOrWhiteSpace(pat))
        {
            throw new InvalidOperationException(
                "Azure DevOps configuration is missing.");
        }

        var url =
            $"https://dev.azure.com/{Uri.EscapeDataString(organization)}/" +
            $"{Uri.EscapeDataString(project)}/_apis/wit/workitems/{id}" +
            "?api-version=7.1";

        using var request = new HttpRequestMessage(HttpMethod.Get, url);

        var credentials = Convert.ToBase64String(
            Encoding.ASCII.GetBytes($":{pat}"));

        request.Headers.Authorization =
            new AuthenticationHeaderValue("Basic", credentials);

        using var response = await _http.SendAsync(
            request, cancellationToken);

        response.EnsureSuccessStatusCode();

        using var stream = await response.Content.ReadAsStreamAsync(
            cancellationToken);

        using var document = await JsonDocument.ParseAsync(
            stream, cancellationToken: cancellationToken);

        return document.RootElement.Clone();
    }
    public async Task<JsonElement> GetWorkItemsAsync(
        CancellationToken cancellationToken = default)
    {
        var organization = _config["AzureDevOps:Organization"];
        var project = _config["AzureDevOps:Project"];
        var pat = _config["AzureDevOps:Pat"];

        if (string.IsNullOrWhiteSpace(organization) ||
            string.IsNullOrWhiteSpace(project) ||
            string.IsNullOrWhiteSpace(pat))
        {
            throw new InvalidOperationException(
                "Azure DevOps configuration is missing.");
        }

        var url =
            $"https://dev.azure.com/{Uri.EscapeDataString(organization)}/" +
            $"{Uri.EscapeDataString(project)}/_apis/wit/wiql" +
            "?api-version=7.1";

        var query = new
        {
            query = "SELECT [System.Id] FROM WorkItems " +
                    "WHERE [System.TeamProject] = @project " +
                    "ORDER BY [System.ChangedDate] DESC"
        };

        using var request = new HttpRequestMessage(
            HttpMethod.Post, url);

        request.Headers.Authorization =
            new AuthenticationHeaderValue(
                "Basic",
                Convert.ToBase64String(
                    Encoding.ASCII.GetBytes($":{pat}")));

        request.Content = new StringContent(
            JsonSerializer.Serialize(query),
            Encoding.UTF8,
            "application/json");

        using var response = await _http.SendAsync(
            request, cancellationToken);

        response.EnsureSuccessStatusCode();

        using var stream = await response.Content.ReadAsStreamAsync(
            cancellationToken);

        using var document = await JsonDocument.ParseAsync(
            stream, cancellationToken: cancellationToken);

        return document.RootElement.Clone();
    }
    public async Task<JsonElement> GetWorkItemRevisionsAsync(
        int id,
        CancellationToken cancellationToken = default)
    {
        var organization = _config["AzureDevOps:Organization"];
        var project = _config["AzureDevOps:Project"];
        var pat = _config["AzureDevOps:Pat"];

        if (string.IsNullOrWhiteSpace(organization) ||
            string.IsNullOrWhiteSpace(project) ||
            string.IsNullOrWhiteSpace(pat))
        {
            throw new InvalidOperationException(
                "Azure DevOps configuration is missing.");
        }

        var url =
            $"https://dev.azure.com/{Uri.EscapeDataString(organization)}/" +
            $"{Uri.EscapeDataString(project)}/_apis/wit/workitems/{id}/revisions" +
            "?api-version=7.1";

        using var request = new HttpRequestMessage(HttpMethod.Get, url);

        request.Headers.Authorization = new AuthenticationHeaderValue(
            "Basic",
            Convert.ToBase64String(Encoding.ASCII.GetBytes($":{pat}")));

        using var response = await _http.SendAsync(
            request, cancellationToken);

        response.EnsureSuccessStatusCode();

        using var stream = await response.Content.ReadAsStreamAsync(
            cancellationToken);

        using var document = await JsonDocument.ParseAsync(
            stream, cancellationToken: cancellationToken);

        return document.RootElement.Clone();
    }
    public async Task<JsonElement> GetPullRequestsAsync(
        CancellationToken cancellationToken = default)
    {
        var organization = _config["AzureDevOps:Organization"];
        var project = _config["AzureDevOps:Project"];
        var pat = _config["AzureDevOps:Pat"];

        if (string.IsNullOrWhiteSpace(organization) ||
            string.IsNullOrWhiteSpace(project) ||
            string.IsNullOrWhiteSpace(pat))
            throw new InvalidOperationException(
                "Azure DevOps configuration is missing.");

        var baseUrl =
            $"https://dev.azure.com/{Uri.EscapeDataString(organization)}/" +
            $"{Uri.EscapeDataString(project)}/_apis/git/";

        async Task<JsonElement> Fetch(string url)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, url);

            request.Headers.Authorization =
                new AuthenticationHeaderValue(
                    "Basic",
                    Convert.ToBase64String(
                        Encoding.ASCII.GetBytes($":{pat}")));

            using var response = await _http.SendAsync(
                request, cancellationToken);

            if (!response.IsSuccessStatusCode)
{
    throw new HttpRequestException(
        $"Azure DevOps upstream HTTP {(int)response.StatusCode} ({response.ReasonPhrase})",
        null,
        response.StatusCode);
}

            using var stream = await response.Content.ReadAsStreamAsync(
                cancellationToken);

            using var document = await JsonDocument.ParseAsync(
                stream, cancellationToken: cancellationToken);

            return document.RootElement.Clone();
        }

        var repositories = await Fetch(
            baseUrl + "repositories?api-version=7.1");

        var pullRequests = new List<JsonElement>();

        foreach (var repository in
            repositories.GetProperty("value").EnumerateArray())
        {
            var repositoryId = repository.GetProperty("id").GetString();

            if (string.IsNullOrWhiteSpace(repositoryId))
                continue;

            var result = await Fetch(
                baseUrl +
                $"repositories/{Uri.EscapeDataString(repositoryId)}/pullrequests" +
                "?searchCriteria.status=all&$top=100&api-version=7.1");

            foreach (var pr in result.GetProperty("value").EnumerateArray())
                pullRequests.Add(pr.Clone());
        }

        return JsonSerializer.SerializeToElement(new
        {
            count = pullRequests.Count,
            value = pullRequests
        });
    }
}

