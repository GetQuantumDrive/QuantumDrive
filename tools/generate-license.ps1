#requires -Version 5.1
<#
.SYNOPSIS
    QuantumDrive Pro License Key Generator (Ed25519 signed)
.DESCRIPTION
    Generates cryptographically signed QDPRO license keys.
    Keys contain a random 4-byte serial + Ed25519 signature.
    Only this tool can generate valid keys — the app only has the public key.
.PARAMETER Count
    Number of keys to generate. Default: 1
.EXAMPLE
    .\generate-license.ps1
    .\generate-license.ps1 -Count 10
#>
param(
    [int]$Count = 1
)

# Load BouncyCastle from NuGet cache
$bcPath = Join-Path $env:USERPROFILE '.nuget\packages\bouncycastle.cryptography\2.6.2\lib\netstandard2.0\BouncyCastle.Cryptography.dll'
if (-not (Test-Path $bcPath)) {
    Write-Error "BouncyCastle not found at $bcPath. Run 'dotnet restore' on the main project first."
    exit 1
}
Add-Type -Path $bcPath

# Ed25519 private key — loaded from environment variable QDPRO_SIGNING_KEY.
# Set it before running: $env:QDPRO_SIGNING_KEY = "<base64-encoded 32-byte key>"
$privateKeyBase64 = $env:QDPRO_SIGNING_KEY
if ([string]::IsNullOrEmpty($privateKeyBase64)) {
    Write-Error "QDPRO_SIGNING_KEY environment variable is not set."
    exit 1
}
$privateKeyBytes = [Convert]::FromBase64String($privateKeyBase64)
$privateKey = New-Object Org.BouncyCastle.Crypto.Parameters.Ed25519PrivateKeyParameters($privateKeyBytes, 0)

$base32Alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567"

function ConvertTo-Base32 {
    param([byte[]]$Bytes)
    $bits = 0
    $buffer = 0
    $result = New-Object System.Text.StringBuilder

    foreach ($b in $Bytes) {
        $buffer = ($buffer -shl 8) -bor $b
        $bits += 8
        while ($bits -ge 5) {
            $bits -= 5
            $index = ($buffer -shr $bits) -band 0x1F
            [void]$result.Append($base32Alphabet[$index])
        }
    }
    if ($bits -gt 0) {
        $index = ($buffer -shl (5 - $bits)) -band 0x1F
        [void]$result.Append($base32Alphabet[$index])
    }
    return $result.ToString()
}

function New-LicenseKey {
    # Generate 4-byte random serial
    $rng = [System.Security.Cryptography.RandomNumberGenerator]::Create()
    $serial = New-Object byte[] 4
    $rng.GetBytes($serial)

    # Sign the serial with Ed25519
    $signer = New-Object Org.BouncyCastle.Crypto.Signers.Ed25519Signer
    $signer.Init($true, $privateKey)
    $signer.BlockUpdate($serial, 0, $serial.Length)
    $signature = $signer.GenerateSignature()

    # Combine: 4 bytes serial + 64 bytes signature = 68 bytes
    $combined = New-Object byte[] 68
    [Array]::Copy($serial, 0, $combined, 0, 4)
    [Array]::Copy($signature, 0, $combined, 4, 64)

    # Encode as base32 and format with dashes
    $b32 = ConvertTo-Base32 -Bytes $combined
    $groups = @()
    for ($i = 0; $i -lt $b32.Length; $i += 5) {
        $len = [Math]::Min(5, $b32.Length - $i)
        $groups += $b32.Substring($i, $len)
    }
    return "QDPRO-" + ($groups -join "-")
}

Write-Host ""
Write-Host "  QuantumDrive Pro License Generator (Ed25519)" -ForegroundColor Magenta
Write-Host "  ==============================================" -ForegroundColor DarkGray
Write-Host ""

for ($i = 0; $i -lt $Count; $i++) {
    $key = New-LicenseKey
    Write-Host "  $key" -ForegroundColor Cyan
}

Write-Host ""
