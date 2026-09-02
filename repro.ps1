. D:\vs_code\uia.ps1

function Report {
    param([string]$Tag)
    $p = Get-Process CoursePlayer -ErrorAction SilentlyContinue
    Write-Host ("{0,-34} alive={1}" -f $Tag, [bool]$p)
    return [bool]$p
}

function Watch {
    param([int]$Seconds = 20, [string]$Tag = "watch")
    $t = 0
    while ($t -lt $Seconds) {
        Start-Sleep -Seconds 2
        $t += 2
        $w = Get-AppWindow -TimeoutSeconds 1
        if (-not $w) { Write-Host ("  {0}: DIED at t+{1}s" -f $Tag, $t); return $false }
    }
    $w = Get-AppWindow -TimeoutSeconds 1
    Write-Host ("  {0}: survived {1}s, clock={2}" -f $Tag, $Seconds, (Get-Clock $w))
    return $true
}

function Next-Lesson {
    param($Window)
    $btns = @(Get-BottomBarButtons $Window)
    Invoke-El $btns[4]
}

function Seek-To {
    param($Window, [double]$Fraction)
    $s = @(Get-Sliders $Window)[0]
    $rv = $s.GetCurrentPattern([System.Windows.Automation.RangeValuePattern]::Pattern)
    Set-SliderValue $s ($rv.Current.Maximum * $Fraction)
}

# ---------- CASE A: switch with NO preceding seek, three times ----------
Write-Host "=== CASE A: three switches, no seeks ==="
$win = Start-App
Open-FirstVideo $win
Watch 8 "after open" | Out-Null
for ($k = 1; $k -le 3; $k++) {
    $w = Get-AppWindow -TimeoutSeconds 3
    if (-not $w) { Write-Host "  window gone before switch $k"; break }
    Next-Lesson $w
    if (-not (Watch 16 "switch $k")) { break }
}
Report "CASE A end" | Out-Null

# ---------- CASE B: seek, then switch ----------
Write-Host "=== CASE B: seek then switch ==="
$win = Start-App
Open-FirstVideo $win
Watch 6 "after open" | Out-Null
$w = Get-AppWindow -TimeoutSeconds 3
Seek-To $w 0.3
Write-Host "  seeked to 30%"
if (Watch 8 "after seek") {
    $w = Get-AppWindow -TimeoutSeconds 3
    Next-Lesson $w
    Watch 20 "switch after seek" | Out-Null
}
Report "CASE B end" | Out-Null
Write-Host "=== LOG ==="
Get-LogTail 20