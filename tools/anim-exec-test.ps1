# ============================================================================
# B1 Chat headless animation execution-report bench test
#
# The master is forced servo-off for the whole active phase. Only slave servo
# outputs are enabled; this exercises their software animation engines even
# when no physical servos are attached. Original servo/auto-animation states
# are restored in finally. No configuration/calibration/OTA/NVS draft changes.
# ============================================================================
param(
    [string]$ComPort = "COM3",
    [ValidateRange(1, 15)][int]$ExpectedSlaveCount = 2
)

$ErrorActionPreference = "Stop"
$script:Port = $null
$script:RequestId = 1000
$script:Reports = [System.Collections.Generic.List[object]]::new()
$script:Acceptances = [System.Collections.Generic.List[object]]::new()
$script:Snapshot = @()
$results = [System.Collections.Generic.List[object]]::new()
$reportPath = Join-Path ([IO.Path]::GetTempPath()) (
    "b1-anim-exec-{0:yyyyMMdd-HHmmss}.json" -f (Get-Date))

function Add-Result([string]$name, [string]$status, [string]$detail = "") {
    $results.Add([pscustomobject]@{ Name = $name; Status = $status; Detail = $detail })
    $color = if ($status -eq "PASS") { "Green" } elseif ($status -eq "FAIL") { "Red" } else { "Yellow" }
    Write-Host ("[{0}] {1}{2}" -f $status, $name, $(if ($detail) { " - $detail" } else { "" })) -ForegroundColor $color
}

function Assert-Bench([bool]$condition, [string]$message) {
    if (-not $condition) { throw $message }
}

function Read-Event([double]$timeoutSeconds = 0.5) {
    $deadline = [DateTime]::UtcNow.AddSeconds($timeoutSeconds)
    while ([DateTime]::UtcNow -lt $deadline) {
        try { $line = $script:Port.ReadLine().Trim() }
        catch [System.TimeoutException] { continue }
        if (-not $line) { continue }
        try { $evt = $line | ConvertFrom-Json }
        catch { continue }
        if ($evt.evt -eq "animExec") { $script:Reports.Add($evt) }
        if ($evt.evt -eq "animAccepted") { $script:Acceptances.Add($evt) }
        return $evt
    }
    return $null
}

function Send-And-Wait([hashtable]$command, [scriptblock]$predicate, [double]$timeoutSeconds = 6) {
    $script:Port.WriteLine(($command | ConvertTo-Json -Compress))
    $deadline = [DateTime]::UtcNow.AddSeconds($timeoutSeconds)
    while ([DateTime]::UtcNow -lt $deadline) {
        $evt = Read-Event 0.5
        if ($null -ne $evt -and (& $predicate $evt)) { return $evt }
    }
    return $null
}

function Read-Inventory {
    Send-And-Wait @{ cmd = "list" } { param($e) $e.evt -eq "droids" } 6
}

function Wait-Inventory([scriptblock]$predicate, [double]$timeoutSeconds = 10) {
    $deadline = [DateTime]::UtcNow.AddSeconds($timeoutSeconds)
    while ([DateTime]::UtcNow -lt $deadline) {
        $inventory = Read-Inventory
        if ($null -ne $inventory -and (& $predicate @($inventory.list))) { return $inventory }
        Start-Sleep -Milliseconds 250
    }
    return $null
}

function Set-Boolean([string]$cmd, [int]$target, [bool]$enabled) {
    $script:Port.WriteLine((@{ cmd = $cmd; target = $target; enabled = $enabled } | ConvertTo-Json -Compress))
}

function Send-TrackedAnim([int]$target, [int]$animId, [int]$leaseMs = 0) {
    $script:RequestId++
    $requestId = $script:RequestId
    $command = @{
        cmd = "anim"; target = $target; animId = $animId
        seed = [uint32](0x62000000 + $requestId); requestId = $requestId
    }
    if ($leaseMs -gt 0) { $command.leaseMs = $leaseMs }
    $script:Port.WriteLine(($command | ConvertTo-Json -Compress))
    return $requestId
}

