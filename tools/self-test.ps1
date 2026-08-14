# ============================================================================
# B1 Chat autonomous, non-destructive self-test
#
# Default behavior:
#   1. builds master + slave firmware and the WPF console;
#   2. checks critical source/build invariants;
#   3. auto-detects a connected B1 master and runs read-only serial checks;
#   4. sends a few deliberately INVALID commands to verify rejection.
#
# It NEVER flashes, starts OTA, enables/disables servos, previews a position,
# changes calibration, or sends a valid animation/configuration command.
# ============================================================================
param(
    [switch]$SkipBuild,
    [switch]$SkipSerial,
    [switch]$RequireHardware,
    [string]$ComPort = "",
    [ValidateRange(1, 30)][int]$ObserveSeconds = 4
)

$ErrorActionPreference = "Stop"
$repo = Split-Path -Parent $PSScriptRoot
$results = [System.Collections.Generic.List[object]]::new()
$script:InboxOverflowSeen = $false

function Add-Result([string]$name, [string]$status, [string]$detail = "") {
    $results.Add([pscustomobject]@{ Name = $name; Status = $status; Detail = $detail })
    $color = switch ($status) {
        "PASS" { "Green" }
        "FAIL" { "Red" }
        "WARN" { "Yellow" }
        default { "DarkGray" }
    }
    Write-Host ("[{0}] {1}{2}" -f $status, $name, $(if ($detail) { " - $detail" } else { "" })) -ForegroundColor $color
}

function Invoke-Test([string]$name, [scriptblock]$body) {
    try {
        $detail = & $body
        Add-Result $name "PASS" ($detail -join " ")
    } catch {
        Add-Result $name "FAIL" $_.Exception.Message
    }
}

function Assert-True([bool]$condition, [string]$message) {
    if (-not $condition) { throw $message }
}

function Assert-Source([string]$relativePath, [string]$pattern, [string]$message) {
    $content = Get-Content (Join-Path $repo $relativePath) -Raw
    if ($content -notmatch $pattern) { throw $message }
}

function Invoke-Build([string]$label, [scriptblock]$command) {
    Invoke-Test $label {
        $output = & $command 2>&1
        if ($LASTEXITCODE -ne 0) {
            throw (($output | Select-Object -Last 20) -join [Environment]::NewLine)
        }
        ($output | Select-String "SUCCESS|Build succeeded" | Select-Object -Last 1).Line
    }
}

function Wait-JsonEvent(
    [System.IO.Ports.SerialPort]$port,
    [scriptblock]$predicate,
    [double]$timeoutSeconds
) {
    $deadline = [DateTime]::UtcNow.AddSeconds($timeoutSeconds)
    while ([DateTime]::UtcNow -lt $deadline) {
        try { $line = $port.ReadLine().Trim() }
        catch [System.TimeoutException] { continue }
        if (-not $line) { continue }
        try { $evt = $line | ConvertFrom-Json }
        catch { continue }
        if ($evt.evt -eq "log" -and "$($evt.msg)" -like "*mesh inbox full*") {
            $script:InboxOverflowSeen = $true
        }
        if (& $predicate $evt) { return $evt }
    }
    return $null
}

function Send-And-Wait(
    [System.IO.Ports.SerialPort]$port,
    [string]$json,
    [scriptblock]$predicate,
    [double]$timeoutSeconds = 4
) {
    $port.WriteLine($json)
    Wait-JsonEvent $port $predicate $timeoutSeconds
}

function Find-B1Master {
    $ports = if ($ComPort) { @($ComPort) } else { @([System.IO.Ports.SerialPort]::GetPortNames() | Sort-Object) }
    foreach ($name in $ports) {
        $port = [System.IO.Ports.SerialPort]::new($name, 115200)
        $port.NewLine = "`n"
        $port.Encoding = [System.Text.Encoding]::UTF8
        $port.ReadTimeout = 200
        $port.WriteTimeout = 1000
        try {
            $port.Open()
            Start-Sleep -Milliseconds 1400
            $port.DiscardInBuffer()
            $hello = Send-And-Wait $port '{"cmd":"hello"}' {
                param($e) $e.evt -eq "hello" -and $e.ok -eq $true
            } 5
            if ($null -ne $hello) {
                return [pscustomobject]@{ Port = $port; Hello = $hello; Name = $name }
            }
        } catch {
            # Busy/non-B1 ports are expected during automatic discovery.
        }
        if ($port.IsOpen) { $port.Close() }
        $port.Dispose()
    }
    return $null
}

