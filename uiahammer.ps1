. D:\vs_code\uia.ps1

# Hammer the UIA tree with NO playback interaction at all. If the process dies here, the
# accessibility probing is the trigger rather than any player action.
function Invoke-UiaHammer {
    param([int]$Seconds = 60)

    $win = Start-App
    Open-FirstVideo $win
    Start-Sleep -Seconds 3
    if (-not (Get-Process CoursePlayer -ErrorAction SilentlyContinue)) { Write-Host "  died on open"; return $false }

    $deadline = (Get-Date).AddSeconds($Seconds)
    $reads = 0
    while ((Get-Date) -lt $deadline) {
        $w = Get-AppWindow -TimeoutSeconds 2
        if (-not $w) { Write-Host ("  DIED after {0} UIA read(s)" -f $reads); return $false }
        try {
            Get-BottomBarButtons $w | Out-Null
            Get-Clock $w | Out-Null
            Get-Sliders $w | Out-Null
            $reads++
        } catch {
            Write-Host ("  UIA read threw after {0}: {1}" -f $reads, $_.Exception.Message)
        }
        if (-not (Get-Process CoursePlayer -ErrorAction SilentlyContinue)) {
            Write-Host ("  DIED after {0} UIA read(s)" -f $reads)
            return $false
        }
    }
    Write-Host ("  survived {0} UIA read(s) with no interaction" -f $reads)
    return $true
}