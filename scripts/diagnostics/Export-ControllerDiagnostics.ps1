param(
    [string]$BaseUrl = "http://localhost:5094",
    [string]$OutputDirectory = "",
    [int]$DecisionLogCount = 500,
    [int]$HistoryHours = 24,
    [int]$HistoryLogCount = 5000,
    [int]$MonitorMinutes = 0,
    [int]$PollSeconds = 10,
    [switch]$IncludeForecast,
    [switch]$Compress
)

$ErrorActionPreference = "Stop"

function Join-Url {
    param(
        [string]$Root,
        [string]$Path
    )

    return $Root.TrimEnd("/") + "/" + $Path.TrimStart("/")
}

function Save-Json {
    param(
        [string]$Name,
        [object]$Value
    )

    $path = Join-Path $script:OutputRoot $Name
    $Value | ConvertTo-Json -Depth 100 | Set-Content -LiteralPath $path -Encoding UTF8
    Write-Host "Saved $Name"
}

function Invoke-Json {
    param(
        [string]$Path,
        [hashtable]$Query = @{}
    )

    $builder = [System.UriBuilder](Join-Url $BaseUrl $Path)
    if ($Query.Count -gt 0) {
        $pairs = foreach ($key in $Query.Keys) {
            [System.Uri]::EscapeDataString($key) + "=" + [System.Uri]::EscapeDataString([string]$Query[$key])
        }
        $builder.Query = ($pairs -join "&")
    }

    return Invoke-RestMethod -Uri $builder.Uri.AbsoluteUri -TimeoutSec 30
}

function Try-SaveEndpoint {
    param(
        [string]$Name,
        [string]$Path,
        [hashtable]$Query = @{}
    )

    try {
        $response = Invoke-Json -Path $Path -Query $Query
        Save-Json -Name $Name -Value $response
    }
    catch {
        Save-Json -Name ("error-" + $Name) -Value ([pscustomobject]@{
            endpoint = $Path
            query = $Query
            error = $_.Exception.Message
            capturedAt = [DateTimeOffset]::Now.ToString("o")
        })
    }
}

if ([string]::IsNullOrWhiteSpace($OutputDirectory)) {
    $timestamp = Get-Date -Format "yyyyMMdd-HHmmss"
    $OutputDirectory = Join-Path (Get-Location) ("artifacts\diagnostics\controller-diagnostics-" + $timestamp)
}

$script:OutputRoot = $OutputDirectory
New-Item -ItemType Directory -Force -Path $script:OutputRoot | Out-Null

$metadata = [pscustomobject]@{
    capturedAt = [DateTimeOffset]::Now.ToString("o")
    baseUrl = $BaseUrl
    decisionLogCount = $DecisionLogCount
    historyHours = $HistoryHours
    historyLogCount = $HistoryLogCount
    monitorMinutes = $MonitorMinutes
    pollSeconds = $PollSeconds
    machine = $env:COMPUTERNAME
    user = $env:USERNAME
}
Save-Json -Name "metadata.json" -Value $metadata

Try-SaveEndpoint -Name "health.json" -Path "/health"
Try-SaveEndpoint -Name "status.json" -Path "/api/status"
Try-SaveEndpoint -Name "settings.json" -Path "/api/settings"
Try-SaveEndpoint -Name "decision-current.json" -Path "/api/decision/current"
Try-SaveEndpoint -Name "decision-logs.json" -Path "/api/decision/logs" -Query @{ maxCount = $DecisionLogCount }
Try-SaveEndpoint -Name "decision-history.json" -Path "/api/decision/history" -Query @{
    hours = $HistoryHours
    maxCount = $HistoryLogCount
}

$today = Get-Date -Format "yyyy-MM-dd"
Try-SaveEndpoint -Name "savings-day.json" -Path "/api/savings" -Query @{
    period = "day"
    referenceDate = $today
    currency = "EUR"
}
Try-SaveEndpoint -Name "savings-total.json" -Path "/api/savings" -Query @{
    period = "total"
    currency = "EUR"
}

if ($IncludeForecast) {
    $startsAtUtc = [DateTimeOffset]::UtcNow
    $startsAtUtc = $startsAtUtc.AddMinutes(-$startsAtUtc.Minute).AddSeconds(-$startsAtUtc.Second).AddMilliseconds(-$startsAtUtc.Millisecond)
    Try-SaveEndpoint -Name "forecast-24h.json" -Path "/api/forecast" -Query @{
        startsAtUtc = $startsAtUtc.ToString("o")
        hours = 24
    }
}

if ($MonitorMinutes -gt 0) {
    $monitorPath = Join-Path $script:OutputRoot "decision-monitor.ndjson"
    $seen = @{}
    $endsAt = (Get-Date).AddMinutes($MonitorMinutes)

    Write-Host "Monitoring decisions for $MonitorMinutes minute(s)..."
    while ((Get-Date) -lt $endsAt) {
        try {
            $logs = Invoke-Json -Path "/api/decision/logs" -Query @{ maxCount = 20 }
            foreach ($entry in @($logs.value | Sort-Object decidedAtUtc)) {
                if ($null -eq $entry.id -or $seen.ContainsKey($entry.id)) {
                    continue
                }

                $seen[$entry.id] = $true
                $line = [pscustomobject]@{
                    capturedAt = [DateTimeOffset]::Now.ToString("o")
                    decision = $entry
                } | ConvertTo-Json -Depth 100 -Compress
                Add-Content -LiteralPath $monitorPath -Value $line -Encoding UTF8
            }
        }
        catch {
            $line = [pscustomobject]@{
                capturedAt = [DateTimeOffset]::Now.ToString("o")
                error = $_.Exception.Message
            } | ConvertTo-Json -Depth 20 -Compress
            Add-Content -LiteralPath $monitorPath -Value $line -Encoding UTF8
        }

        Start-Sleep -Seconds ([Math]::Max(1, $PollSeconds))
    }
}

if ($Compress) {
    $archivePath = $script:OutputRoot.TrimEnd("\", "/") + ".zip"
    if (Test-Path -LiteralPath $archivePath) {
        Remove-Item -LiteralPath $archivePath -Force
    }

    Compress-Archive -LiteralPath (Join-Path $script:OutputRoot "*") -DestinationPath $archivePath
    Write-Host "Archive: $archivePath"
}

Write-Host "Diagnostics: $script:OutputRoot"