Set-Location $repo
Write-Host "B1 Chat autonomous self-test (non-destructive)" -ForegroundColor Cyan

$originalBuildNumber = (Get-Content "console/build.number" -Raw).Trim()

if (-not $SkipBuild) {
    $pio = Join-Path $env:USERPROFILE ".platformio\penv\Scripts\pio.exe"
    Invoke-Test "PlatformIO available" {
        Assert-True (Test-Path $pio) "pio.exe not found at $pio"
        $pio
    }
    if (Test-Path $pio) {
        Invoke-Build "Firmware master build" { & $pio run -e b1_master }
        Invoke-Build "Firmware slave build" { & $pio run -e b1_slave }
    }

    $tempBuildNumber = Join-Path ([IO.Path]::GetTempPath()) ("b1-build-{0}.number" -f [Guid]::NewGuid().ToString("N"))
    try {
        [IO.File]::WriteAllText($tempBuildNumber, $originalBuildNumber)
        Invoke-Build "WPF console build" {
            & dotnet build "console/b1-chat-console.csproj" --no-restore `
                "-p:BuildNumberFile=$tempBuildNumber" "-p:BuildNumber=$originalBuildNumber"
        }
        Invoke-Build "Sequencer unit tests" {
            # Restore is intentionally allowed here: unlike the main console project, the
            # test-only xUnit packages may not exist yet on a fresh developer machine. The
            # ProjectReference opts out of the product build-number increment.
            & dotnet test "console.tests/b1-chat-console.Tests.csproj" `
                "-p:SkipBuildNumberIncrement=true"
        }
    } finally {
        if (Test-Path $tempBuildNumber) { Remove-Item -LiteralPath $tempBuildNumber -Force }
    }
}

Invoke-Test "Console build number preserved" {
    $after = (Get-Content "console/build.number" -Raw).Trim()
    Assert-True ($after -eq $originalBuildNumber) "build.number changed from $originalBuildNumber to $after"
    $after
}

Invoke-Test "No whitespace errors" {
    $output = & git diff --check 2>&1
    if ($LASTEXITCODE -ne 0) { throw ($output -join [Environment]::NewLine) }
    "git diff --check clean"
}

Invoke-Test "Callback-to-loop mesh isolation present" {
    Assert-Source "src/main.cpp" "enqueueMeshMessage" "mesh inbox enqueue missing"
    Assert-Source "src/main.cpp" "pumpMeshInbox\(\)" "mesh inbox pump missing"
    "bounded inbox detected"
}

Invoke-Test "Per-droid animation persistence present" {
    Assert-Source "src/config_store.cpp" "animParamsFor\(uint16_t id" "per-ID animation getter missing"
    Assert-Source "src/serial_console.cpp" 'doc\["target"\] = resolvedTarget' "targeted config response missing"
    "per-ID NVS + targeted protocol detected"
}

Invoke-Test "Strict serial and mesh validation present" {
    Assert-Source "src/serial_console.cpp" "readIntField" "serial range validation missing"
    Assert-Source "src/main.cpp" "validConfigPayload" "mesh config validation missing"
    Assert-Source "src/ota_slave.cpp" "p\.dataLen > OTA_CHUNK_DATA_MAX" "OTA chunk bound missing"
    "serial, mesh and OTA guards detected"
}

Invoke-Test "Virgin-board motion defaults fail closed" {
    Assert-Source "src/main.cpp" "Config\.servosEnabled\(false\)" "virgin servo default is not off"
    Assert-Source "src/main.cpp" "Config\.autoAnimEnabled\(false\)" "virgin automatic-animation default is not off"
    Assert-Source "src/main.cpp" "static bool gLocateOn = false" "locate default is not off"
    Assert-Source "src/main.cpp" "gLocateOn \? 4 : 0" "Locate state is not reported in heartbeat"
    Assert-Source "src/servo_engine.h" "bool _enabled = false" "servo PWM engine does not start detached"
    "servos + automatic animations + locate default off; PWM detached"
}

