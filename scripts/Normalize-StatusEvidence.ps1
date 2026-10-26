param(
    [string]$InputDirectory = ".\data\outlook-evidence",
    [string]$OutputDirectory = ".\data\normalized-evidence"
)

$ErrorActionPreference = "Stop"

$latest = Get-ChildItem $InputDirectory -Filter "outlook-*.json" |
    Sort-Object Name -Descending |
    Select-Object -First 1

if (!$latest) {
    throw "No Outlook evidence JSON found."
}

$raw = Get-Content $latest.FullName -Raw | ConvertFrom-Json

$items = @()
$warnings = @($raw.Warnings | Where-Object { $_ })

foreach ($item in @($raw.Mail) + @($raw.Calendar)) {
    if (!$item) { continue }

    if ([string]::IsNullOrWhiteSpace([string]$item.ActivityId)) {
        $warnings += "Skipped evidence without ActivityId"
        continue
    }

    $category = if ($item.SourceType -eq "OutlookCalendar") {
        "Meeting"
    }
    else {
        "NeedsReview"
    }

    $items += [pscustomobject]@{
        ActivityId = [string]$item.ActivityId
        SourceType = [string]$item.SourceType
        Category = $category
        Title = [string]$item.Title
        Summary = [string]$item.Summary
        OccurredAt = [string]$item.OccurredAt
        SourceUrl = $item.SourceUrl
        EvidenceId = [string]$item.EvidenceId
        Confidence = "Low"
        RequiresUserConfirmation = $true
        SourceMetadata = $item.SourceMetadata
    }
}

$unique = @(
    $items |
        Group-Object ActivityId |
        ForEach-Object { $_.Group | Select-Object -First 1 }
)

$result = [ordered]@{
    SchemaVersion = "1.0"
    GeneratedAt = (Get-Date).ToString("o")
    Source = "OutlookCOM"
    SourceStatus = $raw.Status
    ReportingWindow = $raw.ReportingWindow
    TotalInput = @($items).Count
    TotalUnique = @($unique).Count
    DuplicatesRemoved = @($items).Count - @($unique).Count
    RequiresHumanApproval = $true
    Activities = $unique
    Warnings = @($warnings)
}

New-Item -ItemType Directory -Force -Path $OutputDirectory |
    Out-Null

$file = Join-Path $OutputDirectory (
    "normalized-" + (Get-Date -Format "yyyyMMdd-HHmmss") + ".json"
)

$result |
    ConvertTo-Json -Depth 15 |
    Set-Content $file -Encoding UTF8

Write-Host "`n=== NORMALIZATION RESULT ===" -ForegroundColor Cyan
Write-Host "Input records: $($result.TotalInput)"
Write-Host "Unique records: $($result.TotalUnique)"
Write-Host "Duplicates removed: $($result.DuplicatesRemoved)"
Write-Host "Warnings: $(@($result.Warnings).Count)"
Write-Host "Human approval required: $($result.RequiresHumanApproval)"
Write-Host "Saved: $file" -ForegroundColor Green
