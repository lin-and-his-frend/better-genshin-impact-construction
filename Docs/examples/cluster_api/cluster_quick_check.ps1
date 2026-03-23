param(
    [string]$Host = "http://127.0.0.1:50000",
    [string]$Token = $env:BGI_CLUSTER_TOKEN
)

if ([string]::IsNullOrWhiteSpace($Token)) {
    throw "Missing token. Pass -Token or set env BGI_CLUSTER_TOKEN."
}

$Host = $Host.TrimEnd('/')
$headers = @{
    "X-BGI-Cluster-Token" = $Token
}

function Get-Json([string]$path) {
    Invoke-RestMethod -Method GET -Uri "$Host$path" -Headers $headers -TimeoutSec 15
}

Write-Host "== health ==" -ForegroundColor Cyan
Get-Json "/api/cluster/health" | ConvertTo-Json -Depth 8

Write-Host "== meta ==" -ForegroundColor Cyan
Get-Json "/api/cluster/meta" | ConvertTo-Json -Depth 8

Write-Host "== routes ==" -ForegroundColor Cyan
Get-Json "/api/cluster/routes" | ConvertTo-Json -Depth 8

Write-Host "== openapi summary ==" -ForegroundColor Cyan
$openapi = Get-Json "/api/cluster/openapi.json"
[pscustomobject]@{
    Title = $openapi.info.title
    Version = $openapi.info.version
    PathCount = ($openapi.paths.PSObject.Properties | Measure-Object).Count
} | ConvertTo-Json -Depth 4

Write-Host "== notification settings ==" -ForegroundColor Cyan
Get-Json "/api/cluster/settings/notification" | ConvertTo-Json -Depth 8
