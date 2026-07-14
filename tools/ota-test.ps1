# OTA test bench: drives the B1 master over the serial port (console closed),
# replays the console's JSON protocol (hello/ping/otaStart/otaChunk), and lets
# you inject faults: corrupted chunk, mid-transfer abort, double otaStart.
# See the CLAUDE.md OTA section for the protocol.
param(
    [Parameter(Mandatory=$true)][string]$Bin,
    [int]$Target = 18872,          # B1-Red (0x49B8)
    [int]$CorruptChunk = -1,       # chunk index to corrupt in flight (-1 = none)
    [int]$StopAtChunk = -1,        # abruptly closes the port at this chunk (-1 = never)
    [int]$SecondStartAt = -1,      # sends a 2nd otaStart at this chunk (-1 = never)
    [string]$ComPort = "COM3",
    [string]$LogFile = "$env:TEMP\ota-test.log"   # full TX/RX trace (outside the repo)
)

$ErrorActionPreference = 'Stop'

function Log([string]$msg) {
    Add-Content -Path $LogFile -Value ("{0:HH:mm:ss.fff} {1}" -f (Get-Date), $msg)
}
function Say([string]$msg) { Write-Output ("{0:HH:mm:ss} {1}" -f (Get-Date), $msg); Log ("== " + $msg) }

$image = [System.IO.File]::ReadAllBytes($Bin)
$md5 = ([System.Security.Cryptography.MD5]::Create().ComputeHash($image) | ForEach-Object { $_.ToString("x2") }) -join ''
Say "image $Bin ($($image.Length) B, md5 $md5)"

$port = New-Object System.IO.Ports.SerialPort($ComPort, 115200)
$port.NewLine = "`n"
$port.Encoding = [System.Text.Encoding]::UTF8
$port.ReadTimeout = 300
$port.Open()

function Send([string]$json) {
    Log ("TX " + $json.Substring(0, [Math]::Min(120, $json.Length)))
    $port.WriteLine($json)
}

$chunkSize = 0
$totalChunks = 0
$busySeen = $false
$done = $false
$stopped = $false
$helloOk = $false
$secondStartSent = $false

function SendChunk([int]$idx) {
    $offset = $idx * $script:chunkSize
    $len = [Math]::Min($script:chunkSize, $image.Length - $offset)
    $data = New-Object byte[] $len
    [Array]::Copy($image, $offset, $data, 0, $len)
    if ($idx -eq $CorruptChunk) {
        $data[0] = $data[0] -bxor 0xFF
        Say "chunk $idx CORRUPTED in flight (byte 0 flipped)"
    }
    $b64 = [Convert]::ToBase64String($data)
    Send ('{"cmd":"otaChunk","seq":' + $idx + ',"data":"' + $b64 + '"}')
}

Send '{"cmd":"hello"}'
$deadline = (Get-Date).AddSeconds(720)
$lastPing = Get-Date

while ((Get-Date) -lt $deadline -and -not $done -and -not $stopped) {
    if (((Get-Date) - $lastPing).TotalSeconds -gt 2) { Send '{"cmd":"ping"}'; $lastPing = Get-Date }
    try { $line = $port.ReadLine() } catch { continue }
    if (-not $line) { continue }
    Log ("RX " + $line.Substring(0, [Math]::Min(150, $line.Length)))
    try { $j = $line | ConvertFrom-Json } catch { continue }
    switch ($j.evt) {
        'hello' {
            if (-not $helloOk) {
                $helloOk = $true
                Say "hello ok (master fw $($j.fw))"
                Send ('{"cmd":"otaStart","target":' + $Target + ',"size":' + $image.Length + ',"md5":"' + $md5 + '"}')
            }
        }
        'otaReady' {
            $chunkSize = $j.chunkSize
            $totalChunks = $j.totalChunks
            Say "otaReady: $totalChunks chunks of $chunkSize B (session $($j.sessionId))"
            SendChunk 0
        }
        'otaChunkAck' {
            $sent = [int]$j.sent
            if ($sent % 500 -eq 0) { Say "progress $sent/$totalChunks" }
            if ($SecondStartAt -ge 0 -and $sent -ge $SecondStartAt -and -not $secondStartSent) {
                $secondStartSent = $true
                Say 'expecting otaError "busy": 2nd otaStart injected'
                Send ('{"cmd":"otaStart","target":' + $Target + ',"size":' + $image.Length + ',"md5":"' + $md5 + '"}')
            }
            if ($StopAtChunk -ge 0 -and $sent -ge $StopAtChunk) {
                Say "VOLUNTARY ABORT at chunk $sent (simulating console closed)"
                $stopped = $true
                break
            }
            if ($sent -lt $totalChunks) { SendChunk $sent }
        }
        'otaDone' { Say "otaDone -- slave rebooting, awaiting verdict..." }
        'otaResult' {
            Say ("otaResult ok=$($j.ok) fw=$($j.fw) reason=$($j.reason)")
            $done = $true
        }
        'otaError' {
            if ("$($j.reason)" -like '*busy*') {
                $busySeen = $true
                Say 'otaError "busy" received -- anti-double-session guard OK, session continues'
            } else {
                Say ("otaError: $($j.reason)")
                $done = $true
            }
        }
        default { }
    }
}
$port.Close()
if ($SecondStartAt -ge 0) { Say ("busy guard verdict: " + $(if ($busySeen) { "SEEN (expected)" } else { "NOT SEEN (test FAILED)" })) }
if (-not $done -and -not $stopped) { Say "global script TIMEOUT" }
Say "done"
