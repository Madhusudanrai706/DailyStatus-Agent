param(
    [int]$Port = 5086
)

$ErrorActionPreference = "Stop"

$root = Split-Path $PSScriptRoot -Parent
$evidenceDir = Join-Path $root "data\combined-evidence"
$draftDir = Join-Path $root "data\review-drafts"
$htmlPath = Join-Path $PSScriptRoot "index.html"

New-Item -ItemType Directory -Force -Path $draftDir | Out-Null

$listener = New-Object System.Net.HttpListener
$listener.Prefixes.Add("http://127.0.0.1:$Port/")
$listener.Start()

Write-Host "Local Review Dashboard: http://127.0.0.1:$Port/" -ForegroundColor Green
Write-Host "Press Ctrl+C to stop."

function Send-Response {
    param(
        $Context,
        [int]$Status,
        [string]$Content,
        [string]$ContentType = "application/json"
    )

    $bytes = [Text.Encoding]::UTF8.GetBytes($Content)

    $Context.Response.StatusCode = $Status
    $Context.Response.ContentType = "$ContentType; charset=utf-8"
    $Context.Response.Headers.Add("Cache-Control", "no-store")
    $Context.Response.Headers.Add("X-Content-Type-Options", "nosniff")
    $Context.Response.Headers.Add("X-Frame-Options", "DENY")
    $Context.Response.ContentLength64 = $bytes.Length
    $Context.Response.OutputStream.Write($bytes, 0, $bytes.Length)
    $Context.Response.Close()
}

try {
    while ($listener.IsListening) {
        $context = $listener.GetContext()

        try {
            $path = $context.Request.Url.AbsolutePath
            $method = $context.Request.HttpMethod

            if ($method -eq "GET" -and $path -eq "/") {
                Send-Response $context 200 `
                    ([IO.File]::ReadAllText($htmlPath)) `
                    "text/html"
                continue
            }

            if ($method -eq "GET" -and $path -eq "/api/evidence") {
                $latest = Get-ChildItem $evidenceDir -Filter "combined-*.json" |
                    Sort-Object Name -Descending |
                    Select-Object -First 1

                if (!$latest) {
                    Send-Response $context 404 '{"error":"Evidence missing"}'
                    continue
                }

                Send-Response $context 200 `
                    ([IO.File]::ReadAllText($latest.FullName))
                continue
            }

            if ($method -eq "POST" -and $path -eq "/api/draft") {
                if ($context.Request.ContentLength64 -gt 1048576) {
                    Send-Response $context 413 '{"error":"Draft too large"}'
                    continue
                }

                $reader = New-Object IO.StreamReader(
                    $context.Request.InputStream,
                    [Text.Encoding]::UTF8
                )

                try {
                    $body = $reader.ReadToEnd()
                }
                finally {
                    $reader.Dispose()
                }

                if ($body.Length -gt 1048576) {
                    Send-Response $context 413 '{"error":"Draft too large"}'
                    continue
                }

                $draft = $body | ConvertFrom-Json

                if (!$draft.text -or !$draft.includedIds) {
                    Send-Response $context 400 '{"error":"Draft text and included IDs required"}'
                    continue
                }

                $latest = Get-ChildItem $evidenceDir -Filter "combined-*.json" |
                    Sort-Object Name -Descending |
                    Select-Object -First 1

                if (!$latest) {
                    Send-Response $context 404 '{"error":"Evidence missing"}'
                    continue
                }

                $evidence = Get-Content $latest.FullName -Raw |
                    ConvertFrom-Json

                $allowed = @(
                    $evidence.Activities |
                    ForEach-Object { [string]$_.ActivityId }
                )

                $included = @(
                    $draft.includedIds |
                    Where-Object { $_ -in $allowed } |
                    Select-Object -Unique
                )

                $saved = [ordered]@{
                    DraftId = [guid]::NewGuid().ToString()
                    CreatedAt = (Get-Date).ToString("o")
                    Status = "PendingReview"
                    Approved = $false
                    Published = $false
                    EvidenceFile = $latest.Name
                    IncludedActivityIds = $included
                    Text = [string]$draft.text
                    RequiresExplicitApproval = $true
                }

                $file = Join-Path $draftDir (
                    "draft-" + (Get-Date -Format "yyyyMMdd-HHmmss") +
                    "-" + $saved.DraftId + ".json"
                )

                $saved |
                    ConvertTo-Json -Depth 12 |
                    Set-Content $file -Encoding UTF8

                Send-Response $context 200 (
                    @{
                        saved = $true
                        draftId = $saved.DraftId
                        status = $saved.Status
                    } | ConvertTo-Json -Compress
                )
                continue
            }

            Send-Response $context 404 '{"error":"Not found"}'
        }
        catch {
            try {
                Send-Response $context 500 '{"error":"Request failed"}'
            }
            catch {}
        }
    }
}
finally {
    $listener.Stop()
    $listener.Close()
}
