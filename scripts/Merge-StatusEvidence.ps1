param(
    [string]$OutlookDirectory = ".\data\normalized-evidence",
    [string]$AdoDirectory = ".\data\ado-evidence",
    [string]$OutputDirectory = ".\data\combined-evidence"
)

$ErrorActionPreference = "Stop"

$outlookFile = Get-ChildItem $OutlookDirectory -Filter "normalized-*.json" |
    Sort-Object Name -Descending |
    Select-Object -First 1

$adoFile = Get-ChildItem $AdoDirectory -Filter "ado-classification-*.json" |
    Sort-Object Name -Descending |
    Select-Object -First 1

if (!$outlookFile) { throw "Outlook evidence missing" }
if (!$adoFile) { throw "ADO evidence missing" }

$outlook = Get-Content $outlookFile.FullName -Raw | ConvertFrom-Json
$ado = Get-Content $adoFile.FullName -Raw | ConvertFrom-Json

$activities = [System.Collections.Generic.List[object]]::new()

foreach ($item in @($outlook.Activities)) {
    if (!$item) { continue }

    $activities.Add([pscustomobject]@{
        ActivityId = [string]$item.ActivityId
        SourceType = [string]$item.SourceType
        Category = [string]$item.Category
        Title = [string]$item.Title
        Summary = [string]$item.Summary
        OccurredAt = $item.OccurredAt
        SourceUrl = $item.SourceUrl
        EvidenceId = [string]$item.EvidenceId
        Confidence = "Low"
        RequiresUserConfirmation = $true
        SourceMetadata = $item.SourceMetadata
    })
}

if (!$ado.workItemId -or !$ado.evidenceId) {
    throw "ADO evidence missing required workItemId or evidenceId"
}

$adoId = "ADO-WORKITEM-$($ado.workItemId)"

$activities.Add([pscustomobject]@{
    ActivityId = $adoId
    SourceType = "AzureDevOps"
    Category = [string]$ado.category
    Title = [string]$ado.title
    Summary = [string]$ado.classificationReason
    OccurredAt = $ado.changedAt
    SourceUrl = $ado.sourceUrl
    EvidenceId = [string]$ado.evidenceId
    Confidence = "Medium"
    RequiresUserConfirmation = [bool]$ado.requiresReview
    SourceMetadata = @{
        WorkItemId = $ado.workItemId
        WorkItemType = $ado.workItemType
        State = $ado.state
    }
})

# STEP173_ADDITIONAL_EVIDENCE
$calendarCount = 0
$prCount = 0
$calendarSource = $null
$prSource = "AzureDevOpsPullRequestsAPI"
$additionalWarnings = @()

$calendarFile = Get-ChildItem ".\data\calendar-evidence" `
    -Filter "calendar-*.json" -ErrorAction SilentlyContinue |
    Sort-Object Name -Descending |
    Select-Object -First 1

if ($calendarFile) {
    $calendarSource = $calendarFile.Name
    $calendarData = Get-Content $calendarFile.FullName -Raw |
        ConvertFrom-Json

    foreach ($event in @($calendarData.Calendar)) {
        if (!$event) { continue }

        $calendarCount++

        $activities.Add([pscustomobject]@{
            ActivityId = "CAL-$([string]$event.EvidenceId)"
            SourceType = "OutlookCalendar"
            Category = "Meeting"
            Title = [string]$event.Title
            Summary = "Scheduled calendar event; attendance not verified."
            OccurredAt = $event.OccurredAt
            SourceUrl = $null
            EvidenceId = [string]$event.EvidenceId
            Confidence = "ScheduledOnly"
            RequiresUserConfirmation = $true
            SourceMetadata = @{
                EndAt = $event.EndAt
                AttendanceVerified = $false
            }
        })
    }
}

try {
    $prResponse = Invoke-RestMethod `
        -Uri "http://127.0.0.1:5075/api/additional-evidence/pull-requests" `
        -TimeoutSec 30 -ErrorAction Stop

    foreach ($pr in @($prResponse.evidence.value)) {
        if (!$pr) { continue }

        $prCount++

        $activities.Add([pscustomobject]@{
            ActivityId = "ADO-PR-$($pr.pullRequestId)"
            SourceType = "AzureDevOpsPullRequests"
            Category = "PullRequest"
            Title = [string]$pr.title
            Summary = "PR status: $($pr.status). Activity by current user not verified."
            OccurredAt = $pr.creationDate
            SourceUrl = $pr.repository.webUrl
            EvidenceId = "ADO-PR-$($pr.pullRequestId)"
            Confidence = "NeedsReview"
            RequiresUserConfirmation = $true
            SourceMetadata = @{
                PullRequestId = $pr.pullRequestId
                Status = $pr.status
                Repository = $pr.repository.name
                CreatedBy = $pr.createdBy.displayName
                ClosedDate = $pr.closedDate
            }
        })
    }
}
catch {
    $additionalWarnings += "PR evidence unavailable; report remains pending review."
}
$unique = @(
    $activities |
        Group-Object ActivityId |
        ForEach-Object { $_.Group | Select-Object -First 1 }
)

$categories = @{}

foreach ($item in $unique) {
    $category = [string]$item.Category

    if (!$categories.ContainsKey($category)) {
        $categories[$category] = 0
    }

    $categories[$category]++
}

$result = [ordered]@{
    SchemaVersion = "1.0"
    GeneratedAt = (Get-Date).ToString("o")
    SourceFiles = @{
        Outlook = $outlookFile.Name
        AzureDevOps = $adoFile.Name
        Calendar = $calendarSource
        PullRequests = $prSource
    }
    SourceCounts = @{
        Outlook = @($outlook.Activities).Count
        AzureDevOps = 1
        Calendar = $calendarCount
        PullRequests = $prCount
    }
    TotalInput = $activities.Count
    TotalUnique = $unique.Count
    DuplicatesRemoved = $activities.Count - $unique.Count
    CategoryCounts = $categories
    Status = "PendingReview"
    RequiresHumanApproval = $true
    Approved = $false
    Published = $false
    Activities = $unique
    Warnings = @($outlook.Warnings | Where-Object { $_ }) + @($additionalWarnings)
}

New-Item -ItemType Directory -Force -Path $OutputDirectory |
    Out-Null

$file = Join-Path $OutputDirectory (
    "combined-" + (Get-Date -Format "yyyyMMdd-HHmmss") + ".json"
)

$result |
    ConvertTo-Json -Depth 15 |
    Set-Content $file -Encoding UTF8

Write-Host "`n=== COMBINED EVIDENCE ===" -ForegroundColor Cyan
Write-Host "Outlook activities: $($result.SourceCounts.Outlook)"
Write-Host "ADO activities: $($result.SourceCounts.AzureDevOps)"
Write-Host "Total input: $($result.TotalInput)"
Write-Host "Unique activities: $($result.TotalUnique)"
Write-Host "Duplicates removed: $($result.DuplicatesRemoved)"

Write-Host "`n=== CLASSIFICATIONS ===" -ForegroundColor Cyan

$categories.GetEnumerator() |
    Sort-Object Name |
    ForEach-Object {
        Write-Host "$($_.Key): $($_.Value)"
    }

Write-Host "`nReview required: $($result.RequiresHumanApproval)"
Write-Host "Approved: $($result.Approved)"
Write-Host "Published: $($result.Published)"
Write-Host "Saved: $file" -ForegroundColor Green

