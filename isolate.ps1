. D:\vs_code\uia.ps1

# Same harsh loop, but Start-App honours extra environment switches for A/B isolation.
function Start-AppEnv {
    param([hashtable]$Env = @{})
    Get-Process CoursePlayer -ErrorAction SilentlyContinue | Stop-Process -Force
    Start-Sleep -Milliseconds 800
    $env:COURSEPLAYER_FFMPEG_DIR = 'C:\ffmpeg\x64'
    $env:COURSEPLAYER_NO_AUDIO = $null
    $env:COURSEPLAYER_NO_VIDEO = $null
    foreach ($k in $Env.Keys) { Set-Item -Path ("env:" + $k) -Value $Env[$k] }
    Start-Process 'D:\vs_code\CoursePlayer\bin\Debug\net9.0-windows\win-x64\CoursePlayer.exe' | Out-Null
    return Get-AppWindow
}

function Invoke-HarshEnv {
    param([int]$Switches = 15, [double]$Dwell = 1.5, [switch]$Seek, [hashtable]$Env = @{})

    $win = Start-AppEnv -Env $Env
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

function Invoke-Case {
    param([string]$Name, [hashtable]$Env = @{}, [int]$Count = 2, [int]$Switches = 15)
    Write-Host ("=== {0} ===" -f $Name)
    $died = 0
    for ($t = 1; $t -le $Count; $t++) {
        $r = Invoke-HarshEnv -Switches $Switches -Dwell 1.5 -Seek -Env $Env
        if ($r -lt $Switches) { $died++; Write-Host ("  trial {0}: DIED after {1}" -f $t, $r) }
        else { Write-Host ("  trial {0}: OK" -f $t) }
    }
    Write-Host ("  => {0}/{1} died" -f $died, $Count)
}