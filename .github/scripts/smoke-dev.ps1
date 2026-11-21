<#
Pull-request smoke for the dev build. Launches GeckoPeeksDev.exe with
GECKO_PEEKS_DEV_ROOT pointed at runner scratch and checks that the production
Run value and production log folders stay unchanged.

It also checks the v2 window: the window shows, the title carries the version,
the WebView2 page renders (a render of the window is not blank and the page
headings are readable through UI Automation), Run test now answers with the
Dev toast, and the Settings page shows a log path under the scratch root.

This script does not rewrite HKCU, change ACLs, or stop a process selected by
the production process name. It runs on a CI runner only.
#>
param(
    [Parameter(Mandatory = $true)][string]$Exe,
    [string]$ExpectVersion = "",
    [string]$OutDir = "smoke-dev"
)

$ErrorActionPreference = "Stop"
if ($env:GITHUB_ACTIONS -ne "true") {
    throw "smoke-dev.ps1 launches GeckoPeeksDev.exe; run it on a CI runner only."
}

New-Item -ItemType Directory -Force -Path $OutDir | Out-Null
$OutDir = (Resolve-Path -LiteralPath $OutDir).Path
$resolved = (Resolve-Path -LiteralPath $Exe).Path
if ((Split-Path -Leaf $resolved) -ne "GeckoPeeksDev.exe") {
    throw "Refusing to launch '$(Split-Path -Leaf $resolved)'. This smoke accepts GeckoPeeksDev.exe only."
}

Add-Type -AssemblyName System.Drawing
Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes
Add-Type -TypeDefinition @"
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;

public static class DevSmokeWin32
{
    private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential)]
    public struct RECT { public int Left; public int Top; public int Right; public int Bottom; }

    [DllImport("user32.dll")] private static extern bool EnumWindows(EnumWindowsProc callback, IntPtr lParam);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowText(IntPtr hWnd, StringBuilder text, int max);
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr hWnd, out RECT rect);
    [DllImport("user32.dll")] public static extern bool PrintWindow(IntPtr hWnd, IntPtr hdc, uint flags);
    [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr hWnd);
    [DllImport("user32.dll")] public static extern bool IsIconic(IntPtr hWnd);
    [DllImport("user32.dll")] public static extern bool SetProcessDPIAware();

    public static List<IntPtr> TopWindows(int processId)
    {
        var found = new List<IntPtr>();
        EnumWindows((hWnd, _) =>
        {
            uint owner;
            GetWindowThreadProcessId(hWnd, out owner);
            if (owner == (uint)processId)
                found.Add(hWnd);
            return true;
        }, IntPtr.Zero);
        return found;
    }

    public static string Title(IntPtr hWnd)
    {
        var text = new StringBuilder(512);
        GetWindowText(hWnd, text, text.Capacity);
        return text.ToString();
    }
}
"@
# Window rectangles, renders and UI Automation rectangles then share one pixel scale.
[void][DevSmokeWin32]::SetProcessDPIAware()

$documents = [Environment]::GetFolderPath("MyDocuments")
$localAppData = [Environment]::GetFolderPath("LocalApplicationData")
$productionLog = Join-Path $documents "RiotRouteLog"
$productionAppData = Join-Path $localAppData "GeckoPeeks"
$productionSkip = Join-Path $productionAppData "skipped-update.txt"
$runKey = "HKCU:\Software\Microsoft\Windows\CurrentVersion\Run"
$runName = "GeckoPeeks"
$devName = "GeckoPeeksDev"