function Renew-AnimLease([int]$target, [int]$meshSeq, [int]$leaseMs) {
    $script:Port.WriteLine((@{
        cmd = "animLease"; target = $target; meshSeq = $meshSeq; leaseMs = $leaseMs
    } | ConvertTo-Json -Compress))
}

function Wait-RequestReports(
    [int]$requestId,
    [int[]]$droidIds,
    [string[]]$phases,
    [double]$timeoutSeconds = 8
) {
    $deadline = [DateTime]::UtcNow.AddSeconds($timeoutSeconds)
    while ([DateTime]::UtcNow -lt $deadline) {
        $matched = @($script:Reports | Where-Object {
            [int]$_.requestId -eq $requestId -and
            $phases -contains "$($_.phase)" -and
            $droidIds -contains [int]$_.droid
        })
        $seenIds = @($matched | ForEach-Object { [int]$_.droid } | Sort-Object -Unique)
        if ($seenIds.Count -eq $droidIds.Count) { return $matched }
        $null = Read-Event 0.5
    }
    $got = @($script:Reports | Where-Object { [int]$_.requestId -eq $requestId } |
        ForEach-Object { "$($_.droid):$($_.phase)" }) -join ", "
    throw "request $requestId missing phase [$($phases -join '/')]; received: $got"
}

function Wait-MasterAcceptance(
    [int]$requestId,
    [int]$target,
    [int]$animId,
    [double]$timeoutSeconds = 4
) {
    $deadline = [DateTime]::UtcNow.AddSeconds($timeoutSeconds)
    while ([DateTime]::UtcNow -lt $deadline) {
        $matched = @($script:Acceptances | Where-Object {
            [int]$_.requestId -eq $requestId -and
            [int]$_.target -eq $target -and
            [int]$_.animId -eq $animId
        })
        if ($matched.Count -gt 0) { return $matched[-1] }
        $null = Read-Event 0.25
    }
    throw "request $requestId was not accepted by the master"
}

function Restore-State {
    if ($null -eq $script:Port -or -not $script:Port.IsOpen) { return }
    foreach ($droid in $script:Snapshot) {
        try { Set-Boolean "autoAnim" ([int]$droid.id) ([bool]$droid.autoAnim) } catch { }
        try { Set-Boolean "servo" ([int]$droid.id) ([bool]$droid.servos) } catch { }
    }
    Start-Sleep -Seconds 2
}

