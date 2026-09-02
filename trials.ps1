. D:\vs_code\uia.ps1

# One trial: open the first lesson, then press Next repeatedly with a short dwell and a
# full UIA tree read each round. Returns the number of switches that completed.
function Invoke-Trial {
    param([int]$Switches = 12, [double]$Dwell = 3, [switch]$ReadTree)

    $win = Start-App
    Open-FirstVideo $win
    Start-Sleep -Seconds 4
    if (-not (Get-Process CoursePlayer -ErrorAction SilentlyContinue)) { return -1 }

    for ($k = 1; $k -le $Switches; $k++) {
        $w = Get-AppWindow -TimeoutSeconds 3
        if (-not $w) { return ($k - 1) }
        $btns = @(Get-BottomBarButtons $w)
        if ($btns.Count -lt 5) { return ($k - 1) }
        try { Invoke-El $btns[4] } catch { return ($k - 1) }
        Start-Sleep -Seconds $Dwell
        if (-not (Get-Process CoursePlayer -ErrorAction SilentlyContinue)) { return ($k - 1) }
        if ($ReadTree) {
            $w2 = Get-AppWindow -TimeoutSeconds 2
            if (-not $w2) { return ($k - 1) }
            Get-Clock $w2 | Out-Null
            if (-not (Get-Process CoursePlayer -ErrorAction SilentlyContinue)) { return ($k - 1) }
        }
    }
    return $Switches
}

function Invoke-Trials {
    param([int]$Count = 3, [int]$Switches = 12, [double]$Dwell = 3, [switch]$ReadTree)
    $results = @()
    for ($t = 1; $t -le $Count; $t++) {
        $r = Invoke-Trial -Switches $Switches -Dwell $Dwell -ReadTree:$ReadTree
        $results += $r
        $verdict = if ($r -ge $Switches) { "OK" } else { "DIED" }
        Write-Host ("  trial {0}: {1} switch(es) -> {2}" -f $t, $r, $verdict)
    }
    $failed = @($results | Where-Object { $_ -lt $Switches }).Count
    Write-Host ("  => {0}/{1} trial(s) died" -f $failed, $Count)
}