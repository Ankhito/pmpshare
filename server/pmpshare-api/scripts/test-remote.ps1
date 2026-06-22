param(
    [Parameter(Mandatory = $true)]
    [string]$BaseUrl,

    [Parameter(Mandatory = $true)]
    [string]$TesterKey
)

$ErrorActionPreference = "Stop"

. "$PSScriptRoot\test-worker.ps1" -BaseUrl $BaseUrl -TesterKey $TesterKey