Invoke-Test "Per-axis servo reverse pipeline present" {
    Assert-Source "src/config_store.cpp" "reverseKey\(uint16_t id" "per-droid reverse persistence missing"
    Assert-Source "src/mesh_comm.h" "MSG_CALIB_V2" "mesh reverse-calibration payload missing"
    Assert-Source "src/mesh_comm.h" "MSG_CAPABILITIES" "per-droid capability report missing"
    Assert-Source "src/servo_engine.cpp" "reverseAroundCenter\(panDeg" "PAN output reversal missing"
    Assert-Source "src/servo_engine.cpp" "reverseAroundCenter\(tiltDeg" "TILT output reversal missing"
    Assert-Source "console/ViewModels/CalibrationViewModel.cs" "PanReversed" "console PAN reverse setting missing"
    Assert-Source "console/ViewModels/CalibrationViewModel.cs" "TiltReversed" "console TILT reverse setting missing"
    Assert-Source "console/ViewModels/CalibrationViewModel.cs" "SelectedTarget\?\.SupportsServoReverse" "selected-droid capability gate missing"
    "NVS + compatible mesh + servo output + console controls detected"
}

Invoke-Test "Firmware Build ID pipeline present" {
    Assert-Source "platformio.ini" "pio_build_id\.py" "PlatformIO Build ID generator missing"
    Assert-Source "src/mesh_comm.h" "uint32_t buildId" "heartbeat Build ID missing"
    Assert-Source "src/ota_master.cpp" "buildChanged" "OTA Build ID comparison missing"
    "content-derived build propagated through heartbeat and OTA verdict"
}

Invoke-Test "Animation execution-report pipeline present" {
    Assert-Source "src/mesh_comm.h" "MSG_ANIM_EXEC" "mesh execution report type missing"
    Assert-Source "src/mesh_comm.cpp" "hdr\.seq" "mesh sequence correlation missing"
    Assert-Source "src/main.cpp" "ANIM_EXEC_INTERRUPTED" "firmware lifecycle reporting missing"
    Assert-Source "src/serial_console.cpp" 'doc\["evt"\] = "animAccepted"' "master animation acceptance event missing"
    Assert-Source "src/serial_console.cpp" 'doc\["evt"\] = "animExec"' "serial execution event missing"
    Assert-Source "console/Services/ProtocolClient.cs" "AnimExecutionReceived" "console execution parser missing"
    Assert-Source "console/Services/ProtocolClient.cs" "AnimMasterAccepted" "console master acceptance parser missing"
    Assert-Source "console/Services/SerialLinkService.cs" "public bool Write" "serial write result is not observable"
    Assert-Source "console/ViewModels/SequencerViewModel.cs" "TrackExecution" "Sequencer execution aggregation missing"
    Assert-Source "console/ViewModels/SequencerViewModel.cs" "ExecutionStartTimeoutMs" "execution start timeout missing"
    Assert-Source "console/ViewModels/SequencerViewModel.cs" '"TIMEOUT"' "execution completion timeout state missing"
    Assert-Source "console/ViewModels/SequencerViewModel.cs" '"UNCONF"' "unconfirmed execution state missing"
    Assert-Source "console/ViewModels/SequencerViewModel.cs" "StopInfiniteGestures" "infinite gesture cleanup missing"
    Assert-Source "src/mesh_comm.h" "MSG_ANIM_LEASE_RENEW" "firmware animation lease protocol missing"
    Assert-Source "src/main.cpp" "ANIM_EXEC_REASON_LEASE_EXPIRED" "firmware lease expiry missing"
    Assert-Source "console/ViewModels/SequencerViewModel.cs" "ScheduleAnimLeaseRenewal" "Sequencer lease renewal missing"
    Assert-Source "src/main.cpp" "applySafeStop" "firmware Safe Stop missing"
    Assert-Source "console/ViewModels/SequencerViewModel.cs" "EmergencyStop" "Sequencer Emergency Stop missing"
    "delivery stages, lifecycle reporting, leases and three-level stop policy detected"
}

Invoke-Test "Boot sequence randomization present" {
    Assert-Source "src/mesh_comm.cpp" "_seq = \(uint16_t\)esp_random\(\)" "mesh sequence still deterministic"
    "random sequence seed detected"
}