$failure = $null
try {
    Write-Host "B1 Chat headless animation execution test" -ForegroundColor Cyan
    $script:Port = [System.IO.Ports.SerialPort]::new($ComPort, 115200)
    $script:Port.NewLine = "`n"
    $script:Port.ReadTimeout = 200
    $script:Port.WriteTimeout = 1000
    $script:Port.DtrEnable = $false
    $script:Port.RtsEnable = $false
    $script:Port.Open()
    Start-Sleep -Seconds 4
    $script:Port.DiscardInBuffer()

    $hello = Send-And-Wait @{ cmd = "hello" } { param($e) $e.evt -eq "hello" -and $e.ok } 6
    Assert-Bench ($null -ne $hello) "master handshake missing"
    Assert-Bench (@($hello.caps) -contains "animExec") "firmware does not advertise animExec"
    Assert-Bench (@($hello.caps) -contains "animAccepted") "firmware does not advertise animAccepted"
    Assert-Bench (@($hello.caps) -contains "animLease") "firmware does not advertise animLease"
    Add-Result "Execution-report capabilities" "PASS" ("master {0}, build {1}" -f $hello.id, $hello.build)

    $inventory = Read-Inventory
    Assert-Bench ($null -ne $inventory) "inventory missing"
    $script:Snapshot = @($inventory.list | ForEach-Object {
        [pscustomobject]@{ id = [int]$_.id; role = "$($_.role)"; servos = [bool]$_.servos; autoAnim = [bool]$_.autoAnim }
    })
    $master = @($script:Snapshot | Where-Object role -eq "master")
    $slaves = @($script:Snapshot | Where-Object role -eq "slave")
    Assert-Bench ($master.Count -eq 1) "expected one master"
    Assert-Bench ($slaves.Count -eq $ExpectedSlaveCount) "expected $ExpectedSlaveCount slaves, found $($slaves.Count)"

    # Keep the only physically attached servos motionless. The slaves have no
    # physical servos, so enabling their outputs is a headless software test.
    Set-Boolean "servo" ([int]$master[0].id) $false
    Set-Boolean "autoAnim" ([int]$master[0].id) $false
    foreach ($slave in $slaves) {
        Set-Boolean "autoAnim" ([int]$slave.id) $false
        Set-Boolean "servo" ([int]$slave.id) $true
    }
    $armed = Wait-Inventory {
        param($list)
        $m = @($list | Where-Object role -eq "master")
        $s = @($list | Where-Object role -eq "slave")
        $m.Count -eq 1 -and -not [bool]$m[0].servos -and
        @($s | Where-Object { -not [bool]$_.servos -or [bool]$_.autoAnim }).Count -eq 0
    } 12
    Assert-Bench ($null -ne $armed) "headless servo/auto-animation state did not propagate"
    Add-Result "Safe headless preparation" "PASS" "master servos off; slave software engines on; auto animations off"

    foreach ($slave in $slaves) {
        $request = Send-TrackedAnim ([int]$slave.id) 2
        $accepted = Wait-MasterAcceptance $request ([int]$slave.id) 2
        Assert-Bench ([bool]$accepted.meshQueued) "master did not queue targeted animation on mesh"
        $null = Wait-RequestReports $request @([int]$slave.id) @("started") 6
        $null = Wait-RequestReports $request @([int]$slave.id) @("completed") 8
    }
    Add-Result "Targeted slave lifecycle" "PASS" "$($slaves.Count) slave(s) reported started + completed"

    $allIds = @($script:Snapshot | ForEach-Object { [int]$_.id })
    $broadcast = Send-TrackedAnim 65535 3
    $accepted = Wait-MasterAcceptance $broadcast 65535 3
    Assert-Bench ([bool]$accepted.meshQueued -and [bool]$accepted.local) "master did not accept broadcast for mesh + local execution"
    $initial = Wait-RequestReports $broadcast $allIds @("started", "rejected") 8
    $masterReport = @($initial | Where-Object { [int]$_.droid -eq [int]$master[0].id })
    Assert-Bench ($masterReport.Count -eq 1 -and "$($masterReport[0].phase)" -eq "rejected" -and "$($masterReport[0].reason)" -eq "servosOff") "master did not explicitly reject while servo-off"
    $null = Wait-RequestReports $broadcast @($slaves | ForEach-Object { [int]$_.id }) @("completed") 8
    Add-Result "Broadcast mixed lifecycle" "PASS" "master rejected servosOff; every headless slave completed"

    $firstSlave = [int]$slaves[0].id
    $looping = Send-TrackedAnim $firstSlave 17
    $null = Wait-MasterAcceptance $looping $firstSlave 17
    $null = Wait-RequestReports $looping @($firstSlave) @("started") 6
    Start-Sleep -Milliseconds 300
    $idle = Send-TrackedAnim $firstSlave 0
    $null = Wait-MasterAcceptance $idle $firstSlave 0
    $null = Wait-RequestReports $looping @($firstSlave) @("interrupted") 6
    $null = Wait-RequestReports $idle @($firstSlave) @("started") 6
    $null = Wait-RequestReports $idle @($firstSlave) @("completed") 6
    Add-Result "Looping gesture interruption" "PASS" "TALK interrupted by tracked IDLE; IDLE completed"

    $leased = Send-TrackedAnim $firstSlave 17 1500
    $leasedAccepted = Wait-MasterAcceptance $leased $firstSlave 17
    Assert-Bench ([int]$leasedAccepted.leaseMs -eq 1500) "master did not preserve the requested lease"
    $null = Wait-RequestReports $leased @($firstSlave) @("started") 6
    $expired = Wait-RequestReports $leased @($firstSlave) @("interrupted") 5
    Assert-Bench (@($expired | Where-Object { "$($_.reason)" -eq "leaseExpired" }).Count -eq 1) "leased TALK did not expire with leaseExpired"
    Add-Result "Infinite gesture lease expiry" "PASS" "unrenewed 1500 ms TALK failed closed to IDLE"

    $renewed = Send-TrackedAnim $firstSlave 16 1500
    $renewedAccepted = Wait-MasterAcceptance $renewed $firstSlave 16
    $null = Wait-RequestReports $renewed @($firstSlave) @("started") 6
    for ($renewal = 0; $renewal -lt 3; $renewal++) {
        Start-Sleep -Milliseconds 700
        Renew-AnimLease $firstSlave ([int]$renewedAccepted.meshSeq) 1500
        $null = Read-Event 0.2
        $earlyExpiry = @($script:Reports | Where-Object {
            [int]$_.requestId -eq $renewed -and "$($_.reason)" -eq "leaseExpired"
        })
        Assert-Bench ($earlyExpiry.Count -eq 0) "POWER_DOWN expired while valid renewals were being sent"
    }
    $renewedExpiry = Wait-RequestReports $renewed @($firstSlave) @("interrupted") 5
    Assert-Bench (@($renewedExpiry | Where-Object { "$($_.reason)" -eq "leaseExpired" }).Count -eq 1) "renewed POWER_DOWN did not expire after renewals stopped"
    Add-Result "Infinite gesture lease renewal" "PASS" "three renewals extended POWER_DOWN; stopping renewals failed closed"

    $staleProtected = Send-TrackedAnim $firstSlave 17 1500
    $staleAccepted = Wait-MasterAcceptance $staleProtected $firstSlave 17
    Assert-Bench ([int]$staleAccepted.meshSeq -ne [int]$renewedAccepted.meshSeq) "test requires distinct animation mesh sequences"
    $null = Wait-RequestReports $staleProtected @($firstSlave) @("started") 6
    $staleTimer = [Diagnostics.Stopwatch]::StartNew()
    Start-Sleep -Milliseconds 900
    Renew-AnimLease $firstSlave ([int]$renewedAccepted.meshSeq) 1500
    $staleExpiry = Wait-RequestReports $staleProtected @($firstSlave) @("interrupted") 3
    $staleTimer.Stop()
    Assert-Bench (@($staleExpiry | Where-Object { "$($_.reason)" -eq "leaseExpired" }).Count -eq 1) "new TALK did not expire after stale renewal"
    Assert-Bench ($staleTimer.ElapsedMilliseconds -lt 2100) "stale sequence renewed a newer infinite gesture"
    Add-Result "Stale lease rejection" "PASS" ("old meshSeq ignored; new lease expired after {0} ms" -f $staleTimer.ElapsedMilliseconds)
} catch {
    $failure = $_.Exception.Message
    Add-Result "Headless execution test" "FAIL" $failure
} finally {
    Restore-State
    if ($null -ne $script:Port) {
        if ($script:Port.IsOpen) { $script:Port.Close() }
        $script:Port.Dispose()
    }
}

$passed = @($results | Where-Object Status -eq "PASS").Count
$failed = @($results | Where-Object Status -eq "FAIL").Count
[pscustomobject]@{
    Timestamp = (Get-Date).ToString("o")
    Passed = $passed
    Failed = $failed
    Results = $results
    Reports = $script:Reports
    Acceptances = $script:Acceptances
} | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $reportPath -Encoding UTF8

Write-Host ""
Write-Host ("Summary: {0} passed, {1} failed" -f $passed, $failed) -ForegroundColor $(if ($failed) { "Red" } else { "Green" })
Write-Host "Report: $reportPath"
if ($failed) { exit 1 }
