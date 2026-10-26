# Used by the setup wizard: checks an iLoveAPI project public key.
# Exit codes: 0 = accepted, 2 = rejected by iLoveAPI, 3 = could not verify (network/server problem)
param([Parameter(Mandatory = $true)][string]$KeyFile)
try {
    $key = (Get-Content -Raw -LiteralPath $KeyFile).Trim()
    [Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12
    Invoke-RestMethod -Method Post -Uri 'https://api.ilovepdf.com/v1/auth' -Body @{ public_key = $key } -TimeoutSec 20 | Out-Null
    exit 0
}
catch {
    $resp = $_.Exception.Response
    if ($null -ne $resp -and [int]$resp.StatusCode -lt 500) { exit 2 }
    exit 3
}