Invoke-Test "Firmware downloads fail closed" {
    Assert-Source "console/Services/UpdateService.cs" "Release manifest has no SHA-256" "missing-checksum rejection absent"
    Assert-Source "console/Services/UpdateService.cs" "sha256\.Length != 64" "checksum shape validation absent"
    "mandatory SHA-256 detected"
}

Invoke-Test "Console Help packaging hardened" {
    Assert-Source "console/b1-chat-console.csproj" 'Help\\\*\*[\s\S]*CopyToPublishDirectory="PreserveNewest"[\s\S]*ExcludeFromSingleFile="true"' "Help files are not forced into publish output"
    Assert-Source "console/installer/release.ps1" 'verify-publish\.ps1' "release does not verify its publish payload"
    Assert-Source "console/ViewModels/HelpViewModel.cs" 'Help image missing' "missing Help images are not handled"
    "physical Help payload + release gate + runtime fallback detected"
}

Invoke-Test "Installer prerequisites checked" {
    Assert-Source "console/installer/b1-chat-console.nsi" 'AtLeastBuild\} 14393' "minimum Windows version check missing"
    Assert-Source "console/installer/b1-chat-console.nsi" 'IsNativeAMD64' "x64 architecture check missing"
    Assert-Source "console/installer/b1-chat-console.nsi" 'mfplat\.dll' "Media Foundation check missing"
    Assert-Source "console/installer/b1-chat-console.nsi" '--verify-install' "installed app self-check missing"
    Assert-Source "console/installer/b1-chat-console.nsi" 'espflash\.exe.*--version' "installed espflash self-check missing"
    "OS + architecture + media + installed binary checks detected"
}

Invoke-Test "Debounces snapshot their target" {
    Assert-Source "console/ViewModels/AnimationViewModel.cs" "var target = TargetId;" "animation target snapshot missing"
    Assert-Source "console/ViewModels/CalibrationViewModel.cs" "var target = SelectedTarget\.Id;" "calibration target snapshot missing"
    Assert-Source "console/ViewModels/CalibrationViewModel.cs" "_loadingCalibration" "calibration load suppression missing"
    "target snapshots + load suppression detected"
}

Invoke-Test "Audio failures are bounded and typed" {
    Assert-Source "console/Services/AudioProbe.cs" "AudioProbeStatus\.Timeout" "duration probe has no timeout outcome"
    Assert-Source "console/Services/AudioProbe.cs" "handle\?\.Dispose\(\)" "duration probe does not release its media handle"
    Assert-Source "console/Services/AudioPlaybackService.cs" "PlaybackFailed" "playback failures are not surfaced"
    Assert-Source "console/Views/SequenceTimelineView.xaml" 'ToolTip="\{Binding AudioFailureText\}"' "playback failures are not visible in the Sequencer UI"
    Assert-Source "console/Services/WaveformService.cs" "LastWriteTimeUtc" "waveform cache key ignores file changes"
    Assert-Source "console/ViewModels/SequencerViewModel.cs" "clip\.WaveformToken != token" "stale waveform assignment is not rejected"
    "typed probe + bounded cache + reported playback failures detected"
}

# The test suite now opens the committed fixture through both NAudio and WPF MediaPlayer on a real
# STA dispatcher. This prerequisite check gives a direct installation diagnosis before those
# codec tests run on Windows N/KN editions without the optional media stack.
Invoke-Test "Media Foundation prerequisites" {
    $fixture = Join-Path $repo "console.tests/Fixtures/Audio/probe-tone-1500ms.mp3"
    if (-not (Test-Path $fixture)) { throw "audio fixture missing: $fixture" }
    $mfplat = Join-Path $env:WINDIR "System32/mfplat.dll"
    if (-not (Test-Path $mfplat)) {
        throw "mfplat.dll not found — Sequencer audio playback will not work on this machine (Windows N/KN needs the Media Feature Pack)"
    }
    $size = (Get-Item $fixture).Length
    "Media Foundation present; dispatcher smoke-test fixture $([math]::Round($size / 1KB, 1)) KB"
}

