. D:\vs_code\uia.ps1

# Hammer: open the first lesson, then press Next repeatedly. Reports how many switches
# completed before the process died (if it died at all).
function Hammer {
    param([int]$Switches = 8, [int]$DwellSeconds = 6)

    $win = Start-App
    Open-FirstVideo $win
    Start-Sleep -Seconds 5
    if (-not (Get-Process CoursePlayer -ErrorAction SilentlyContinue)) {
        Write-Host "  died before the first switch"
        return -1
    }

    for ($k = 1; $k -le $Switches; $k++) {
        $w = Get-AppWindow -TimeoutSeconds 3
        if (-not $w) { Write-Host ("  died before switch {0}" -f $k); return ($k - 1) }
        $btns = @(Get-BottomBarButtons $w)
        if ($btns.Count -lt 5) { Write-Host ("  no next button at switch {0}" -f $k); return ($k - 1) }
        try { Invoke-El $btns[4] } catch { Write-Host ("  switch {0} invoke failed: {1}" -f $k, $_.Exception.Message); return ($k - 1) }
        Start-Sleep -Seconds $DwellSeconds
        if (-not (Get-Process CoursePlayer -ErrorAction SilentlyContinue)) {
            Write-Host ("  DIED on switch {0}" -f $k)
            return ($k - 1)
        }
        Write-Host ("  switch {0} ok" -f $k)
    }
    return $Switches
}