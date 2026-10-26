using System.Text.Json;

namespace DailyStatus.Api;

public static class ReportDeliveryEndpoints
{
    public static void MapReportDeliveryEndpoints(this WebApplication app)
    {
        // Legacy delivery is disabled outside Development.
        if (!app.Environment.IsDevelopment())
            return;

        var reportDir = Path.Combine(app.Environment.ContentRootPath, "WorkflowReports");
        var deliveryDir = Path.Combine(app.Environment.ContentRootPath, "DeliveryHistory");

        Directory.CreateDirectory(deliveryDir);

        app.MapPost("/api/delivery/prepare/{id:guid}", async (Guid id) =>
        {
            var reportPath = Path.Combine(reportDir, $"{id}.json");

            if (!File.Exists(reportPath))
                return Results.NotFound(new { error = "Report not found." });

            using var report = JsonDocument.Parse(
                await File.ReadAllTextAsync(reportPath));

            var root = report.RootElement;

            string GetText(string name)
            {
                foreach (var p in root.EnumerateObject())
                    if (string.Equals(p.Name, name,
                        StringComparison.OrdinalIgnoreCase))
                        return p.Value.ToString();
                return "";
            }

            var status = GetText("Status");

            if (!string.Equals(status, "Approved",
                StringComparison.OrdinalIgnoreCase))
                return Results.Conflict(new {
                    error = "Only approved reports can be prepared for delivery.",
                    status
                });

            var deliveryId = Guid.NewGuid();

            var record = new
            {
                deliveryId,
                reportId = id,
                reportDate = GetText("ReportDate"),
                status = "Prepared",
                deliveryStatus = "NotSent",
                channel = "Unconfigured",
                createdAtUtc = DateTime.UtcNow,
                summary = GetText("Summary")
            };

            var file = Path.Combine(deliveryDir, $"{deliveryId}.json");

            await File.WriteAllTextAsync(
                file,
                JsonSerializer.Serialize(record,
                    new JsonSerializerOptions { WriteIndented = true }));

            return Results.Ok(record);
        });

        app.MapGet("/api/delivery/history", async () =>
        {
            var records = new List<JsonElement>();

            foreach (var file in Directory.GetFiles(deliveryDir, "*.json"))
            {
                using var doc = JsonDocument.Parse(
                    await File.ReadAllTextAsync(file));

                records.Add(doc.RootElement.Clone());
            }

            return Results.Ok(records.OrderByDescending(
                r => r.GetProperty("createdAtUtc").GetDateTime()));
        });
    }
}

