. D:\vs_code\uia.ps1

function Test-Step {
    param([string]$Name, [scriptblock]$Action, [int]$SettleSeconds = 6)
    $win = Get-AppWindow -TimeoutSeconds 5
    if (-not $win) { Write-Host ("  [{0}] SKIPPED - app already gone" -f $Name); return $false }
    try { & $Action $win } catch { Write-Host ("  [{0}] action threw: {1}" -f $Name, $_.Exception.Message) }
    Start-Sleep -Seconds $SettleSeconds
    $alive = [bool](Get-Process CoursePlayer -ErrorAction SilentlyContinue)
    Write-Host ("  [{0}] alive={1}" -f $Name, $alive)
    return $alive
}