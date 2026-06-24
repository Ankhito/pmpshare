param(
    [Parameter(Mandatory = $true)]
    [string]$BaseUrl,

    [Parameter(Mandatory = $true)]
    [string]$TesterKey
)

$ErrorActionPreference = "Stop"

function Join-Url {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Base,

        [Parameter(Mandatory = $true)]
        [string]$Path
    )

    return $Base.TrimEnd("/") + "/" + $Path.TrimStart("/")
}

function Assert-Equal {
    param(
        [Parameter(Mandatory = $true)]
        $Actual,

        [Parameter(Mandatory = $true)]
        $Expected,

        [Parameter(Mandatory = $true)]
        [string]$Message
    )

    if ($Actual -ne $Expected) {
        throw "$Message Expected '$Expected', got '$Actual'."
    }
}

function Invoke-ExpectHttpError {
    param(
        [Parameter(Mandatory = $true)]
        [scriptblock]$Request,

        [Parameter(Mandatory = $true)]
        [int]$StatusCode,

        [Parameter(Mandatory = $true)]
        [string]$Name
    )

    try {
        & $Request | Out-Null
    }
    catch {
        $response = $_.Exception.Response
        if ($null -eq $response) {
            throw "${Name}: expected HTTP $StatusCode, but got non-HTTP error: $($_.Exception.Message)"
        }

        $actualStatusCode = [int]$response.StatusCode
        if ($actualStatusCode -ne $StatusCode) {
            throw "${Name}: expected HTTP $StatusCode, got HTTP $actualStatusCode."
        }

        Write-Host "[pass] $Name rejected with HTTP $StatusCode"
        return
    }

    throw "${Name}: expected HTTP $StatusCode, but request succeeded."
}

$BaseUrl = $BaseUrl.TrimEnd("/")
$headers = @{ "X-PmpShare-Key" = $TesterKey }
$workDir = Join-Path ([System.IO.Path]::GetTempPath()) ("pmpshare-api-test-" + [Guid]::NewGuid().ToString("N"))
New-Item -ItemType Directory -Path $workDir | Out-Null

