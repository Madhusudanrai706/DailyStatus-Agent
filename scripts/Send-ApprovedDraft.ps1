param(
    [Parameter(Mandatory=$true)]
    [guid]$DraftId,

    [Parameter(Mandatory=$true)]
    [string]$Recipient,

    [switch]$ConfirmSend
)

$ErrorActionPreference = "Stop"

if (-not $ConfirmSend) {
    throw "Explicit -ConfirmSend required. No email sent."
}

if ($Recipient -notmatch '^[^@\s]+@[^@\s]+\.[^@\s]+$') {
    throw "Invalid recipient."
}

$root = Split-Path $PSScriptRoot -Parent
$draftFile = Join-Path $root "data\review-drafts\draft-$DraftId.json"
$deliveryFile = Join-Path $root "data\draft-delivery\draft-$DraftId-delivery.json"

if (!(Test-Path $draftFile) -or !(Test-Path $deliveryFile)) {
    throw "Approved draft or prepared delivery missing."
}

$draft = Get-Content $draftFile -Raw | ConvertFrom-Json
$delivery = Get-Content $deliveryFile -Raw | ConvertFrom-Json

if ($draft.Status -ne "Approved" -or
    $draft.Approved -ne $true -or
    $draft.ReviewConfirmed -ne $true -or
    $draft.Published -ne $false) {
    throw "Confirmed approved draft required."
}

if ($delivery.Status -ne "Prepared" -or
    $delivery.DeliveryStatus -ne "NotSent" -or
    $delivery.EmailSent -ne $false) {
    throw "Delivery must be Prepared / NotSent."
}

$content = [string]$draft.Text

if ([string]::IsNullOrWhiteSpace($content)) {
    throw "BLOCKED: Empty report content."
}

if ($content -match '(?i)test.only|do not deliver|reviewer audit validation') {
    throw "BLOCKED: Test-only report must not be emailed."
}
if ($Recipient.Trim().ToLowerInvariant() -ne "madhusudan.rai@brickred.in") {
    throw "BLOCKED: Recipient not on approved allowlist."
}
$recipientKey = $Recipient.Trim().ToLowerInvariant()
$historyDir = Join-Path $root "data\graph-delivery"
[void][System.IO.Directory]::CreateDirectory($historyDir)

$sha = [System.Security.Cryptography.SHA256]::Create()
try {
    $hash = [BitConverter]::ToString(
        $sha.ComputeHash(
            [System.Text.Encoding]::UTF8.GetBytes(
                "$DraftId|$recipientKey"
            )
        )
    ).Replace("-", "")
}
finally {
    $sha.Dispose()
}

$mutex = [System.Threading.Mutex]::new(
    $false,
    "Local\BrickredApprovedDraft_$hash"
)

$locked = $false

try {
    $locked = $mutex.WaitOne([TimeSpan]::FromSeconds(15))

    if (!$locked) {
        throw "Another send is in progress."
    }

    $recordFile = Join-Path $historyDir "$hash.json"

    if (Test-Path $recordFile) {
        throw "Send already attempted. Manual reconciliation required."
    }

    # Reserve send before calling Graph to prevent duplicate retries.
    $record = @{
        DraftId = "$DraftId"
        Recipient = $recipientKey
        Status = "Attempting"
        CreatedAtUtc = (Get-Date).ToUniversalTime().ToString("o")
    }

    $record | ConvertTo-Json |
        Set-Content $recordFile -Encoding UTF8

    try {
        if (!(Get-Command Connect-MgGraph -ErrorAction SilentlyContinue)) {
            throw "Microsoft Graph PowerShell module unavailable."
        }

        if (-not (Get-MgContext)) {
            Connect-MgGraph `
                -Scopes "User.Read","Mail.Send" `
                -ContextScope CurrentUser `
                -NoWelcome `
                -ErrorAction Stop
        }

        $ctx = Get-MgContext

        if ($ctx.Scopes -notcontains "Mail.Send") {
            throw "Mail.Send permission missing."
        }

        $encodedText = [System.Net.WebUtility]::HtmlEncode(
            [string]$draft.Text
        )

        $html = "<html><body><h2>Brickred Daily Status Report</h2><pre style='font-family:Arial;white-space:pre-wrap'>$encodedText</pre></body></html>"

        $payload = @{
            message = @{
                subject = "Brickred Daily Status Report"
                body = @{
                    contentType = "HTML"
                    content = $html
                }
                toRecipients = @(
                    @{
                        emailAddress = @{
                            address = $recipientKey
                        }
                    }
                )
            }
            saveToSentItems = $true
        } | ConvertTo-Json -Depth 10

        Invoke-MgGraphRequest `
            -Method POST `
            -Uri "https://graph.microsoft.com/v1.0/me/sendMail" `
            -Body $payload `
            -ContentType "application/json"

        $record.Status = "AcceptedByGraph"
        $record.AcceptedAtUtc = (Get-Date).ToUniversalTime().ToString("o")

        $record | ConvertTo-Json |
            Set-Content $recordFile -Encoding UTF8

        Write-Host "PASS: Microsoft Graph accepted send request" -ForegroundColor Green
        Write-Host "Recipient: $recipientKey"
        Write-Host "Record: $recordFile"
    }
    catch {
        $record.Status = "UnknownOrFailed"
        $record.Error = $_.Exception.Message

        $record | ConvertTo-Json |
            Set-Content $recordFile -Encoding UTF8

        throw
    }
}
finally {
    if ($locked) {
        $mutex.ReleaseMutex()
    }

    $mutex.Dispose()
}



