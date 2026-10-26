$ErrorActionPreference = "Stop"

Import-Module Microsoft.Graph.Authentication

try {
    Connect-MgGraph `
        -Scopes "User.Read","Mail.Send" `
        -ContextScope CurrentUser `
        -NoWelcome `
        -ErrorAction Stop

    $ctx = Get-MgContext

    if (!$ctx -or $ctx.Scopes -notcontains "Mail.Send") {
        throw "Mail.Send permission unavailable."
    }

    $me = Invoke-MgGraphRequest `
        -Method GET `
        -Uri 'https://graph.microsoft.com/v1.0/me?$select=userPrincipalName'

    Write-Host "ACCOUNT: $($me.userPrincipalName)"
    Write-Host "RESULT: GRAPH_AUTH_PASS"
}
catch {
    Write-Host "RESULT: GRAPH_AUTH_FAILED"
    Write-Host $_.Exception.Message
    exit 1
}
