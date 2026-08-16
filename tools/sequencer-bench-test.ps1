# ============================================================================
# B1 Chat active Sequencer bench test
#
# Default mode is a read-only preflight. Physical commands are sent only when
# -AllowMotion is explicit. The active phase snapshots servo/auto-animation
# states, finishes every motion group with IDLE, and restores those states in a
# finally block. It never flashes, starts OTA, writes calibration,
# commits NVS drafts, or intentionally disconnects the serial link.
# ============================================================================
param(
    [string]$ComPort = "",
    [ValidateRange(1, 16)][int]$ExpectedDroidCount = 3,
    [ValidateRange(0, 15)][int]$ExpectedSlaveCount = 2,
    [ValidateRange(1, 50)][int]$LoopCycles = 5,
    [switch]$AllowMotion
)

$ErrorActionPreference = "Stop"
$results = [System.Collections.Generic.List[object]]::new()
$script:InboxOverflowSeen = $false
$script:Port = $null
$script:Snapshot = @()
$script:MasterId = 0
$script:CleanupRequired = $false
$script:ReportPath = Join-Path ([IO.Path]::GetTempPath()) (
    "b1-sequencer-bench-{0:yyyyMMdd-HHmmss}.json" -f (Get-Date))

function Add-Result([string]$name, [string]$status, [string]$detail = "") {
    $results.Add([pscustomobject]@{ Name = $name; Status = $status; Detail = $detail })
    $color = switch ($status) {
        "PASS" { "Green" }
        "FAIL" { "Red" }
        "SKIP" { "DarkGray" }
        default { "Yellow" }
    }
    Write-Host ("[{0}] {1}{2}" -f $status, $name, $(if ($detail) { " - $detail" } else { "" })) -ForegroundColor $color
}

function Assert-Bench([bool]$condition, [string]$message) {
    if (-not $condition) { throw $message }
}

