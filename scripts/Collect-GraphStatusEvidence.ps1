param(
    [datetimeoffset]$WindowStart = [datetimeoffset]::Parse("2026-10-22T00:00:00+05:30"),
    [datetimeoffset]$WindowEnd = [datetimeoffset]::Parse("2026-10-23T23:59:59+05:30"),
    [int]$MaxItems = 25,
    [switch]$SkipMail,
    [switch]$SkipCalendar
)

$ErrorActionPreference = "Stop"

if ($WindowStart -ge $WindowEnd) {
    throw "Invalid reporting window."
}

if ($MaxItems -lt 1 -or $MaxItems -gt 100) {
    throw "MaxItems must be between 1 and 100."
}

Import-Module Microsoft.Graph.Authentication -ErrorAction Stop

$requiredScopes = @("User.Read")

if (-not $SkipMail) {
    $requiredScopes += "Mail.Read"
}

if (-not $SkipCalendar) {
    $requiredScopes += "Calendars.Read"
}

$context = Get-MgContext

$missing = @(
    $requiredScopes | Where-Object {
        $scope = $_
        -not $context -or
        -not @($context.Scopes | Where-Object {
            $_ -ieq $scope
        }).Count
    }
)

if ($missing.Count -gt 0) {
    Connect-MgGraph -Scopes $requiredScopes -NoWelcome
}

$context = Get-MgContext

foreach ($scope in $requiredScopes) {
    if (-not @($context.Scopes | Where-Object {
        $_ -ieq $scope
    }).Count) {
        throw "Required delegated permission missing: $scope"
    }
}

$startUtc = $WindowStart.UtcDateTime.ToString(
    "yyyy-MM-ddTHH:mm:ssZ"
)

$endUtc = $WindowEnd.UtcDateTime.ToString(
    "yyyy-MM-ddTHH:mm:ssZ"
)

$warnings = [System.Collections.Generic.List[string]]::new()
$mail = @()
$meetings = @()

if (-not $SkipMail) {
    try {
        $filter = "receivedDateTime ge $startUtc and receivedDateTime le $endUtc"

        $uri = "https://graph.microsoft.com/v1.0/me/messages?" +
            '$select=id,subject,from,receivedDateTime,webLink,isDraft' +
            "&`$filter=$([uri]::EscapeDataString($filter))" +
            "&`$top=$MaxItems"

        $response = Invoke-MgGraphRequest -Method GET -Uri $uri

        $mail = @(
            foreach ($m in @($response.value)) {
                if ($null -eq $m -or $m.isDraft) {
                    continue
                }

                $sender = [string]$m.from.emailAddress.address

                if ($sender -match '(?i)(no.?reply|notification|newsletter)') {
                    continue
                }

                [pscustomobject]@{
                    SourceType = "OutlookMail"
                    Category = "Communication"
                    Title = [string]$m.subject
                    OccurredAt = $m.receivedDateTime
                    SourceUrl = $m.webLink
                    EvidenceId = "MAIL-$($m.id)"
                    Confidence = "Unclassified"
                    RequiresUserConfirmation = $true
                }
            }
        )

        if ($response.'@odata.nextLink') {
            $warnings.Add(
                "Mail results truncated to first page; pagination pending."
            )
        }
    }
    catch {
        $warnings.Add("Mail retrieval failed: $($_.Exception.Message)")
    }
}

if (-not $SkipCalendar) {
    try {
        $uri = "https://graph.microsoft.com/v1.0/me/calendarView?" +
            "startDateTime=$([uri]::EscapeDataString($startUtc))" +
            "&endDateTime=$([uri]::EscapeDataString($endUtc))" +
            '&$select=id,subject,start,end,organizer,webLink,isCancelled' +
            "&`$top=$MaxItems"

        $response = Invoke-MgGraphRequest -Method GET -Uri $uri

        $meetings = @(
            foreach ($m in @($response.value)) {
                if ($null -eq $m -or $m.isCancelled) {
                    continue
                }

                [pscustomobject]@{
                    SourceType = "OutlookCalendar"
                    Category = "Meeting"
                    Title = [string]$m.subject
                    OccurredAt = $m.start.dateTime
                    SourceUrl = $m.webLink
                    EvidenceId = "CAL-$($m.id)"
                    Confidence = "ScheduledOnly"
                    RequiresUserConfirmation = $true
                }
            }
        )

        if ($response.'@odata.nextLink') {
            $warnings.Add(
                "Calendar results truncated to first page; pagination pending."
            )
        }
    }
    catch {
        $warnings.Add("Calendar retrieval failed: $($_.Exception.Message)")
    }
}

$result = [pscustomobject]@{
    ReportingWindow = @{
        Start = $WindowStart.ToString("o")
        End = $WindowEnd.ToString("o")
    }
    SourceCounts = @{
        Mail = $mail.Count
        Calendar = $meetings.Count
    }
    Mail = @($mail)
    Calendar = @($meetings)
    Warnings = @($warnings)
    Status = "ReadOnlyEvidence"
}

$result | ConvertTo-Json -Depth 8