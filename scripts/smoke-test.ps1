# Signs in and crawls every page reachable from the home page, reporting any response that isn't HTTP 200
# or that contains a server error marker. Usage: ./scripts/smoke-test.ps1 [-BaseUrl http://localhost:5048] [-User admin] [-Max 1500]
param(
    [string]$BaseUrl = "http://localhost:5048",
    [string]$User = "admin",
    [string]$Password = "Vellum!Demo2026",
    [int]$Max = 1500
)
$ErrorActionPreference = "Stop"
$session = New-Object Microsoft.PowerShell.Commands.WebRequestSession

function Get-Token([string]$html) {
    if ($html -match 'name="__RequestVerificationToken"[^>]*value="([^"]+)"') { return $Matches[1] }
    throw "No antiforgery token found"
}

# Identifier-first sign-in
$page = Invoke-WebRequest "$BaseUrl/login?u=$User" -WebSession $session -UseBasicParsing
$token = Get-Token $page.Content
$null = Invoke-WebRequest "$BaseUrl/login?handler=Password" -Method Post -WebSession $session -UseBasicParsing -MaximumRedirection 5 `
    -Body @{ Username = $User; Password = $Password; __RequestVerificationToken = $token }

$queue = New-Object System.Collections.Queue
$seen = @{}
$queue.Enqueue("/")
$seen["/"] = $true
$failures = @()
$count = 0
$skip = '^/(files/|logout|api/|lib/|css/|js/|img/)|\.csv|\.json|handler=|mode=override|export'

while ($queue.Count -gt 0 -and $count -lt $Max) {
    $path = $queue.Dequeue()
    $count++
    try {
        $r = Invoke-WebRequest ($BaseUrl + $path) -WebSession $session -UseBasicParsing -MaximumRedirection 0 -ErrorAction Stop
        $html = $r.Content
        if ($html -match 'Something went wrong|An unhandled exception|Error 500') { $failures += "ERRORPAGE $path" }
        foreach ($m in [regex]::Matches($html, 'href="(/[^"#]*)"')) {
            $href = [System.Net.WebUtility]::HtmlDecode($m.Groups[1].Value)
            if ($href -match $skip) { continue }
            # Collapse paging/sorting variants so the crawl stays bounded.
            $key = ($href -replace '([?&])(p|sort|desc|month|days)=[^&]*', '$1').TrimEnd('?', '&')
            if (-not $seen.ContainsKey($key)) { $seen[$key] = $true; $queue.Enqueue($href) }
        }
    }
    catch {
        $status = $_.Exception.Response.StatusCode.value__
        if ($status -ne 302) { $failures += "HTTP $status $path" }
    }
}

Write-Host "Crawled $count pages as $User."
if ($failures.Count -eq 0) { Write-Host "All pages OK." -ForegroundColor Green }
else { Write-Host "$($failures.Count) problem(s):" -ForegroundColor Red; $failures | ForEach-Object { Write-Host "  $_" } }
