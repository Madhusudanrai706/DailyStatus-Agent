param(
    [switch]$SkipMerge
)

$ErrorActionPreference = "Stop"

$root = Split-Path $PSScriptRoot -Parent
Set-Location $root

$logDirectory = Join-Path $root "data\sync-logs"
New-Item -ItemType Directory -Path $logDirectory -Force | Out-Null

$logFile = Join-Path $logDirectory (
    "sync-" + (Get-Date -Format "yyyyMMdd") + ".log"
)

$mutex = [System.Threading.Mutex]::new(
    $false,
    "Local\BrickredDailyStatusEvidenceSync"
)

$acquired = $false

function Write-SyncLog {
    param([string]$Message)

    $line = "$(Get-Date -Format 'yyyy-MM-dd HH:mm:ss') | $Message"

    Add-Content -Path $logFile -Value $line
    Write-Host $line
}

try {
    $acquired = $mutex.WaitOne(0)

    if (-not $acquired) {
        Write-SyncLog "SKIPPED: Previous sync still running."
        return
    }

    Write-SyncLog "START: Evidence synchronization."

    $today = (Get-Date).Date
    $tomorrow = $today.AddDays(1)

    & "$PSScriptRoot\Collect-OutlookComEvidence.ps1" `
        -WindowStart $today `
        -WindowEnd $tomorrow `
        -MaxItems 500

    if (-not $?) {
        throw "Outlook collection failed."
    }

    Write-SyncLog "Outlook evidence collected."

    # Refresh local Outlook calendar evidence on every sync.
    $calendarDirectory = Join-Path $root "data\calendar-evidence"
    New-Item -ItemType Directory -Path $calendarDirectory -Force |
        Out-Null

    $calendarFile = Join-Path $calendarDirectory (
        "calendar-" + (Get-Date -Format "yyyyMMdd-HHmmss") + ".json"
    )

    $calendarJson = & "$PSScriptRoot\Collect-OutlookLocalCalendar.ps1" `
        -WindowStart $today `
        -WindowEnd $tomorrow `
        -MaxItems 100

    if (-not $?) {
        throw "Calendar collection failed."
    }

    $calendarText = ($calendarJson | Out-String).Trim()

    $calendarData = ConvertFrom-Json -InputObject $calendarText `
        -ErrorAction Stop

    if ($null -eq $calendarData.Calendar) {
        throw "Calendar collector returned unexpected JSON."
    }

    [IO.File]::WriteAllText(
        $calendarFile,
        $calendarText,
        (New-Object System.Text.UTF8Encoding($false))
    )

    Write-SyncLog "Calendar evidence collected: $(@($calendarData.Calendar).Count) events."

    & "$PSScriptRoot\Normalize-StatusEvidence.ps1"

    if (-not $?) {
        throw "Evidence normalization failed."
    }

    Write-SyncLog "Evidence normalized."

    if (-not $SkipMerge) {
        & "$PSScriptRoot\Merge-StatusEvidence.ps1"

        if (-not $?) {
            throw "Evidence merge failed."
        }

        Write-SyncLog "Combined evidence updated."
    }

    Write-SyncLog "SUCCESS: Synchronization completed."
}
catch {
    Write-SyncLog "FAILED: $($_.Exception.Message)"
    throw
}
finally {
    if ($acquired) {
        $mutex.ReleaseMutex()
    }

    $mutex.Dispose()
}