if (-not $SkipSerial) {
    $device = Find-B1Master
    if ($null -eq $device) {
        $hardwareStatus = if ($RequireHardware) { "FAIL" } else { "SKIP" }
        Add-Result "B1 serial integration" $hardwareStatus "no available B1 master found (or its COM port is already open)"
    } else {
        $port = $device.Port
        try {
            $hello = $device.Hello
            Add-Result "B1 master handshake" "PASS" ("{0}, fw {1}, build {2}, proto {3}" -f $device.Name, $hello.fw, $hello.build, $hello.proto)
            $masterId = [int]$hello.id

            Invoke-Test "Animation safety capabilities" {
                Assert-True (@($hello.caps) -contains "animExec") "master does not advertise animExec"
                Assert-True (@($hello.caps) -contains "animAccepted") "master does not advertise animAccepted"
                Assert-True (@($hello.caps) -contains "animLease") "master does not advertise animLease"
                Assert-True (@($hello.caps) -contains "safeStop") "master does not advertise safeStop"
                "animExec + animAccepted + animLease + safeStop advertised"
            }

            $droids = Send-And-Wait $port '{"cmd":"list"}' { param($e) $e.evt -eq "droids" }
            Invoke-Test "Droid inventory response" {
                Assert-True ($null -ne $droids) "no droids event"
                $master = @($droids.list | Where-Object { [int]$_.id -eq $masterId -and $_.role -eq "master" })
                Assert-True ($master.Count -eq 1) "master missing from inventory"
                $droidCount = @($droids.list).Count
                $slaveCount = @($droids.list | Where-Object role -eq "slave").Count
                "{0} droid(s), {1} slave(s)" -f $droidCount, $slaveCount
            }

            Invoke-Test "Firmware Build ID propagation" {
                Assert-True ("$($hello.build)" -match '^[0-9A-Fa-f]{8}$') "hello has no valid 8-hex Build ID"
                foreach ($droid in @($droids.list)) {
                    Assert-True ("$($droid.build)" -match '^[0-9A-Fa-f]{8}$') "droid $($droid.id) has no valid Build ID"
                }
                $master = @($droids.list | Where-Object { [int]$_.id -eq $masterId })[0]
                Assert-True ("$($master.build)" -eq "$($hello.build)") "master hello/inventory Build IDs disagree"
                "master $($hello.build); fleet identities present"
            }

            $configJson = '{"cmd":"getConfig","target":' + $masterId + '}'
            $configBefore = Send-And-Wait $port $configJson {
                param($e) $e.evt -eq "config" -and [int]$e.target -eq $masterId
            }
            Invoke-Test "Targeted config read" {
                Assert-True ($null -ne $configBefore) "no targeted config response"
                foreach ($v in @([int]$configBefore.freq, [int]$configBefore.amp, [int]$configBefore.speed)) {
                    Assert-True ($v -ge 0 -and $v -le 100) "config value outside 0..100"
                }
                "freq=$($configBefore.freq), amp=$($configBefore.amp), speed=$($configBefore.speed)"
            }

            $calibJson = '{"cmd":"getCalib","target":' + $masterId + '}'
            $calibBefore = Send-And-Wait $port $calibJson {
                param($e) $e.evt -eq "calibData" -and [int]$e.target -eq $masterId
            }
            Invoke-Test "Calibration read" {
                Assert-True ($null -ne $calibBefore) "no calibration response"
                Assert-True ([int]$calibBefore.panMin -le [int]$calibBefore.panCenter -and [int]$calibBefore.panCenter -le [int]$calibBefore.panMax) "invalid stored pan order"
                Assert-True ([int]$calibBefore.tiltMin -le [int]$calibBefore.tiltCenter -and [int]$calibBefore.tiltCenter -le [int]$calibBefore.tiltMax) "invalid stored tilt order"
                if (@($hello.caps) -contains "servoReverse") {
                    Assert-True ($calibBefore.panReversed -is [bool]) "PAN reverse state missing or not boolean"
                    Assert-True ($calibBefore.tiltReversed -is [bool]) "TILT reverse state missing or not boolean"
                }
                "pan $($calibBefore.panMin)/$($calibBefore.panCenter)/$($calibBefore.panMax), tilt $($calibBefore.tiltMin)/$($calibBefore.tiltCenter)/$($calibBefore.tiltMax)"
            }

            $durations = Send-And-Wait $port '{"cmd":"getAnimDurations"}' { param($e) $e.evt -eq "animDurations" }
            Invoke-Test "Animation duration catalog" {
                Assert-True ($null -ne $durations) "no duration catalog"
                Assert-True (@($durations.list).Count -eq 18) "expected 18 animations"
                Assert-True (@($durations.list | Where-Object { [int]$_.ms -le 0 }).Count -eq 0) "zero/negative duration found"
                "18 positive durations"
            }

            # Capability strings and version labels are not sufficient proof that the binary
            # actually contains strict validation. Probe a read-only command first: old/stale
            # firmware may accept invalid setters and mutate the bench instead of returning err.
            $validationProbe = Send-And-Wait $port '{"cmd":"getConfig","target":70000}' { param($e) $e.evt -eq "err" }
            $strictValidation = $null -ne $validationProbe
            Invoke-Test "Runtime validation preflight" {
                Assert-True $strictValidation "read-only invalid target was not rejected; mutating rejection tests suppressed"
                "$($validationProbe.msg)"
            }

            if ($strictValidation) {
                $badAnim = Send-And-Wait $port '{"cmd":"anim","target":65535,"animId":99}' { param($e) $e.evt -eq "err" }
                Invoke-Test "Invalid animation rejected" {
                    Assert-True ($null -ne $badAnim) "invalid anim produced no err event"
                    "$($badAnim.msg)"
                }

                $badLeasedAnim = Send-And-Wait $port '{"cmd":"anim","target":65535,"animId":2,"leaseMs":5000}' { param($e) $e.evt -eq "err" }
                Invoke-Test "Invalid leased animation rejected" {
                    Assert-True ($null -ne $badLeasedAnim) "finite leased anim produced no err event"
                    "$($badLeasedAnim.msg)"
                }

                $badLeaseRenewal = Send-And-Wait $port '{"cmd":"animLease","target":65535,"meshSeq":1,"leaseMs":999}' { param($e) $e.evt -eq "err" }
                Invoke-Test "Invalid lease renewal rejected" {
                    Assert-True ($null -ne $badLeaseRenewal) "short lease renewal produced no err event"
                    "$($badLeaseRenewal.msg)"
                }

                $badSafeStop = Send-And-Wait $port '{"cmd":"safeStop","target":0}' { param($e) $e.evt -eq "err" }
                Invoke-Test "Invalid Safe Stop rejected" {
                    Assert-True ($null -ne $badSafeStop) "invalid Safe Stop produced no err event"
                    "$($badSafeStop.msg)"
                }

                $badConfig = Send-And-Wait $port '{"cmd":"config","target":65535,"freq":101,"amp":60,"speed":50}' { param($e) $e.evt -eq "err" }
                $configAfter = Send-And-Wait $port $configJson {
                    param($e) $e.evt -eq "config" -and [int]$e.target -eq $masterId
                }
                Invoke-Test "Invalid config rejected without mutation" {
                    Assert-True ($null -ne $badConfig) "invalid config produced no err event"
                    Assert-True ($null -ne $configAfter) "config could not be reread"
                    Assert-True ([int]$configBefore.freq -eq [int]$configAfter.freq -and [int]$configBefore.amp -eq [int]$configAfter.amp -and [int]$configBefore.speed -eq [int]$configAfter.speed) "invalid config changed stored values"
                    "$($badConfig.msg)"
                }

                $badCalibJson = '{"cmd":"calib","target":' + $masterId + ',"panMin":120,"panCenter":90,"panMax":60,"tiltMin":60,"tiltCenter":90,"tiltMax":120}'
                $badCalib = Send-And-Wait $port $badCalibJson { param($e) $e.evt -eq "err" }
                $calibAfter = Send-And-Wait $port $calibJson {
                    param($e) $e.evt -eq "calibData" -and [int]$e.target -eq $masterId
                }
                Invoke-Test "Invalid calibration rejected without mutation" {
                    Assert-True ($null -ne $badCalib) "invalid calibration produced no err event"
                    Assert-True ($null -ne $calibAfter) "calibration could not be reread"
                    foreach ($field in @("panMin","panCenter","panMax","tiltMin","tiltCenter","tiltMax")) {
                        Assert-True ([int]$calibBefore.$field -eq [int]$calibAfter.$field) "invalid calibration changed $field"
                    }
                    "$($badCalib.msg)"
                }

                if (@($hello.caps) -contains "servoReverse") {
                    $badReverseJson = '{"cmd":"calib","target":' + $masterId + ',"panMin":' + [int]$calibBefore.panMin + ',"panCenter":' + [int]$calibBefore.panCenter + ',"panMax":' + [int]$calibBefore.panMax + ',"tiltMin":' + [int]$calibBefore.tiltMin + ',"tiltCenter":' + [int]$calibBefore.tiltCenter + ',"tiltMax":' + [int]$calibBefore.tiltMax + ',"panReversed":"invalid","tiltReversed":false}'
                    $badReverse = Send-And-Wait $port $badReverseJson { param($e) $e.evt -eq "err" }
                    $calibAfterReverse = Send-And-Wait $port $calibJson {
                        param($e) $e.evt -eq "calibData" -and [int]$e.target -eq $masterId
                    }
                    Invoke-Test "Invalid servo reverse rejected without mutation" {
                        Assert-True ($null -ne $badReverse) "invalid reverse flag produced no err event"
                        Assert-True ($null -ne $calibAfterReverse) "calibration could not be reread"
                        foreach ($field in @("panMin","panCenter","panMax","tiltMin","tiltCenter","tiltMax","panReversed","tiltReversed")) {
                            Assert-True ($calibBefore.$field -eq $calibAfterReverse.$field) "invalid reverse flag changed $field"
                        }
                        "$($badReverse.msg)"
                    }
                } else {
                    Add-Result "Invalid servo reverse rejected without mutation" "SKIP" "firmware does not advertise servoReverse"
                }
            } else {
                $reason = "runtime validation preflight failed; command intentionally not sent"
                Add-Result "Invalid animation rejected" "SKIP" $reason
                Add-Result "Invalid leased animation rejected" "SKIP" $reason
                Add-Result "Invalid lease renewal rejected" "SKIP" $reason
                Add-Result "Invalid Safe Stop rejected" "SKIP" $reason
                Add-Result "Invalid config rejected without mutation" "SKIP" $reason
                Add-Result "Invalid calibration rejected without mutation" "SKIP" $reason
                Add-Result "Invalid servo reverse rejected without mutation" "SKIP" $reason
            }

            Start-Sleep -Seconds $ObserveSeconds
            $droidsAfter = Send-And-Wait $port '{"cmd":"list"}' { param($e) $e.evt -eq "droids" }
            Invoke-Test "Short mesh health observation" {
                Assert-True ($null -ne $droidsAfter) "inventory stopped responding"
                $beforeIds = @($droids.list | ForEach-Object { [int]$_.id } | Sort-Object)
                $afterIds = @($droidsAfter.list | ForEach-Object { [int]$_.id } | Sort-Object)
                Assert-True (($beforeIds -join ',') -eq ($afterIds -join ',')) "droid inventory changed during observation"
                Assert-True (-not $script:InboxOverflowSeen) "mesh inbox overflow reported"
                "stable for $ObserveSeconds second(s)"
            }
        } finally {
            if ($port.IsOpen) { $port.Close() }
            $port.Dispose()
        }
    }
}

$failed = @($results | Where-Object Status -eq "FAIL").Count
$passed = @($results | Where-Object Status -eq "PASS").Count
$skipped = @($results | Where-Object Status -eq "SKIP").Count
$reportPath = Join-Path ([IO.Path]::GetTempPath()) ("b1-self-test-{0:yyyyMMdd-HHmmss}.json" -f (Get-Date))
[pscustomobject]@{
    Timestamp = (Get-Date).ToString("o")
    Passed = $passed
    Failed = $failed
    Skipped = $skipped
    Results = $results
} | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath $reportPath -Encoding UTF8

Write-Host ""
Write-Host ("Summary: {0} passed, {1} failed, {2} skipped" -f $passed, $failed, $skipped) -ForegroundColor $(if ($failed) { "Red" } else { "Green" })
Write-Host "Report: $reportPath"
if ($failed -gt 0) { exit 1 }