function Test-IsUnder([string]$Path, [string]$Parent) {
    if ([string]::IsNullOrWhiteSpace($Parent)) { return $false }
    $full = [System.IO.Path]::GetFullPath($Path)
    $root = [System.IO.Path]::GetFullPath($Parent).TrimEnd('\', '/')
    if ($full.TrimEnd('\', '/') -eq $root) { return $true }
    $prefix = $root + [System.IO.Path]::DirectorySeparatorChar
    return $full.StartsWith($prefix, [System.StringComparison]::OrdinalIgnoreCase)
}

$runnerTemp = $env:RUNNER_TEMP
if ([string]::IsNullOrWhiteSpace($runnerTemp)) {
    throw "RUNNER_TEMP is required so the scratch root is not the executable directory."
}

$scratch = Join-Path $runnerTemp "gecko-peeks-dev-smoke"
$exeDirectory = Split-Path -Parent $resolved
if (Test-IsUnder $scratch $exeDirectory) { throw "Scratch root is inside the executable directory." }
if (Test-IsUnder $scratch $documents) { throw "Scratch root is inside Documents." }
if (Test-IsUnder $scratch $productionAppData) { throw "Scratch root is inside the production AppData folder." }

function Get-RunValue {
    if (-not (Test-Path -LiteralPath $runKey)) { return "" }
    $item = Get-ItemProperty -Path $runKey -Name $runName -ErrorAction SilentlyContinue
    if ($null -eq $item) { return "" }
    return [string]$item.$runName
}

function Get-TreeStamp([string]$Folder) {
    if (-not (Test-Path -LiteralPath $Folder)) { return "missing" }
    $rows = @(Get-ChildItem -LiteralPath $Folder -Recurse -File -Force | ForEach-Object {
        $hash = (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash
        $_.FullName.Substring($Folder.Length) + " " + $hash
    })
    return (($rows | Sort-Object) -join "`n")
}

function Get-ProductionProcessCount {
    @(Get-Process -ErrorAction SilentlyContinue | Where-Object { $_.ProcessName -eq "GeckoPeeks" }).Count
}

function Wait-Until([scriptblock]$Condition, [int]$TimeoutMs) {
    $clock = [System.Diagnostics.Stopwatch]::StartNew()
    while ($clock.ElapsedMilliseconds -lt $TimeoutMs) {
        $met = try { & $Condition } catch { $false }
        if ($met) { return [int]$clock.ElapsedMilliseconds }
        Start-Sleep -Milliseconds 250
    }
    return -1
}

function Get-MainWindow([System.Diagnostics.Process]$Process) {
    foreach ($handle in [DevSmokeWin32]::TopWindows($Process.Id)) {
        if ([DevSmokeWin32]::Title($handle) -like "Gecko Peeks*") { return $handle }
    }
    return [IntPtr]::Zero
}

function Test-WindowShown([System.Diagnostics.Process]$Process) {
    $handle = Get-MainWindow $Process
    return ($handle -ne [IntPtr]::Zero) -and [DevSmokeWin32]::IsWindowVisible($handle) -and -not [DevSmokeWin32]::IsIconic($handle)
}

# --- The web page, read through UI Automation. Chromium builds its accessibility tree when a
# UI Automation client asks, so every read waits for it. Hidden pages are not in the tree.
$uiaElement = [System.Windows.Automation.AutomationElement]
$uiaScope = [System.Windows.Automation.TreeScope]
$uiaType = [System.Windows.Automation.ControlType]

function Get-WebDocument([IntPtr]$Form) {
    if ($Form -eq [IntPtr]::Zero) { return $null }
    $condition = New-Object System.Windows.Automation.PropertyCondition($uiaElement::ControlTypeProperty, $uiaType::Document)
    return $uiaElement::FromHandle($Form).FindFirst($uiaScope::Descendants, $condition)
}

# FindFirst searches the control view only. Some elements (the <code> log path) are in the raw
# view only, so a miss walks the raw view.
function Find-WebRaw($Element, [string]$Id, [int]$Depth) {
    $walker = [System.Windows.Automation.TreeWalker]::RawViewWalker
    $child = $walker.GetFirstChild($Element)
    while ($null -ne $child) {
        if ($child.Current.AutomationId -ceq $Id) { return $child }
        if ($Depth -lt 30) {
            $found = Find-WebRaw $child $Id ($Depth + 1)
            if ($null -ne $found) { return $found }
        }
        $child = $walker.GetNextSibling($child)
    }
    return $null
}

function Find-WebId($Document, [string]$Id) {
    if ($null -eq $Document) { return $null }
    $condition = New-Object System.Windows.Automation.PropertyCondition($uiaElement::AutomationIdProperty, $Id)
    $found = $Document.FindFirst($uiaScope::Descendants, $condition)
    if ($null -ne $found) { return $found }
    return Find-WebRaw $Document $Id 0
}

function Find-WebNamed($Document, $ControlType, [string]$Name) {
    if ($null -eq $Document) { return $null }
    $condition = New-Object System.Windows.Automation.AndCondition(
        (New-Object System.Windows.Automation.PropertyCondition($uiaElement::ControlTypeProperty, $ControlType)),
        (New-Object System.Windows.Automation.PropertyCondition($uiaElement::NameProperty, $Name)))
    return $Document.FindFirst($uiaScope::Descendants, $condition)
}

# The words inside an element: the names of its leaves, joined. The raw view also holds text
# that the control view leaves out (the text inside <code>).
function Get-WebLeaves($Element, [System.Collections.Generic.List[string]]$Into, [int]$Depth) {
    $walker = [System.Windows.Automation.TreeWalker]::RawViewWalker
    $child = $walker.GetFirstChild($Element)
    if ($null -eq $child) {
        if ($Element.Current.Name) { $Into.Add($Element.Current.Name) }
        return
    }
    while ($null -ne $child -and $Depth -lt 30) {
        Get-WebLeaves $child $Into ($Depth + 1)
        $child = $walker.GetNextSibling($child)
    }
}

function Get-WebText($Element) {
    if ($null -eq $Element) { return "" }
    $parts = New-Object System.Collections.Generic.List[string]
    Get-WebLeaves $Element $parts 0
    if ($parts.Count -eq 0) { return [string]$Element.Current.Name }
    return (($parts -join "") -replace "\s+", " ").Trim()
}

function Get-WebTexts($Document) {
    if ($null -eq $Document) { return @() }
    return @($Document.FindAll($uiaScope::Descendants, [System.Windows.Automation.Condition]::TrueCondition) | ForEach-Object { $_.Current.Name } | Where-Object { $_ })
}

function Invoke-WebElement($Element) {
    if ($null -eq $Element) { return $false }
    $Element.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
    return $true
}

# Sidebar link by name; the page title follows the hash route ("Gecko Peeks · Settings").
function Open-WebPage([IntPtr]$Form, [string]$Page) {
    $document = Get-WebDocument $Form
    if (-not (Invoke-WebElement (Find-WebNamed $document ($uiaType::Hyperlink) $Page))) { return -1 }
    return Wait-Until { (Get-WebDocument $Form).Current.Name -like "*$Page" } 5000
}

# Below the caption bar, on a 4 px grid: share of pixels off the page background (#0f0f10),
# share of white pixels, and the number of distinct colours (4 bits a channel).
function Measure-Bitmap([System.Drawing.Bitmap]$Bitmap) {
    $samples = 0; $ink = 0; $white = 0
    $colours = @{}
    for ($y = [Math]::Min(48, $Bitmap.Height - 1); $y -lt $Bitmap.Height; $y += 4) {
        for ($x = 0; $x -lt $Bitmap.Width; $x += 4) {
            $pixel = $Bitmap.GetPixel($x, $y)
            $samples++
            if (([Math]::Abs($pixel.R - 0x0f) + [Math]::Abs($pixel.G - 0x0f) + [Math]::Abs($pixel.B - 0x10)) -gt 40) { $ink++ }
            if ($pixel.R -gt 235 -and $pixel.G -gt 235 -and $pixel.B -gt 235) { $white++ }
            $colours[(($pixel.R -shr 4) -shl 8) -bor (($pixel.G -shr 4) -shl 4) -bor ($pixel.B -shr 4)] = $true
        }
    }
    [pscustomobject]@{
        InkShare = if ($samples) { $ink / $samples } else { 0 }
        WhiteShare = if ($samples) { $white / $samples } else { 0 }
        Colours = $colours.Count
    }
}

# A render of the window through DWM, taken again from the screen when it comes back flat.
# A blank or failed WebView2 is one flat colour; a white flash is mostly white.
function Measure-WindowRender([IntPtr]$Form, [string]$SaveAs) {
    $rect = New-Object DevSmokeWin32+RECT
    if (-not [DevSmokeWin32]::GetWindowRect($Form, [ref]$rect)) { return $null }
    $width = $rect.Right - $rect.Left
    $height = $rect.Bottom - $rect.Top
    if ($width -le 0 -or $height -le 0) { return $null }
    $bitmap = New-Object System.Drawing.Bitmap $width, $height
    try {
        $graphics = [System.Drawing.Graphics]::FromImage($bitmap)
        $hdc = $graphics.GetHdc()
        $printed = [DevSmokeWin32]::PrintWindow($Form, $hdc, 2)
        $graphics.ReleaseHdc($hdc)
        $how = "PrintWindow"
        $stats = Measure-Bitmap $bitmap
        if (-not $printed -or $stats.Colours -lt 16) {
            $graphics.CopyFromScreen($rect.Left, $rect.Top, 0, 0, $bitmap.Size)
            $how = "screen copy"
            $stats = Measure-Bitmap $bitmap
        }
        $graphics.Dispose()
        if ($SaveAs) { $bitmap.Save($SaveAs, [System.Drawing.Imaging.ImageFormat]::Png) }
        return [pscustomobject]@{
            How = $how
            Size = "${width}x${height}"
            InkShare = $stats.InkShare
            WhiteShare = $stats.WhiteShare
            Colours = $stats.Colours
        }
    }
    finally {
        $bitmap.Dispose()
    }
}

$checks = New-Object System.Collections.Generic.List[string]
$failures = New-Object System.Collections.Generic.List[string]
function Check([string]$Name, [bool]$Ok, [string]$Detail = "") {
    $status = if ($Ok) { "pass" } else { "FAIL" }
    $checks.Add("[$status] $Name $Detail".Trim())
    if (-not $Ok) { $failures.Add("$Name $Detail".Trim()) }
    Write-Host "[$status] $Name $Detail"
}

$beforeRun = Get-RunValue
$beforeDocs = Get-TreeStamp $productionLog
$beforeApp = Get-TreeStamp $productionAppData
$beforeSkip = if (Test-Path -LiteralPath $productionSkip) { (Get-FileHash -LiteralPath $productionSkip -Algorithm SHA256).Hash } else { "missing" }
$beforeProductionCount = Get-ProductionProcessCount

New-Item -ItemType Directory -Force -Path $scratch | Out-Null
$env:GECKO_PEEKS_DEV_ROOT = $scratch
$launchClock = [System.Diagnostics.Stopwatch]::StartNew()
$started = Start-Process -FilePath $resolved -WorkingDirectory $scratch -PassThru
# Caching the handle keeps ExitCode readable after the process ends.
$null = $started.Handle

try {
    $shownMs = Wait-Until { Test-WindowShown $started } 30000
    Check "Dev window shows on launch" ($shownMs -ge 0) "$shownMs ms"
    $form = Get-MainWindow $started
    $title = [DevSmokeWin32]::Title($form)
    if ($ExpectVersion) {
        Check "Title shows v$ExpectVersion" ($title -ceq "Gecko Peeks v$ExpectVersion") $title
    } else {
        Check "Title shows a version" ($title -cmatch '^Gecko Peeks v\d+\.\d+\.\d+$') $title
    }

    $pageMs = Wait-Until { (Get-WebText (Find-WebId (Get-WebDocument $form) "h-overview")) -eq "Route to Riot" } 45000
    $pageDetail = "$pageMs ms after the window showed"
    if ($pageMs -lt 0) {
        # The WinForms fallback panel says why: no WebView2 Runtime, or the web view failed.
        $fallback = @($uiaElement::FromHandle($form).FindAll($uiaScope::Descendants, [System.Windows.Automation.Condition]::TrueCondition) | ForEach-Object { $_.Current.Name } | Where-Object { $_ -match "WebView2|web view" })
        $pageDetail = if ($fallback.Count) { "fallback panel: $($fallback -join ' ')" } else { "no heading and no fallback panel" }
    }
    Check "WebView2 page renders the Overview heading" ($pageMs -ge 0) $pageDetail
    $document = Get-WebDocument $form
    $missing = @("Overview", "Route", "History", "Internet", "Settings" | Where-Object { $null -eq (Find-WebNamed $document ($uiaType::Hyperlink) $_) })
    Check "Sidebar shows the five pages" ($missing.Count -eq 0) $(if ($missing.Count) { "missing: $($missing -join ', ')" } else { "" })

    Start-Sleep -Seconds 1
    $render = Measure-WindowRender $form (Join-Path $OutDir "smoke-dev-window.png")
    if ($null -eq $render) {
        Check "Window render is not blank" $false "could not render the window"
    } else {
        $detail = "{0} by {1}, {2:P1} ink, {3:P1} white, {4} colours" -f $render.Size, $render.How, $render.InkShare, $render.WhiteShare, $render.Colours
        Check "Window render is not blank" ($render.InkShare -ge 0.02 -and $render.Colours -ge 16) $detail
        Check "Window render is not a white page" ($render.WhiteShare -lt 0.5) $detail
    }

    $runPressed = Invoke-WebElement (Find-WebId (Get-WebDocument $form) "btnRunTop")
    $toastMs = Wait-Until { (Get-WebText (Find-WebId (Get-WebDocument $form) "toasts")) -match "Tests do not run in the Dev build" } 5000
    Check "Run test now answers with the Dev toast" ($runPressed -and $toastMs -ge 0) "$toastMs ms"

    $settingsMs = Open-WebPage $form "Settings"
    Check "Settings page opens from the sidebar" ($settingsMs -ge 0 -and (Get-WebText (Find-WebId (Get-WebDocument $form) "h-settings")) -eq "Settings") "$settingsMs ms"
    $null = Wait-Until { (Get-WebText (Find-WebId (Get-WebDocument $form) "logPath")) -like "*.csv" } 5000
    $logPath = Get-WebText (Find-WebId (Get-WebDocument $form) "logPath")
    Check "Settings shows a log path under the dev root" ($logPath -and (Test-IsUnder $logPath $scratch)) $logPath
    (Get-WebTexts (Get-WebDocument $form)) -join "`n" | Out-File -FilePath (Join-Path $OutDir "smoke-dev-settings-text.txt") -Encoding utf8
}
catch {
    Check "Window checks finished" $false "$($_.Exception.Message) (line $($_.InvocationInfo.ScriptLineNumber))"
}

$remaining = 15000 - [int]$launchClock.ElapsedMilliseconds
if ($remaining -gt 0) { Start-Sleep -Milliseconds $remaining }
$started.Refresh()
$processName = $started.ProcessName
$exited = $started.HasExited
$exitCode = $null
if ($exited) { $exitCode = $started.ExitCode }

Get-Process -ErrorAction SilentlyContinue | Where-Object { $_.ProcessName -eq "GeckoPeeksDev" } | ForEach-Object {
    Stop-Process -Id $_.Id -Force -ErrorAction SilentlyContinue
}
if ($processName -eq "GeckoPeeks") {
    Stop-Process -Id $started.Id -Force -ErrorAction SilentlyContinue
}
# The WebView2 browser processes of the dev profile exit once their host is gone.
$webViewLeft = Wait-Until {
    @(Get-CimInstance Win32_Process -Filter "Name='msedgewebview2.exe'" -ErrorAction SilentlyContinue | Where-Object { $_.CommandLine -and $_.CommandLine.IndexOf($scratch, [System.StringComparison]::OrdinalIgnoreCase) -ge 0 }).Count -eq 0
} 15000
Check "Dev WebView2 processes exit with the app" ($webViewLeft -ge 0) "$webViewLeft ms"

$afterRun = Get-RunValue
$afterDocs = Get-TreeStamp $productionLog
$afterApp = Get-TreeStamp $productionAppData
$afterSkip = if (Test-Path -LiteralPath $productionSkip) { (Get-FileHash -LiteralPath $productionSkip -Algorithm SHA256).Hash } else { "missing" }
$afterProductionCount = Get-ProductionProcessCount

if ($beforeRun -ne $afterRun) { $failures.Add("HKCU Run value changed.") }
if ($beforeDocs -ne $afterDocs) { $failures.Add("Documents log folder changed.") }
if ($beforeApp -ne $afterApp) { $failures.Add("Production AppData folder changed.") }
if ($beforeSkip -ne $afterSkip) { $failures.Add("Skip file changed.") }
if ($beforeProductionCount -ne $afterProductionCount) { $failures.Add("Production process count changed.") }
if ($processName -ne $devName) { $failures.Add("Launched process name was '$processName'.") }
if ($exited -and $exitCode -ne 0) { $failures.Add("Dev process exited with code $exitCode.") }
if ($exited) { $failures.Add("Dev process exited before the 15 s mark.") }

$summary = "process=$processName exited=$exited code=$exitCode checks=$($checks.Count) failures=$($failures.Count)"
(@($summary) + $checks) | Out-File -FilePath (Join-Path $OutDir "smoke-dev-summary.txt") -Encoding utf8
if ($failures.Count -gt 0) {
    $failures | Out-File -FilePath (Join-Path $OutDir "smoke-dev-failures.txt") -Encoding utf8
    throw ($failures -join " ")
}

Write-Host "Dev smoke passed. $summary"
