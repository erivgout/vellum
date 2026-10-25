# Signs in and crawls every page reachable from the user's landing page, reporting any response that isn't HTTP 200
# or that contains a server error marker. For a supplier portal user (landing on /portal) it also checks that every
# internal URL is refused with 403 and that the crawl never leaves /portal.
# Usage: ./scripts/smoke-test.ps1 [-BaseUrl http://localhost:5048] [-User admin] [-Max 1500]
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

# Start from wherever the user lands (internal users: the home page; supplier portal users: /portal).
$landing = Invoke-WebRequest "$BaseUrl/" -WebSession $session -UseBasicParsing -MaximumRedirection 5
$start = if ($landing.BaseResponse.ResponseUri) { $landing.BaseResponse.ResponseUri.AbsolutePath } else { $landing.BaseResponse.RequestMessage.RequestUri.AbsolutePath }
$portal = $start.StartsWith("/portal")

$queue = New-Object System.Collections.Queue
$seen = @{}
$queue.Enqueue($start)
$seen[$start] = $true
$failures = @()
$count = 0
$refused = 0
$skip = '^/(files/|logout|api/|lib/|css/|js/|img/)|\.csv|\.json|handler=|mode=override|export'

# SEC-804: a supplier portal user must be refused everywhere outside /portal (pages, APIs, search, exports, files).
if ($portal) {
    $probes = "/apps", "/apps/suppliers", "/apps/capa", "/work", "/search?q=a", "/notifications", "/account/profile", "/admin", "/admin/users",
        "/designer", "/insights", "/audits/findings", "/api/lookup/users", "/api/lookup/records?q=a", "/api/search/quick?q=a", "/api/notifications",
        "/files/1", "/apps/suppliers/export.csv", "/forbidden"
    foreach ($p in $probes) {
        try {
            $null = Invoke-WebRequest ($BaseUrl + $p) -WebSession $session -UseBasicParsing -MaximumRedirection 0 -ErrorAction Stop
            $failures += "LEAK $p (expected 403)"
        }
        catch {
            $status = $_.Exception.Response.StatusCode.value__
            if ($status -eq 403) { $refused++ } else { $failures += "HTTP $status $p (expected 403)" }
        }
    }
}

while ($queue.Count -gt 0 -and $count -lt $Max) {
    $path = $queue.Dequeue()
    $count++
    try {
        $r = Invoke-WebRequest ($BaseUrl + $path) -WebSession $session -UseBasicParsing -MaximumRedirection 0 -ErrorAction Stop
        $html = $r.Content
        if ($portal -and -not $path.StartsWith("/portal")) { $failures += "LEAK $path (a portal user reached a page outside /portal)" }
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
        if ($portal -and $status -eq 403 -and -not $path.StartsWith("/portal")) { $refused++ }
        elseif ($status -ne 302) { $failures += "HTTP $status $path" }
    }
}

Write-Host "Crawled $count pages as $User, starting at $start."
if ($portal) { Write-Host "Supplier portal user: $refused internal URL(s) correctly refused with 403." }
if ($failures.Count -eq 0) { Write-Host "All pages OK." -ForegroundColor Green }
else { Write-Host "$($failures.Count) problem(s):" -ForegroundColor Red; $failures | ForEach-Object { Write-Host "  $_" } }
