param([string]$OutputFile)

$ErrorActionPreference = "Stop"

try {
    & "$PSScriptRoot\Test-BackgroundGraphAuth.ps1" *>&1 |
        Out-File -FilePath $OutputFile -Encoding UTF8

    if ($LASTEXITCODE -ne 0) {
        exit $LASTEXITCODE
    }
}
catch {
    $_.Exception.Message |
        Out-File -FilePath $OutputFile -Append -Encoding UTF8
    exit 1
}
