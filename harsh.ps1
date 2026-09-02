. D:\vs_code\uia.ps1

# Harsh trial: switch lessons quickly, seeking before some switches, and report where it dies.
function Invoke-Harsh {
    param([int]$Switches = 15, [double]$Dwell = 1.5, [switch]$Seek)

    $win = Start-App
    Open-FirstVideo $win
    Start-Sleep -Seconds 4
    if (-not (Get-Process CoursePlayer -ErrorAction SilentlyContinue)) { return -1 }

    for ($k = 1; $k -le $Switches; $k++) {
        $w = Get-AppWindow -TimeoutSeconds 3
        if (-not $w) { return ($k - 1) }

        if ($Seek -and ($k % 2 -eq 1)) {
            try {
                $s = @(Get-Sliders $w)[0]
                $rv = $s.GetCurrentPattern([System.Windows.Automation.RangeValuePattern]::Pattern)
                Set-SliderValue $s ($rv.Current.Maximum * 0.6)
            } catch { }
            Start-Sleep -Milliseconds 700
            if (-not (Get-Process CoursePlayer -ErrorAction SilentlyContinue)) { return ($k - 1) }
        }

        $btns = @(Get-BottomBarButtons $w)
        if ($btns.Count -lt 5) { return ($k - 1) }
        try { Invoke-El $btns[4] } catch { return ($k - 1) }

        Start-Sleep -Seconds $Dwell
        if (-not (Get-Process CoursePlayer -ErrorAction SilentlyContinue)) { return ($k - 1) }
    }
    return $Switches
}

function Invoke-HarshTrials {
    param([int]$Count = 3, [int]$Switches = 15, [double]$Dwell = 1.5, [switch]$Seek)
    $died = 0
    for ($t = 1; $t -le $Count; $t++) {
        $r = Invoke-Harsh -Switches $Switches -Dwell $Dwell -Seek:$Seek
        if ($r -lt $Switches) { $died++ ; Write-Host ("  trial {0}: DIED after {1} switch(es)" -f $t, $r) }
        else { Write-Host ("  trial {0}: OK ({1} switches)" -f $t, $r) }
    }
    Write-Host ("  => {0}/{1} died" -f $died, $Count)
}