try {
    Write-Host "[test] GET /health"
    $health = Invoke-RestMethod -Method Get -Uri (Join-Url $BaseUrl "/health")
    Assert-Equal $health.ok $true "Health ok mismatch."
    Assert-Equal $health.service "pmpshare-api" "Health service mismatch."
    Write-Host "[pass] health"

    $blobPath = Join-Path $workDir "encrypted.bin"
    [System.IO.File]::WriteAllBytes($blobPath, [System.Text.Encoding]::UTF8.GetBytes("encrypted-client-payload"))
    $blobSize = (Get-Item $blobPath).Length

    Write-Host "[test] POST /v1/transfers"
    $createBody = @{
        fileName = "example.pmp"
        plaintextSha256 = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa"
        encryptedSize = $blobSize
        expiresInSeconds = 10800
    } | ConvertTo-Json

    $transfer = Invoke-RestMethod `
        -Method Post `
        -Uri (Join-Url $BaseUrl "/v1/transfers") `
        -Headers $headers `
        -ContentType "application/json" `
        -Body $createBody

    if ([string]::IsNullOrWhiteSpace($transfer.transferId)) {
        throw "Create transfer did not return transferId."
    }
    Write-Host "[pass] created transfer $($transfer.transferId)"

    Write-Host "[test] PUT /v1/transfers/{id}/blob"
    $uploaded = Invoke-RestMethod `
        -Method Put `
        -Uri (Join-Url $BaseUrl $transfer.uploadUrl) `
        -Headers $headers `
        -ContentType "application/octet-stream" `
        -InFile $blobPath
    Assert-Equal $uploaded.status "uploaded" "Uploaded status mismatch."
    Write-Host "[pass] uploaded blob"

    Write-Host "[test] GET /v1/transfers/{id}/metadata"
    $metadata = Invoke-RestMethod -Method Get -Uri (Join-Url $BaseUrl $transfer.metadataUrl)
    Assert-Equal $metadata.status "uploaded" "Metadata status mismatch."
    Assert-Equal $metadata.fileName "example.pmp" "Metadata fileName mismatch."
    Write-Host "[pass] metadata"

    Write-Host "[test] GET /v1/transfers/{id}/blob"
    $downloadPath = Join-Path $workDir "downloaded.bin"
    Invoke-WebRequest `
        -Method Get `
        -Uri (Join-Url $BaseUrl "/v1/transfers/$($transfer.transferId)/blob") `
        -OutFile $downloadPath | Out-Null
    Assert-Equal (Get-FileHash $downloadPath -Algorithm SHA256).Hash (Get-FileHash $blobPath -Algorithm SHA256).Hash "Downloaded blob hash mismatch."
    Write-Host "[pass] downloaded blob"

    Write-Host "[test] POST /v1/transfers/{id}/complete"
    $completed = Invoke-RestMethod `
        -Method Post `
        -Uri (Join-Url $BaseUrl "/v1/transfers/$($transfer.transferId)/complete") `
        -Headers $headers
    Assert-Equal $completed.status "completed" "Completed status mismatch."
    Write-Host "[pass] completed transfer"

    Invoke-ExpectHttpError `
        -StatusCode 410 `
        -Name "blob after complete" `
        -Request {
            Invoke-WebRequest `
                -Method Get `
                -Uri (Join-Url $BaseUrl "/v1/transfers/$($transfer.transferId)/blob") `
                -OutFile (Join-Path $workDir "after-complete.bin")
        }

    Invoke-ExpectHttpError `
        -StatusCode 400 `
        -Name "non-.pmp filename" `
        -Request {
            $badBody = @{
                fileName = "example.txt"
                plaintextSha256 = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa"
                encryptedSize = 1
            } | ConvertTo-Json

            Invoke-RestMethod `
                -Method Post `
                -Uri (Join-Url $BaseUrl "/v1/transfers") `
                -Headers $headers `
                -ContentType "application/json" `
                -Body $badBody
        }

    Invoke-ExpectHttpError `
        -StatusCode 401 `
        -Name "missing tester key" `
        -Request {
            Invoke-RestMethod `
                -Method Post `
                -Uri (Join-Url $BaseUrl "/v1/transfers") `
                -ContentType "application/json" `
                -Body $createBody
        }

    Invoke-ExpectHttpError `
        -StatusCode 400 `
        -Name "oversized upload metadata" `
        -Request {
            $oversizedBody = @{
                fileName = "too-large.pmp"
                plaintextSha256 = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa"
                encryptedSize = 524288001
            } | ConvertTo-Json

            Invoke-RestMethod `
                -Method Post `
                -Uri (Join-Url $BaseUrl "/v1/transfers") `
                -Headers $headers `
                -ContentType "application/json" `
                -Body $oversizedBody
        }

    Write-Host "[test] POST /v1/send-requests"
    $senderId = "ps_sender123456789"
    $recipientId = "ps_recipient123456789"
    $sendRequestBody = @{
        senderId = $senderId
        recipientId = $recipientId
        senderDisplayName = "Local Sender"
        transferId = $transfer.transferId
        senderPublicKey = [Convert]::ToBase64String([byte[]](1..32))
        encryptedPassphrase = [Convert]::ToBase64String([byte[]](1..32))
        encryptedPassphraseNonce = [Convert]::ToBase64String([byte[]](1..12))
        message = "Encrypted passphrase envelope test"
        expiresInSeconds = 10800
    } | ConvertTo-Json

    $sendRequest = Invoke-RestMethod `
        -Method Post `
        -Uri (Join-Url $BaseUrl "/v1/send-requests") `
        -Headers $headers `
        -ContentType "application/json" `
        -Body $sendRequestBody
    Assert-Equal $sendRequest.status "pending" "Send request status mismatch."
    Assert-Equal $sendRequest.senderPublicKey ([Convert]::ToBase64String([byte[]](1..32))) "Send request senderPublicKey mismatch."
    Assert-Equal $sendRequest.encryptedPassphraseNonce ([Convert]::ToBase64String([byte[]](1..12))) "Send request nonce mismatch."
    Write-Host "[pass] created send request"

    Write-Host "[test] GET /v1/inbox"
    $inbox = Invoke-RestMethod `
        -Method Get `
        -Uri (Join-Url $BaseUrl "/v1/inbox?recipientId=$recipientId") `
        -Headers $headers
    if ($inbox.requests.Count -lt 1) {
        throw "Inbox did not include created send request."
    }
    Write-Host "[pass] inbox listed request"

    Write-Host "[test] POST /v1/send-requests/{id}/accept"
    $accepted = Invoke-RestMethod `
        -Method Post `
        -Uri (Join-Url $BaseUrl "/v1/send-requests/$($sendRequest.requestId)/accept") `
        -Headers $headers
    Assert-Equal $accepted.status "accepted" "Accepted send request status mismatch."
    Write-Host "[pass] accepted send request"

    Write-Host "[test] POST /v1/send-requests/{id}/accept is retryable"
    $acceptedAgain = Invoke-RestMethod `
        -Method Post `
        -Uri (Join-Url $BaseUrl "/v1/send-requests/$($sendRequest.requestId)/accept") `
        -Headers $headers
    Assert-Equal $acceptedAgain.status "accepted" "Retry accept send request status mismatch."
    Write-Host "[pass] accepted send request retry"

    Write-Host "[test] POST /v1/send-requests/{id}/complete"
    $completedSendRequest = Invoke-RestMethod `
        -Method Post `
        -Uri (Join-Url $BaseUrl "/v1/send-requests/$($sendRequest.requestId)/complete") `
        -Headers $headers
    Assert-Equal $completedSendRequest.status "completed" "Completed send request status mismatch."
    Write-Host "[pass] completed send request"

    Write-Host "[test] completed send request is removed from inbox"
    $inboxAfterComplete = Invoke-RestMethod `
        -Method Get `
        -Uri (Join-Url $BaseUrl "/v1/inbox?recipientId=$recipientId") `
        -Headers $headers
    if (($inboxAfterComplete.requests | Where-Object { $_.requestId -eq $sendRequest.requestId }).Count -ne 0) {
        throw "Completed send request was still listed in inbox."
    }
    Write-Host "[pass] completed send request removed from inbox"

    Write-Host "[test] declined send request is hidden from inbox"
    $declineRequest = Invoke-RestMethod `
        -Method Post `
        -Uri (Join-Url $BaseUrl "/v1/send-requests") `
        -Headers $headers `
        -ContentType "application/json" `
        -Body $sendRequestBody
    $declinedRequest = Invoke-RestMethod `
        -Method Post `
        -Uri (Join-Url $BaseUrl "/v1/send-requests/$($declineRequest.requestId)/decline") `
        -Headers $headers
    Assert-Equal $declinedRequest.status "declined" "Declined send request status mismatch."
    $inboxAfterDecline = Invoke-RestMethod `
        -Method Get `
        -Uri (Join-Url $BaseUrl "/v1/inbox?recipientId=$recipientId") `
        -Headers $headers
    if (($inboxAfterDecline.requests | Where-Object { $_.requestId -eq $declineRequest.requestId }).Count -ne 0) {
        throw "Declined send request was still listed in inbox."
    }
    Write-Host "[pass] declined send request hidden from inbox"

    Invoke-ExpectHttpError `
        -StatusCode 401 `
        -Name "send request missing tester key" `
        -Request {
            Invoke-RestMethod `
                -Method Post `
                -Uri (Join-Url $BaseUrl "/v1/send-requests") `
                -ContentType "application/json" `
                -Body $sendRequestBody
        }

    Write-Host "[pass] all Worker API tests passed"
}
finally {
    Remove-Item -LiteralPath $workDir -Recurse -Force -ErrorAction SilentlyContinue
}
