param(
    [datetime]$WindowStart = (Get-Date).Date,
    [datetime]$WindowEnd = (Get-Date).Date.AddDays(1),
    [int]$MaxItems = 100
)

$ErrorActionPreference = "Stop"

if ($WindowStart -ge $WindowEnd) {
    throw "Invalid reporting window."
}

if ($MaxItems -lt 1 -or $MaxItems -gt 500) {
    throw "MaxItems must be between 1 and 500."
}

$outlook = New-Object -ComObject Outlook.Application
$namespace = $outlook.GetNamespace("MAPI")
$calendar = $namespace.GetDefaultFolder(9)

$items = $calendar.Items
$items.Sort("[Start]")
$items.IncludeRecurrences = $true

$culture = [System.Globalization.CultureInfo]::GetCultureInfo("en-US")

$startText = $WindowStart.ToString("MM/dd/yyyy hh:mm tt", $culture)
$endText = $WindowEnd.ToString("MM/dd/yyyy hh:mm tt", $culture)

$filter = "[Start] < '$endText' AND [End] > '$startText'"
$filtered = $items.Restrict($filter)

$events = [System.Collections.Generic.List[object]]::new()

foreach ($item in $filtered) {
    if ($events.Count -ge $MaxItems) {
        break
    }

    try {
        if ($item.MessageClass -notlike "IPM.Appointment*") {
            continue
        }

        $events.Add([pscustomobject]@{
            SourceType = "OutlookCalendar"
            Category = "Meeting"
            Title = [string]$item.Subject
            OccurredAt = ([datetime]$item.Start).ToString("o")
            EndAt = ([datetime]$item.End).ToString("o")
            EvidenceId = "CAL-LOCAL-$($item.EntryID)"
            Confidence = "ScheduledOnly"
            RequiresUserConfirmation = $true
        })
    }
    catch {
        continue
    }
}

[pscustomobject]@{
    ReportingWindow = @{
        Start = $WindowStart.ToString("o")
        End = $WindowEnd.ToString("o")
    }
    SourceCounts = @{
        Calendar = $events.Count
    }
    Calendar = @($events.ToArray())
    Status = "ReadOnlyEvidence"
} | ConvertTo-Json -Depth 8
