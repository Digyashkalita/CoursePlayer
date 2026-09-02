Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes

function Get-AppWindow {
    param([int]$TimeoutSeconds = 20)
    $deadline = (Get-Date).AddSeconds($TimeoutSeconds)
    while ((Get-Date) -lt $deadline) {
        $proc = Get-Process CoursePlayer -ErrorAction SilentlyContinue | Select-Object -First 1
        if ($proc -and $proc.MainWindowHandle -ne 0) {
            $el = [System.Windows.Automation.AutomationElement]::FromHandle($proc.MainWindowHandle)
            if ($el) { return $el }
        }
        Start-Sleep -Milliseconds 500
    }
    return $null
}

function Get-Elements {
    param($Window, [string]$Type)
    $ct = [System.Windows.Automation.ControlType]::$Type
    $cond = New-Object System.Windows.Automation.PropertyCondition(
        [System.Windows.Automation.AutomationElement]::ControlTypeProperty, $ct)
    $list = @()
    foreach ($e in $Window.FindAll([System.Windows.Automation.TreeScope]::Descendants, $cond)) {
        if (-not $e.Current.IsOffscreen) { $list += $e }
    }
    return $list
}

function Get-Clock {
    param($Window)
    foreach ($t in (Get-Elements $Window 'Text')) {
        if ($t.Current.Name -match '^\s*\d+:\d\d(:\d\d)?\s+/\s+\d') { return $t.Current.Name.Trim() }
    }
    return $null
}

function Get-Texts {
    param($Window)
    (Get-Elements $Window 'Text') | ForEach-Object { $_.Current.Name } | Where-Object { $_.Trim() }
}

# Player buttons, left-to-right on the bottom bar. The bar sits in the lowest 80px of the
# window, so filter by Y and sort by X to get stable ordinals.
function Get-BottomBarButtons {
    param($Window)
    $wr = $Window.Current.BoundingRectangle
    $minY = $wr.Y + $wr.Height - 80
    (Get-Elements $Window 'Button') |
        Where-Object {
            $r = $_.Current.BoundingRectangle
            $r.Y -gt $minY -and $r.Width -lt 60 -and $r.Height -gt 25
        } |
        Sort-Object { $_.Current.BoundingRectangle.X }
}

function Invoke-El {
    param($Element)
    $Element.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
}

function Invoke-Where {
    param($Window, [string]$Type = 'Button', [scriptblock]$Match)
    foreach ($e in (Get-Elements $Window $Type)) {
        $r = $e.Current.BoundingRectangle
        if (& $Match $r $e) { Invoke-El $e; return $r }
    }
    return $null
}

function Get-Sliders {
    param($Window)
    (Get-Elements $Window 'Slider') | Sort-Object { $_.Current.BoundingRectangle.Y }
}

function Get-SliderValue {
    param($Element)
    $p = $Element.GetCurrentPattern([System.Windows.Automation.RangeValuePattern]::Pattern)
    return $p.Current.Value
}

function Set-SliderValue {
    param($Element, [double]$Value)
    $p = $Element.GetCurrentPattern([System.Windows.Automation.RangeValuePattern]::Pattern)
    $p.SetValue($Value)
}

function Send-Keys {
    param([string]$Keys)
    Add-Type -AssemblyName System.Windows.Forms
    [System.Windows.Forms.SendKeys]::SendWait($Keys)
}

function Get-LogTail {
    param([int]$Lines = 40)
    $file = Get-ChildItem "$env:LOCALAPPDATA\CoursePlayer\logs" -Filter 'courseplayer-*.log' |
        Sort-Object LastWriteTime -Descending | Select-Object -First 1
    $all = Get-Content $file.FullName
    $starts = $all | Select-String 'CoursePlayer starting' | Select-Object -Expand LineNumber
    $from = if ($starts) { $starts[-1] - 1 } else { 0 }
    $all[$from..($all.Length - 1)] | Select-Object -Last $Lines
}

function Start-App {
    Get-Process CoursePlayer -ErrorAction SilentlyContinue | Stop-Process -Force
    Start-Sleep -Milliseconds 800
    $env:COURSEPLAYER_FFMPEG_DIR = 'C:\ffmpeg\x64'
    Start-Process 'D:\vs_code\CoursePlayer\bin\Debug\net9.0-windows\win-x64\CoursePlayer.exe' | Out-Null
    return Get-AppWindow
}

# Home -> first course card -> first video lesson.
function Open-FirstVideo {
    param($Window)
    Invoke-Where $Window 'Button' { param($r) $r.X -gt 520 -and $r.Y -gt 200 -and $r.Y -lt 320 } | Out-Null
    Start-Sleep -Seconds 3
    # Asset rows expose a play button on the far right of each row.
    Invoke-Where $Window 'Button' { param($r) $r.X -gt 1450 -and $r.Y -gt 255 -and $r.Y -lt 305 } | Out-Null
    Start-Sleep -Seconds 6
}