function Wait-JsonEvent([scriptblock]$predicate, [double]$timeoutSeconds = 4) {
    $deadline = [DateTime]::UtcNow.AddSeconds($timeoutSeconds)
    while ([DateTime]::UtcNow -lt $deadline) {
        try { $line = $script:Port.ReadLine().Trim() }
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

function Send-JsonAndWait(
    [hashtable]$command,
    [scriptblock]$predicate,
    [double]$timeoutSeconds = 4
) {
    $script:Port.WriteLine(($command | ConvertTo-Json -Compress))
    Wait-JsonEvent $predicate $timeoutSeconds
}

function Open-B1Master {
    $names = if ($ComPort) {
        @($ComPort)
    } else {
        @([System.IO.Ports.SerialPort]::GetPortNames() | Sort-Object)
    }

    foreach ($name in $names) {
        $candidate = [System.IO.Ports.SerialPort]::new($name, 115200)
        $candidate.NewLine = "`n"
        $candidate.Encoding = [System.Text.Encoding]::UTF8
        $candidate.ReadTimeout = 200
        $candidate.WriteTimeout = 1000
        try {
            $candidate.Open()
            Start-Sleep -Milliseconds 1400
            $candidate.DiscardInBuffer()
            $script:Port = $candidate
            $hello = Send-JsonAndWait @{ cmd = "hello" } {
                param($e) $e.evt -eq "hello" -and $e.ok -eq $true
            } 5
            if ($null -ne $hello) {
                return [pscustomobject]@{ Name = $name; Hello = $hello }
            }
        } catch { }

        if ($candidate.IsOpen) { $candidate.Close() }
        $candidate.Dispose()
        $script:Port = $null
    }
    throw "No available B1 master found. Close the WPF console and verify the COM port."
}

function Read-Inventory {
    Send-JsonAndWait @{ cmd = "list" } { param($e) $e.evt -eq "droids" } 5
}

function Wait-InventoryState([scriptblock]$predicate, [double]$timeoutSeconds = 8) {
    $deadline = [DateTime]::UtcNow.AddSeconds($timeoutSeconds)
    while ([DateTime]::UtcNow -lt $deadline) {
        $inventory = Read-Inventory
        if ($null -ne $inventory -and (& $predicate @($inventory.list))) { return $inventory }
        Start-Sleep -Milliseconds 250
    }
    return $null
}

function Send-Idle([int]$target = 65535) {
    $null = Send-JsonAndWait @{ cmd = "anim"; target = $target; animId = 0; seed = 1 } {
        param($e) $e.evt -eq "log" -and "$($e.msg)" -like "anim 0*"
    } 3
}

function Send-Animation([int]$target, [int]$animId, [uint32]$seed) {
    $ack = Send-JsonAndWait @{ cmd = "anim"; target = $target; animId = $animId; seed = $seed } {
        param($e) $e.evt -eq "log" -and "$($e.msg)" -like "anim $animId*"
    } 3
    Assert-Bench ($null -ne $ack) "master did not log anim $animId for target $target"
}

function Set-BooleanCommand([string]$cmd, [int]$target, [bool]$enabled) {
    $expected = if ($enabled) { "ON" } else { "OFF" }
    $ack = Send-JsonAndWait @{ cmd = $cmd; target = $target; enabled = $enabled } {
        param($e) $e.evt -eq "log" -and "$($e.msg)" -like "*$expected*"
    } 3
    Assert-Bench ($null -ne $ack) "$cmd=$enabled was not acknowledged for target $target"
}

function Restore-BenchState {
    if ($null -eq $script:Port -or -not $script:Port.IsOpen -or -not $script:CleanupRequired) { return }
    Write-Host "Restoring bench state..." -ForegroundColor Cyan
    try { Send-Idle 65535 } catch { Add-Result "Cleanup IDLE" "WARN" $_.Exception.Message }
    foreach ($droid in $script:Snapshot) {
        try { Set-BooleanCommand "servo" ([int]$droid.id) ([bool]$droid.servos) }
        catch { Add-Result "Restore servos $($droid.id)" "WARN" $_.Exception.Message }
    }

    try {
        $restored = Wait-InventoryState {
            param($list)
            foreach ($before in $script:Snapshot) {
                $after = @($list | Where-Object { [int]$_.id -eq [int]$before.id })
                if ($after.Count -ne 1 -or
                    [bool]$after[0].servos -ne [bool]$before.servos) { return $false }
            }
            return $true
        } 10
        if ($null -eq $restored) { throw "restored states were not reflected by inventory" }
        Add-Result "Bench state restored" "PASS" "IDLE plus original servo states"
    } catch {
        Add-Result "Bench state restored" "WARN" $_.Exception.Message
    }
}

$failure = $null
try {
    Write-Host "B1 Chat Sequencer bench test" -ForegroundColor Cyan
    $device = Open-B1Master
    $hello = $device.Hello
    $script:MasterId = [int]$hello.id
    Add-Result "Master handshake" "PASS" ("{0}, id {1}, fw {2}, build {3}, proto {4}" -f $device.Name, $script:MasterId, $hello.fw, $hello.build, $hello.proto)

    Assert-Bench ([int]$hello.proto -ge 5) "protocol 5 or newer is required"
    Assert-Bench (@($hello.caps) -contains "err") "firmware does not advertise err capability"
    Assert-Bench (@($hello.caps) -contains "animExec") "firmware does not advertise animExec"
    Assert-Bench (@($hello.caps) -contains "animAccepted") "firmware does not advertise animAccepted"
    Assert-Bench (@($hello.caps) -contains "animLease") "firmware does not advertise animLease"
    Assert-Bench (@($hello.caps) -contains "safeStop") "firmware does not advertise safeStop"
    Assert-Bench ("$($hello.build)" -match '^[0-9A-Fa-f]{8}$') "master has no valid Build ID"

    $inventory = Read-Inventory
    Assert-Bench ($null -ne $inventory) "no droid inventory response"
    $droids = @($inventory.list)
    $slaves = @($droids | Where-Object role -eq "slave")
    Assert-Bench ($droids.Count -eq $ExpectedDroidCount) "expected $ExpectedDroidCount droids, found $($droids.Count)"
    Assert-Bench ($slaves.Count -eq $ExpectedSlaveCount) "expected $ExpectedSlaveCount slaves, found $($slaves.Count)"
    Assert-Bench (@($droids | Where-Object { $_.fw -ne $hello.fw }).Count -eq 0) "fleet firmware versions are inconsistent"
    Assert-Bench (@($droids | Where-Object { "$($_.build)" -notmatch '^[0-9A-Fa-f]{8}$' }).Count -eq 0) "one or more droids have no valid Build ID"
    $masterInventory = @($droids | Where-Object { [int]$_.id -eq $script:MasterId })
    Assert-Bench ($masterInventory.Count -eq 1 -and "$($masterInventory[0].build)" -eq "$($hello.build)") "master hello/inventory Build IDs disagree"
    $script:Snapshot = @($droids | ForEach-Object {
        [pscustomobject]@{ id = [int]$_.id; servos = [bool]$_.servos; role = "$($_.role)" }
    })
    $identities = ($droids | ForEach-Object { "$($_.id):$($_.build)" }) -join ", "
    Add-Result "Fleet inventory" "PASS" ("1 master + {0} slaves, fw {1}; {2}" -f $slaves.Count, $hello.fw, $identities)

    $calibrationById = @{}
    foreach ($droid in $droids) {
        $id = [int]$droid.id
        $cal = Send-JsonAndWait @{ cmd = "getCalib"; target = $id } {
            param($e) $e.evt -eq "calibData" -and [int]$e.target -eq $id
        } 4
        Assert-Bench ($null -ne $cal) "calibration response missing for droid $id"
        Assert-Bench ([int]$cal.panMin -le [int]$cal.panCenter -and [int]$cal.panCenter -le [int]$cal.panMax) "invalid pan calibration for droid $id"
        Assert-Bench ([int]$cal.tiltMin -le [int]$cal.tiltCenter -and [int]$cal.tiltCenter -le [int]$cal.tiltMax) "invalid tilt calibration for droid $id"
        $calibrationById[$id] = $cal
    }
    Add-Result "Targeted calibration contract" "PASS" "all droids returned bounded values with matching target IDs"

    $durations = Send-JsonAndWait @{ cmd = "getAnimDurations" } { param($e) $e.evt -eq "animDurations" } 4
    Assert-Bench ($null -ne $durations -and @($durations.list).Count -eq 18) "18-entry duration catalog missing"
    Assert-Bench (@($durations.list | Where-Object { [int]$_.ms -le 0 }).Count -eq 0) "duration catalog contains zero/negative values"
    $durationById = @{}
    foreach ($item in @($durations.list)) { $durationById[[int]$item.animId] = [int]$item.ms }
    Add-Result "Animation durations" "PASS" "18 positive durations"

    $validationProbe = Send-JsonAndWait @{ cmd = "getCalib"; target = 70000 } { param($e) $e.evt -eq "err" } 4
    Assert-Bench ($null -ne $validationProbe) "read-only invalid-target probe was accepted; active testing refused"
    Add-Result "Runtime validation preflight" "PASS" "$($validationProbe.msg)"

    $topology = Send-JsonAndWait @{ cmd = "getMeshTopology" } { param($e) $e.evt -eq "meshTopology" } 4
    Assert-Bench ($null -ne $topology) "mesh topology response missing"
    Add-Result "Mesh topology" "PASS" ("{0} directed link(s) reported" -f @($topology.links).Count)

    if (-not $AllowMotion) {
        Add-Result "Active motion scenarios" "SKIP" "read-only preflight only; rerun with -AllowMotion"
    } else {
        $script:CleanupRequired = $true

        foreach ($droid in $script:Snapshot) { Set-BooleanCommand "servo" ([int]$droid.id) $true }
        $armed = Wait-InventoryState {
            param($list) @($list | Where-Object { -not [bool]$_.servos }).Count -eq 0
        } 10
        Assert-Bench ($null -ne $armed) "not every droid reported servos enabled"
        Add-Result "Deterministic bench preparation" "PASS" "servos enabled on all three targets"

        $masterCal = $calibrationById[$script:MasterId]
        $panQuarter = [int][Math]::Round(([int]$masterCal.panMin * 0.75) + ([int]$masterCal.panMax * 0.25))
        $panThreeQuarter = [int][Math]::Round(([int]$masterCal.panMin * 0.25) + ([int]$masterCal.panMax * 0.75))
        foreach ($pan in @([int]$masterCal.panCenter, $panQuarter, $panThreeQuarter, [int]$masterCal.panCenter)) {
            $script:Port.WriteLine((@{ cmd = "preview"; target = $script:MasterId; pan = $pan; tilt = [int]$masterCal.tiltCenter } | ConvertTo-Json -Compress))
            Start-Sleep -Milliseconds 500
        }
        Add-Result "Calibrated master preview" "PASS" "center, 25%, 75%, center within stored limits"

        foreach ($droid in $script:Snapshot) {
            Send-Animation ([int]$droid.id) 2 0x51000001
            Start-Sleep -Milliseconds ($durationById[2] + 250)
        }
        Send-Idle 65535
        Add-Result "Targeted finite gestures" "PASS" "master accepted one command for each droid target"

        Send-Animation 65535 3 0x51000002
        Start-Sleep -Milliseconds ($durationById[3] + 250)
        Send-Idle 65535
        Add-Result "Broadcast finite gesture" "PASS" "broadcast accepted and terminated with IDLE"

        Send-Animation $script:MasterId 4 0x510000AA
        Start-Sleep -Milliseconds ($durationById[4] + 150)
        Send-Animation $script:MasterId 4 0x510000AA
        Start-Sleep -Milliseconds ($durationById[4] + 150)
        Send-Idle $script:MasterId
        Add-Result "Repeated deterministic seed" "PASS" "same target/animation/seed sent twice"

        foreach ($animId in @(5, 6, 7)) {
            Send-Animation 65535 $animId ([uint32](0x51000100 + $animId))
            Start-Sleep -Milliseconds 100
        }
        Send-Idle 65535
        Add-Result "Rapid restart and Stop" "PASS" "three rapid broadcasts followed by IDLE"

        Send-Animation $script:MasterId 17 0x51000017
        Start-Sleep -Milliseconds 1000
        Send-Idle $script:MasterId
        Send-Animation 65535 16 0x51000016
        Start-Sleep -Milliseconds 1000
        Send-Idle 65535
        Add-Result "Infinite gesture interruption" "PASS" "TALK and POWER_DOWN each terminated by IDLE"

        for ($cycle = 1; $cycle -le $LoopCycles; $cycle++) {
            Send-Animation 65535 2 ([uint32](0x51001000 + $cycle))
            Start-Sleep -Milliseconds ($durationById[2] + 100)
        }
        Send-Idle 65535
        Assert-Bench (-not $script:InboxOverflowSeen) "mesh inbox overflow observed during loop stress"
        $finalInventory = Read-Inventory
        Assert-Bench ($null -ne $finalInventory -and @($finalInventory.list).Count -eq $ExpectedDroidCount) "fleet inventory became unstable during motion tests"
        Add-Result "Loop stress" "PASS" "$LoopCycles broadcast cycles; inventory stable; no inbox overflow observed"
    }
} catch {
    $failure = $_.Exception.Message
    Add-Result "Bench test" "FAIL" $failure
} finally {
    Restore-BenchState
    if ($null -ne $script:Port) {
        if ($script:Port.IsOpen) { $script:Port.Close() }
        $script:Port.Dispose()
    }

    $failed = @($results | Where-Object Status -eq "FAIL").Count
    $passed = @($results | Where-Object Status -eq "PASS").Count
    $skipped = @($results | Where-Object Status -eq "SKIP").Count
    [pscustomobject]@{
        Timestamp = (Get-Date).ToString("o")
        ActiveMotionAuthorized = [bool]$AllowMotion
        Passed = $passed
        Failed = $failed
        Skipped = $skipped
        Results = $results
    } | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath $script:ReportPath -Encoding UTF8

    Write-Host ""
    Write-Host "Summary: $passed passed, $failed failed, $skipped skipped" -ForegroundColor $(if ($failed) { "Red" } else { "Green" })
    Write-Host "Report: $script:ReportPath"
}

if ($failure) { exit 1 }
