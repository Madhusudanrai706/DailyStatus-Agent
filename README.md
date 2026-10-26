# Brickred Daily Status Agent

AI-assisted daily status reporting application for Brickred Group.

## Features

- Azure DevOps Work Item evidence
- Azure DevOps Pull Request evidence
- Outlook email and calendar evidence
- Evidence merging and deduplication
- AI-generated daily status reports
- Human review and approval dashboard
- Approved email delivery with duplicate-send protection

## Technology

.NET 10, C#, ASP.NET Core, PowerShell, Azure DevOps REST API,
Microsoft Graph, Microsoft AI Foundry and Outlook COM.

## Run Locally

Requires .NET 10 SDK and authorized credentials.

Run these commands:

dotnet restore

dotnet build

dotnet run --project DailyStatus.Api/DailyStatus.Api.csproj --urls http://127.0.0.1:5075

Review dashboard:

http://127.0.0.1:5075/review/index.html

## Security

Never commit PATs, passwords, tokens, private emails or generated evidence.

Human approval is required before publishing reports.

## Status

Local MVP. Production security hardening and complete automatic
delivery verification are pending.
