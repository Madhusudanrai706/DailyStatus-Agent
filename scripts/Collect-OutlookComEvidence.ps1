param(
    [datetime]$WindowStart = (Get-Date).Date.AddDays(-7),
    [datetime]$WindowEnd = (Get-Date),
    [int]$MaxItems = 100
)

$ErrorActionPreference = "Stop"

if ($WindowEnd -le $WindowStart) {
    throw "Invalid reporting window."
}

$MaxItems = [Math]::Max(1, [Math]::Min($MaxItems, 500))

function New-EvidenceId {
    param(
        [string]$Prefix,
        [string]$Key
    )

    $sha = [Security.Cryptography.SHA256]::Create()

    try {
        $bytes = [Text.Encoding]::UTF8.GetBytes($Key)
        $hash = $sha.ComputeHash($bytes)

        $hex = [BitConverter]::ToString($hash).Replace("-", "")

        return "$Prefix-$($hex.Substring(0,16))"
    }
    finally {
        $sha.Dispose()
    }
}

$mail = [System.Collections.Generic.List[object]]::new()
$calendar = [System.Collections.Generic.List[object]]::new()
$warnings = [System.Collections.Generic.List[string]]::new()

$outlook = New-Object -ComObject Outlook.Application
$session = $outlook.GetNamespace("MAPI")

# MAIL — read-only metadata collection
try {
    $inbox = $session.GetDefaultFolder(6)
    $items = $inbox.Items
    $items.Sort("[ReceivedTime]", $true)

    $scanned = 0

    foreach ($item in $items) {
        if (++$scanned -gt 2000 -or $mail.Count -ge $MaxItems) {
            break
        }

        try {
            if ($item.Class -ne 43) { continue }

            $received = [datetime]$item.ReceivedTime

            if ($received -lt $WindowStart) { break }
            if ($received -gt $WindowEnd) { continue }

            $entryId = [string]$item.EntryID

            $id = New-EvidenceId `
                -Prefix "OUTLOOK-MAIL" `
                -Key $entryId

            $mail.Add([pscustomobject]@{
                ActivityId = $id
                SourceType = "OutlookMail"
                Category = "NeedsReview"
                Title = [string]$item.Subject
                Summary = "Mail metadata collected; action and relevance unverified"
                OccurredAt = $received.ToString("o")
                SourceUrl = $null
                EvidenceId = $id
                Confidence = "Low"
                RequiresUserConfirmation = $true
                SourceMetadata = @{
                    SenderName = [string]$item.SenderName
                    Folder = "Inbox"
                    LocalOutlookEntryId = $entryId
                }
            })
        }
        catch {
            $warnings.Add("Mail item error: $($_.Exception.Message)")
        }
    }
}
catch {
    $warnings.Add("Mail source error: $($_.Exception.Message)")
}

# CALENDAR — read-only meeting metadata
try {
    $folder = $session.GetDefaultFolder(9)
    $items = $folder.Items
    $items.Sort("[Start]")
    $items.IncludeRecurrences = $true

    $startText = $WindowStart.ToString("g")
    $endText = $WindowEnd.ToString("g")

    $filter = "[Start] < '$endText' AND [End] > '$startText'"
    $filtered = $items.Restrict($filter)

    $scanned = 0

    foreach ($item in $filtered) {
        if (++$scanned -gt 2000 -or $calendar.Count -ge $MaxItems) {
            break
        }

        try {
            if ($item.Class -ne 26) { continue }

            $start = [datetime]$item.Start
            $end = [datetime]$item.End

            if ($start -ge $WindowEnd -or $end -le $WindowStart) {
                continue
            }

            $entryId = [string]$item.EntryID

            $id = New-EvidenceId `
                -Prefix "OUTLOOK-CAL" `
                -Key "$entryId|$($start.ToString('o'))"

            $calendar.Add([pscustomobject]@{
                ActivityId = $id
                SourceType = "OutlookCalendar"
                Category = "Meeting"
                Title = [string]$item.Subject
                Summary = "Meeting metadata collected; outcomes unverified"
                OccurredAt = $start.ToString("o")
                SourceUrl = $null
                EvidenceId = $id
                Confidence = "Low"
                RequiresUserConfirmation = $true
                SourceMetadata = @{
                    End = $end.ToString("o")
                    Location = [string]$item.Location
                    LocalOutlookEntryId = $entryId
                }
            })
        }
        catch {
            $warnings.Add("Calendar item error: $($_.Exception.Message)")
        }
    }
}
catch {
    $warnings.Add("Calendar source error: $($_.Exception.Message)")
}

$output = [ordered]@{
    ReportingWindow = @{
        Start = $WindowStart.ToString("o")
        End = $WindowEnd.ToString("o")
    }
    SourceCounts = @{
        Mail = $mail.Count
        Calendar = $calendar.Count
    }
    Mail = @($mail.ToArray())
    Calendar = @($calendar.ToArray())
    Warnings = @($warnings.ToArray() | Select-Object -Unique)
    Status = if ($warnings.Count -eq 0) { "Collected" } else { "Partial" }
}

$directory = Join-Path (Get-Location) "data\outlook-evidence"
New-Item -ItemType Directory -Force -Path $directory | Out-Null

$file = Join-Path $directory (
    "outlook-" + (Get-Date -Format "yyyyMMdd-HHmmss") + ".json"
)

$output |
    ConvertTo-Json -Depth 12 |
    Set-Content -Path $file -Encoding UTF8

Write-Host "`n=== OUTLOOK COM COLLECTION ===" -ForegroundColor Cyan
Write-Host "Mail items: $($mail.Count)"
Write-Host "Calendar items: $($calendar.Count)"
Write-Host "Warnings: $($warnings.Count)"
Write-Host "Status: $($output.Status)"
Write-Host "Saved: $file" -ForegroundColor Green

$warnings |
    Select-Object -Unique |
    ForEach-Object {
        Write-Host "WARNING: $_" -ForegroundColor Yellow
    }
