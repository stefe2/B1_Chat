# Banc de test OTA : pilote le maître B1 par le port série (console fermée),
# rejoue le protocole JSON de la console (hello/ping/otaStart/otaChunk) et
# permet d'injecter des fautes : chunk corrompu, abandon en plein transfert,
# double otaStart. Voir CLAUDE.md section OTA pour le protocole.
param(
    [Parameter(Mandatory=$true)][string]$Bin,
    [int]$Target = 18872,          # B1-Rouge (0x49B8)
    [int]$CorruptChunk = -1,       # index de chunk à corrompre en vol (-1 = aucun)
    [int]$StopAtChunk = -1,        # ferme brutalement le port à ce chunk (-1 = jamais)
    [int]$SecondStartAt = -1,      # envoie un 2e otaStart à ce chunk (-1 = jamais)
    [string]$ComPort = "COM3",
    [string]$LogFile = "$env:TEMP\ota-test.log"   # trace complète TX/RX (hors dépôt)
)

$ErrorActionPreference = 'Stop'

function Log([string]$msg) {
    Add-Content -Path $LogFile -Value ("{0:HH:mm:ss.fff} {1}" -f (Get-Date), $msg)
}
function Say([string]$msg) { Write-Output ("{0:HH:mm:ss} {1}" -f (Get-Date), $msg); Log ("== " + $msg) }

$image = [System.IO.File]::ReadAllBytes($Bin)
$md5 = ([System.Security.Cryptography.MD5]::Create().ComputeHash($image) | ForEach-Object { $_.ToString("x2") }) -join ''
Say "image $Bin ($($image.Length) o, md5 $md5)"

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
        Say "chunk $idx CORROMPU en vol (octet 0 inversé)"
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
                Say "hello ok (maître fw $($j.fw))"
                Send ('{"cmd":"otaStart","target":' + $Target + ',"size":' + $image.Length + ',"md5":"' + $md5 + '"}')
            }
        }
        'otaReady' {
            $chunkSize = $j.chunkSize
            $totalChunks = $j.totalChunks
            Say "otaReady : $totalChunks chunks de $chunkSize o (session $($j.sessionId))"
            SendChunk 0
        }
        'otaChunkAck' {
            $sent = [int]$j.sent
            if ($sent % 500 -eq 0) { Say "progression $sent/$totalChunks" }
            if ($SecondStartAt -ge 0 -and $sent -ge $SecondStartAt -and -not $secondStartSent) {
                $secondStartSent = $true
                Say "2e otaStart injecté (attendu : otaError « occupé »)"
                Send ('{"cmd":"otaStart","target":' + $Target + ',"size":' + $image.Length + ',"md5":"' + $md5 + '"}')
            }
            if ($StopAtChunk -ge 0 -and $sent -ge $StopAtChunk) {
                Say "ABANDON VOLONTAIRE au chunk $sent (simulation console fermée)"
                $stopped = $true
                break
            }
            if ($sent -lt $totalChunks) { SendChunk $sent }
        }
        'otaDone' { Say "otaDone — l'esclave redémarre, attente du verdict…" }
        'otaResult' {
            Say ("otaResult ok=$($j.ok) fw=$($j.fw) reason=$($j.reason)")
            $done = $true
        }
        'otaError' {
            if ("$($j.reason)" -like '*occup*') {
                $busySeen = $true
                Say "otaError « occupé » reçu — garde anti-double-session OK, la session continue"
            } else {
                Say ("otaError : $($j.reason)")
                $done = $true
            }
        }
        default { }
    }
}
$port.Close()
if ($SecondStartAt -ge 0) { Say ("verdict garde busy : " + $(if ($busySeen) { "VUE (attendu)" } else { "NON VUE (ÉCHEC du test)" })) }
if (-not $done -and -not $stopped) { Say "TIMEOUT global du script" }
Say "fin"
