param(
    [string]$Recipient = "madhusudan.rai@brickred.in",
    [switch]$EnableSending
)

$ErrorActionPreference = "Stop"

$root = Split-Path $PSScriptRoot -Parent
$draftDir = Join-Path $root "data\review-drafts"
$deliveryDir = Join-Path $root "data\draft-delivery"
$graphDir = Join-Path $root "data\graph-delivery"
$sender = Join-Path $PSScriptRoot "Send-ApprovedDraft.ps1"

if (!(Test-Path $sender)) {
    throw "Graph sender script missing."
}

$cutoffFile = Join-Path $root "data\auto-dispatch-start-utc.txt"

if (!(Test-Path $cutoffFile)) {
    throw "Activation cutoff missing. Sending blocked."
}

$cutoffUtc = [DateTimeOffset]::Parse(
    (Get-Content $cutoffFile -Raw).Trim()
).ToUniversalTime()
$drafts = @(Get-ChildItem $draftDir -Filter "draft-*.json" -ErrorAction SilentlyContinue)

foreach ($file in $drafts) {
    $draft = Get-Content $file.FullName -Raw | ConvertFrom-Json

    if ($draft.Status -ne "Approved" -or
        $draft.Approved -ne $true -or
        $draft.ReviewConfirmed -ne $true -or
        $draft.Published -ne $false) {
        continue
    }

    if ([string]::IsNullOrWhiteSpace([string]$draft.ReviewedAt)) {
        Write-Host "SKIP $($file.BaseName) : Approval timestamp missing"
        continue
    }

    $approvedAt = [DateTimeOffset]::Parse(
        [string]$draft.ReviewedAt
    ).ToUniversalTime()

    if ($approvedAt -le $cutoffUtc) {
        Write-Host "SKIP $($file.BaseName) : Approved before activation"
        continue
    }
    $content = [string]$draft.Text

    if ([string]::IsNullOrWhiteSpace($content) -or
        $content -match '(?i)test.only|do not deliver|reviewer audit validation') {
        Write-Host "SKIP $($file.BaseName) : Test-only or empty report"
        continue
    }
    $id = [guid]($file.BaseName -replace '^draft-', '')

    $deliveryFile = Join-Path $deliveryDir "draft-$id-delivery.json"

    if (!(Test-Path $deliveryFile)) {
        Write-Host "SKIP $id : Delivery not prepared"
        continue
    }

    $delivery = Get-Content $deliveryFile -Raw | ConvertFrom-Json

    if ($delivery.Status -ne "Prepared" -or
        $delivery.DeliveryStatus -ne "NotSent" -or
        $delivery.EmailSent -ne $false) {
        continue
    }

    $alreadyAttempted = @(
        Get-ChildItem $graphDir -Filter "*.json" -ErrorAction SilentlyContinue |
        ForEach-Object {
            try {
                Get-Content $_.FullName -Raw | ConvertFrom-Json
            }
            catch {
                $null
            }
        } |
        Where-Object { $_.DraftId -eq "$id" }
    ).Count -gt 0

    if ($alreadyAttempted) {
        Write-Host "SKIP $id : Graph send already attempted"
        continue
    }

    if (!$EnableSending) {
        Write-Host "READY $id : Approved, not yet sent (dry run)"
        continue
    }

    Write-Host "SENDING $id to $Recipient"

    & $sender -DraftId $id -Recipient $Recipient -ConfirmSend
}

Write-Host "DISPATCH COMPLETE"


