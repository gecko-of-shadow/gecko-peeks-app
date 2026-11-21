<#
Launches the published GeckoPeeks.exe on a GitHub Windows runner and checks that it opens,
stays open, closes to the tray, reopens from the tray icon and from a second launch, starts
hidden with --tray, repoints a stale Start with Windows value, and logs no crash.

The v2 window shows its pages in WebView2. The script reads that page through UI Automation:
the title carries the version, the page renders (a render of the window is not blank and every
page shows its heading), the Logging chip and the Settings log path and status line read as
expected, and Run test now, Copy path and Open folder work from the page.

Then it denies writes to Documents\RiotRouteLog the way Controlled folder access does and checks
that the log moves to %LOCALAPPDATA%\GeckoPeeks\RiotRouteLog with its rows, and that the page
says Blocked when neither folder accepts writes. Timings, page text, results and screenshots go
to the job summary and to -OutDir.

CI only: it rewrites the HKCU Run value, adds and removes deny ACLs on the log folders, and
kills every GeckoPeeks process and the WebView2 processes of the GeckoPeeks profile.
#>
param(
    [Parameter(Mandatory = $true)][string]$Exe,
    [Parameter(Mandatory = $true)][string]$ExpectVersion,
    [string]$OutDir = "smoke"
)

$ErrorActionPreference = "Stop"
if ($env:GITHUB_ACTIONS -ne "true") {
    throw "smoke-exe.ps1 rewrites HKCU Run, changes folder ACLs and kills GeckoPeeks processes; run it on a CI runner only."
}

Add-Type -AssemblyName System.Drawing
Add-Type -AssemblyName System.Windows.Forms
Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes
Add-Type -TypeDefinition @"
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;

public static class SmokeWin32
{
    private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential)]
    public struct RECT { public int Left; public int Top; public int Right; public int Bottom; }

    [DllImport("user32.dll")] private static extern bool EnumWindows(EnumWindowsProc callback, IntPtr lParam);
    [DllImport("user32.dll")] private static extern bool EnumChildWindows(IntPtr parent, EnumWindowsProc callback, IntPtr lParam);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowText(IntPtr hWnd, StringBuilder text, int max);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassName(IntPtr hWnd, StringBuilder text, int max);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr SendMessageTimeout(IntPtr hWnd, uint msg, IntPtr wParam, StringBuilder lParam, uint flags, uint timeoutMs, out IntPtr result);
    [DllImport("user32.dll")] private static extern IntPtr SendMessageTimeout(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam, uint flags, uint timeoutMs, out IntPtr result);
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr hWnd, out RECT rect);
    [DllImport("user32.dll")] public static extern bool PrintWindow(IntPtr hWnd, IntPtr hdc, uint flags);
    [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr hWnd);
    [DllImport("user32.dll")] public static extern bool IsIconic(IntPtr hWnd);
    [DllImport("user32.dll")] public static extern bool PostMessage(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);
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

    public static List<IntPtr> Descendants(IntPtr parent)
    {
        var found = new List<IntPtr>();
        EnumChildWindows(parent, (hWnd, _) =>
        {
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

    // GetWindowText does not read the controls of another process; WM_GETTEXT does.
    public static string TextOf(IntPtr hWnd)
    {
        const uint WM_GETTEXT = 0x000D;
        const uint WM_GETTEXTLENGTH = 0x000E;
        const uint SMTO_ABORTIFHUNG = 0x0002;
        IntPtr length;
        if (hWnd == IntPtr.Zero
            || SendMessageTimeout(hWnd, WM_GETTEXTLENGTH, IntPtr.Zero, IntPtr.Zero, SMTO_ABORTIFHUNG, 3000, out length) == IntPtr.Zero)
            return "";
        var text = new StringBuilder(length.ToInt32() + 2);
        IntPtr copied;
        SendMessageTimeout(hWnd, WM_GETTEXT, (IntPtr)text.Capacity, text, SMTO_ABORTIFHUNG, 3000, out copied);
        return text.ToString();
    }

    public static string ClassOf(IntPtr hWnd)
    {
        var text = new StringBuilder(256);
        GetClassName(hWnd, text, text.Capacity);
        return text.ToString();
    }
}
"@
# Window rectangles, window renders and UI Automation rectangles then share one pixel scale.
[void][SmokeWin32]::SetProcessDPIAware()

$WM_CLOSE = 0x0010
$WM_SYSCOMMAND = 0x0112
$SC_CLOSE = 0xF060
# WinForms NotifyIcon callback message (WM_USER + 1024); lParam carries the mouse message.
$WM_TRAYMOUSEMESSAGE = 0x0800
$WM_LBUTTONDOWN = 0x0201
$WM_LBUTTONUP = 0x0202
$WM_LBUTTONDBLCLK = 0x0203
$WM_RBUTTONUP = 0x0205
# PrintWindow: render through DWM so windows on top do not matter.
$PW_RENDERFULLCONTENT = 2

# app.css tones. The chip dot carries the tone; a bad chip also has its text in that colour.
$tones = [ordered]@{ Good = @(0x34, 0xd3, 0x99); Warn = @(0xfb, 0xbf, 0x24); Bad = @(0xf8, 0x71, 0x71) }
$pages = [ordered]@{ Overview = @("h-overview", "Route to Riot"); Route = @("h-route", "Route"); History = @("h-history", "History"); Internet = @("h-internet", "Internet"); Settings = @("h-settings", "Settings") }

$results = [System.Collections.Generic.List[object]]::new()
$failures = [System.Collections.Generic.List[string]]::new()
$denied = [System.Collections.Generic.List[string]]::new()
$readFailures = [System.Collections.Generic.HashSet[string]]::new([System.StringComparer]::OrdinalIgnoreCase)
$userSid = [System.Security.Principal.WindowsIdentity]::GetCurrent().User.Value

# The page shows the chip as "Logging · on" (v2.1); older builds said "On".
function ChipIs([string]$chip, [string]$state) {
    $c = ($chip -replace '^\s*Logging\s*[\u00B7\-:]\s*', '').Trim()
    return $c -ieq $state
}

function Check([string]$Name, [bool]$Ok, [string]$Detail = "") {
    $status = if ($Ok) { "pass" } else { "FAIL" }
    $results.Add([pscustomobject]@{ Check = $Name; Result = $status; Detail = $Detail })
    if (-not $Ok) { $failures.Add("$Name $Detail".Trim()) }
    Write-Host "[$status] $Name $Detail"
}

function Info([string]$Name, [string]$Detail) {
    $results.Add([pscustomobject]@{ Check = $Name; Result = "info"; Detail = $Detail })
    Write-Host "[info] $Name $Detail"
}

# Also an annotation on the run, readable without downloading the artifact.
function Notice([string]$Name, [string]$Detail) {
    Info $Name $Detail
    $message = $Detail -replace '%', '%25' -replace "`r", '%0D' -replace "`n", '%0A'
    Write-Host "::notice title=Smoke ${Name}::$message"
}

function MainWindow([System.Diagnostics.Process]$Process) {
    foreach ($handle in [SmokeWin32]::TopWindows($Process.Id)) {
        if ([SmokeWin32]::Title($handle) -like "Gecko Peeks*") { return $handle }
    }
    return [IntPtr]::Zero
}

function WindowShown([System.Diagnostics.Process]$Process) {
    $handle = MainWindow $Process
    return ($handle -ne [IntPtr]::Zero) -and [SmokeWin32]::IsWindowVisible($handle) -and -not [SmokeWin32]::IsIconic($handle)
}

function WaitUntil([scriptblock]$Condition, [int]$TimeoutMs) {
    $clock = [System.Diagnostics.Stopwatch]::StartNew()
    while ($clock.ElapsedMilliseconds -lt $TimeoutMs) {
        $met = try { & $Condition } catch { $false }
        if ($met) { return [int]$clock.ElapsedMilliseconds }
        Start-Sleep -Milliseconds 200
    }
    return -1
}

function Launch([string[]]$Arguments = @()) {
    $process = if ($Arguments.Count -gt 0) {
        Start-Process -FilePath $Exe -ArgumentList $Arguments -PassThru
    } else {
        Start-Process -FilePath $Exe -PassThru
    }
    # Caching the handle keeps ExitCode readable after the process ends.
    $null = $process.Handle
    return $process
}

function CloseWithX([System.Diagnostics.Process]$Process) {
    $handle = MainWindow $Process
    if ($handle -ne [IntPtr]::Zero) {
        [void][SmokeWin32]::PostMessage($handle, $WM_SYSCOMMAND, [IntPtr]$SC_CLOSE, [IntPtr]::Zero)
    }
    return WaitUntil { -not (WindowShown $Process) } 5000
}

# The NotifyIcon listens on a hidden top-level WinForms window. Other hidden WinForms windows
# ignore this message, so every hidden one gets it.
function TrayMouse([System.Diagnostics.Process]$Process, [int[]]$MouseMessages) {
    $targets = @([SmokeWin32]::TopWindows($Process.Id) | Where-Object {
        -not [SmokeWin32]::IsWindowVisible($_) -and [SmokeWin32]::ClassOf($_) -like "WindowsForms10.*"
    })
    foreach ($message in $MouseMessages) {
        foreach ($handle in $targets) {
            [void][SmokeWin32]::PostMessage($handle, $WM_TRAYMOUSEMESSAGE, [IntPtr]::Zero, [IntPtr]$message)
        }
        Start-Sleep -Milliseconds 100
    }
    return $targets.Count
}

# --- The web page, read through UI Automation. Chromium builds its accessibility tree when a
# UI Automation client asks, so every read waits for it. Hidden pages are not in the tree.
$uiaElement = [System.Windows.Automation.AutomationElement]
$uiaScope = [System.Windows.Automation.TreeScope]
$uiaType = [System.Windows.Automation.ControlType]

# The page re-renders while a test runs, so an element can go away during a read. A read that
# throws is tried once more, then gives its empty value: one bad read fails one check only.
function Try2([scriptblock]$Read, $Empty) {
    for ($attempt = 0; $attempt -lt 2; $attempt++) {
        try { return (& $Read) } catch { Start-Sleep -Milliseconds 150 }
    }
    return $Empty
}

# The page document, found from the window. When that search comes back empty, it is looked
# for again from the Chromium render window (Chrome_RenderWidgetHostHWND) itself.
function WebDocument([IntPtr]$Form) {
    if ($Form -eq [IntPtr]::Zero) { return $null }
    return Try2 {
        $condition = New-Object System.Windows.Automation.PropertyCondition($uiaElement::ControlTypeProperty, $uiaType::Document)
        $found = $uiaElement::FromHandle($Form).FindFirst($uiaScope::Descendants, $condition)
        if ($null -eq $found) {
            foreach ($handle in [SmokeWin32]::Descendants($Form)) {
                if ([SmokeWin32]::ClassOf($handle) -ceq "Chrome_RenderWidgetHostHWND") {
                    $found = $uiaElement::FromHandle($handle).FindFirst($uiaScope::Subtree, $condition)
                    if ($null -ne $found) { break }
                }
            }
        }
        $found
    } $null
}

# FindFirst searches the control view only. Some elements (the <code> log path) are in the raw
# view only, so a miss walks the raw view.
function WebRaw($Element, [string]$Id, [int]$Depth) {
    $walker = [System.Windows.Automation.TreeWalker]::RawViewWalker
    $child = $walker.GetFirstChild($Element)
    while ($null -ne $child) {
        if ($child.Current.AutomationId -ceq $Id) { return $child }
        if ($Depth -lt 30) {
            $found = WebRaw $child $Id ($Depth + 1)
            if ($null -ne $found) { return $found }
        }
        $child = $walker.GetNextSibling($child)
    }
    return $null
}

function WebId($Document, [string]$Id) {
    if ($null -eq $Document) { return $null }
    return Try2 {
        $condition = New-Object System.Windows.Automation.PropertyCondition($uiaElement::AutomationIdProperty, $Id)
        $found = $Document.FindFirst($uiaScope::Descendants, $condition)
        if ($null -eq $found) { $found = WebRaw $Document $Id 0 }
        $found
    } $null
}

function WebNamed($Document, $ControlType, [string]$Name) {
    if ($null -eq $Document) { return $null }
    return Try2 {
        $condition = New-Object System.Windows.Automation.AndCondition(
            (New-Object System.Windows.Automation.PropertyCondition($uiaElement::ControlTypeProperty, $ControlType)),
            (New-Object System.Windows.Automation.PropertyCondition($uiaElement::NameProperty, $Name)))
        $Document.FindFirst($uiaScope::Descendants, $condition)
    } $null
}

# The words inside an element: the names of its leaves, joined. The raw view also holds text
# that the control view leaves out (the text inside <code>).
function WebLeaves($Element, [System.Collections.Generic.List[string]]$Into, [int]$Depth) {
    $walker = [System.Windows.Automation.TreeWalker]::RawViewWalker
    $child = $walker.GetFirstChild($Element)
    if ($null -eq $child) {
        if ($Element.Current.Name) { $Into.Add($Element.Current.Name) }
        return
    }
    while ($null -ne $child -and $Depth -lt 30) {
        WebLeaves $child $Into ($Depth + 1)
        $child = $walker.GetNextSibling($child)
    }
}

function WebText($Element) {
    if ($null -eq $Element) { return "" }
    return [string](Try2 {
        $parts = [System.Collections.Generic.List[string]]::new()
        WebLeaves $Element $parts 0
        if ($parts.Count -eq 0) { [string]$Element.Current.Name } else { (($parts -join "") -replace "\s+", " ").Trim() }
    } "")
}

# Every leaf text of the page, one per line, for the evidence files.
function WebTexts([IntPtr]$Form) {
    $document = WebDocument $Form
    if ($null -eq $document) { return @() }
    return @(Try2 {
        $parts = [System.Collections.Generic.List[string]]::new()
        WebLeaves $document $parts 0
        $parts.ToArray()
    } @())
}

# A disabled button throws on Invoke, so this waits for it to be enabled first.
function WebInvoke($Element, [int]$EnabledWithinMs = 5000) {
    if ($null -eq $Element) { return $false }
    if ((WaitUntil { $Element.Current.IsEnabled } $EnabledWithinMs) -lt 0) { return $false }
    try {
        $Element.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
        return $true
    }
    catch {
        return $false
    }
}

function WebHeading([IntPtr]$Form, [string]$Page) {
    return WebText (WebId (WebDocument $Form) $pages[$Page][0])
}

# The page is up once the Overview heading reads. When it never does, the WinForms fallback
# panel (no WebView2 Runtime, or the web view failed) says why.
function WaitForPage([IntPtr]$Form, [int]$TimeoutMs) {
    $ms = WaitUntil { (WebHeading $Form "Overview") -eq $pages["Overview"][1] } $TimeoutMs
    $fallback = @([SmokeWin32]::Descendants($Form) | ForEach-Object { [SmokeWin32]::TextOf($_) } | Where-Object { $_ -match "WebView2|web view" })
    return [pscustomobject]@{ Ms = $ms; Fallback = ($fallback -join " ") }
}

# Sidebar link by name (the links keep their names when the sidebar collapses). The document
# title follows the hash route ("Gecko Peeks · Settings"), then the heading must read.
function OpenPage([IntPtr]$Form, [string]$Page) {
    if (-not (WebInvoke (WebNamed (WebDocument $Form) ($uiaType::Hyperlink) $Page))) { return -1 }
    return WaitUntil { ((WebDocument $Form).Current.Name -like "*$Page") -and ((WebHeading $Form $Page) -eq $pages[$Page][1]) } 8000
}

# The top bar chip and, on the Settings page, the log path and the status line.
function LogView([IntPtr]$Form) {
    $document = WebDocument $Form
    $chip = WebId $document "chipLog"
    [pscustomobject]@{
        ChipElement = $chip
        Chip = WebText $chip
        File = WebText (WebId $document "logPath")
        Status = WebText (WebId $document "logStatus")
    }
}

function SaveTexts([string]$Name, [IntPtr]$Form) {
    $texts = WebTexts $Form
    ($texts -join "`n") | Out-File -FilePath (Join-Path $OutDir $Name) -Encoding utf8
    return $texts
}

function WebToast([IntPtr]$Form) {
    return WebText (WebId (WebDocument $Form) "toasts")
}

function RectOf([IntPtr]$Handle) {
    $rect = New-Object SmokeWin32+RECT
    if ($Handle -eq [IntPtr]::Zero -or -not [SmokeWin32]::GetWindowRect($Handle, [ref]$rect)) { return $null }
    return $rect
}

# One render of the whole window with the form's screen rectangle, which maps page rectangles
# onto the bitmap. PrintWindow goes through DWM, so windows on top do not matter. A render
# that comes back flat is taken again from the screen.
function FormBitmap([IntPtr]$Form) {
    $formRect = RectOf $Form
    if ($null -eq $formRect) { return $null }
    $width = $formRect.Right - $formRect.Left
    $height = $formRect.Bottom - $formRect.Top
    if ($width -le 0 -or $height -le 0) { return $null }
    $bitmap = New-Object System.Drawing.Bitmap $width, $height
    $graphics = [System.Drawing.Graphics]::FromImage($bitmap)
    $hdc = $graphics.GetHdc()
    $printed = [SmokeWin32]::PrintWindow($Form, $hdc, $PW_RENDERFULLCONTENT)
    $graphics.ReleaseHdc($hdc)
    $how = "PrintWindow"
    if (-not $printed -or (RenderStats $bitmap).Colours -lt 16) {
        $graphics.CopyFromScreen($formRect.Left, $formRect.Top, 0, 0, $bitmap.Size)
        $how = "screen copy"
    }
    $graphics.Dispose()
    return [pscustomobject]@{ Bitmap = $bitmap; Rect = $formRect; How = $how }
}

# Below the caption bar, on a 4 px grid: share of pixels off the page background (#0f0f10),
# share of white pixels, and the number of distinct colours (4 bits a channel).
function RenderStats([System.Drawing.Bitmap]$Bitmap) {
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

# A blank or failed WebView2 is one flat colour; a white flash is mostly white.
function RenderCheck([IntPtr]$Form, [string]$Name, [string]$SaveAs) {
    $capture = FormBitmap $Form
    if ($null -eq $capture) {
        Check "$Name is not blank" $false "could not render the window"
        return
    }
    try {
        $capture.Bitmap.Save((Join-Path $OutDir $SaveAs), [System.Drawing.Imaging.ImageFormat]::Png)
        $stats = RenderStats $capture.Bitmap
        $detail = "{0}x{1} by {2}, {3:P1} ink, {4:P1} white, {5} colours, saved as {6}" -f $capture.Bitmap.Width, $capture.Bitmap.Height, $capture.How, $stats.InkShare, $stats.WhiteShare, $stats.Colours, $SaveAs
        Check "$Name is not blank" ($stats.InkShare -ge 0.02 -and $stats.Colours -ge 16) $detail
        Check "$Name is not a white page" ($stats.WhiteShare -lt 0.5) $detail
    }
    finally {
        $capture.Bitmap.Dispose()
    }
}

# The tone colour with the most pixels inside the chip. "Unsure" when no tone has at least
# 4 pixels and twice the pixels of the next one.
function ChipTone([IntPtr]$Form, $Chip) {
    if ($null -eq $Chip) { return "" }
    $capture = $null
    try {
        $box = $Chip.Current.BoundingRectangle
        if ($box.IsEmpty -or $box.Width -le 0 -or $box.Height -le 0) { return "" }
        $capture = FormBitmap $Form
        if ($null -eq $capture) { return "" }
        $bitmap = $capture.Bitmap
        $left = [Math]::Max(0, [int]$box.Left - $capture.Rect.Left)
        $top = [Math]::Max(0, [int]$box.Top - $capture.Rect.Top)
        $right = [Math]::Min($bitmap.Width, [int]$box.Right - $capture.Rect.Left)
        $bottom = [Math]::Min($bitmap.Height, [int]$box.Bottom - $capture.Rect.Top)
        $counts = [ordered]@{}
        foreach ($name in $tones.Keys) { $counts[$name] = 0 }
        for ($y = $top; $y -lt $bottom; $y++) {
            for ($x = $left; $x -lt $right; $x++) {
                $pixel = $bitmap.GetPixel($x, $y)
                foreach ($name in $tones.Keys) {
                    $tone = $tones[$name]
                    if (([Math]::Abs($pixel.R - $tone[0]) + [Math]::Abs($pixel.G - $tone[1]) + [Math]::Abs($pixel.B - $tone[2])) -lt 60) { $counts[$name]++ }
                }
            }
        }
        $ranked = @($counts.GetEnumerator() | Sort-Object Value -Descending)
        $nearest = if ($ranked[0].Value -ge 4 -and $ranked[0].Value -ge 2 * $ranked[1].Value) { $ranked[0].Key } else { "Unsure" }
        return "$nearest (" + (($counts.GetEnumerator() | ForEach-Object { "$($_.Key) $($_.Value) px" }) -join ", ") + ")"
    }
    catch {
        return ""
    }
    finally {
        if ($null -ne $capture) { $capture.Bitmap.Dispose() }
    }
}

function ExplorerFolders {
    try {
        $shell = New-Object -ComObject Shell.Application
        return @($shell.Windows() | ForEach-Object { try { $_.Document.Folder.Self.Path } catch { } } | Where-Object { $_ })
    }
    catch {
        return @()
    }
}

function ExplorerTitled([string]$Name) {
    foreach ($process in @(Get-Process -Name explorer -ErrorAction SilentlyContinue)) {
        foreach ($handle in [SmokeWin32]::TopWindows($process.Id)) {
            if ([SmokeWin32]::IsWindowVisible($handle) -and [SmokeWin32]::Title($handle) -eq $Name) { return $true }
        }
    }
    return $false
}

function CloseExplorerAt([string]$Folder) {
    try {
        $shell = New-Object -ComObject Shell.Application
        foreach ($window in @($shell.Windows())) {
            try {
                if ($window.Document.Folder.Self.Path -ieq $Folder) { $window.Quit() }
            }
            catch { }
        }
    }
    catch { }
}

# Controlled folder access lets an app read a protected folder but not create, change, rename or
# delete files in it. A deny ACE for this user on those rights is the closest stand-in on a runner.
# DE, not D: icacls's D also carries SYNCHRONIZE, which every file open needs, so it denies reads too.
function DenyWrites([string]$Folder) {
    $null = & icacls $Folder /deny "*${userSid}:(OI)(CI)(WD,AD,WEA,WA,DE,DC)"
    $ok = $LASTEXITCODE -eq 0
    $denied.Add($Folder)
    $ace = @(& icacls $Folder | Where-Object { $_ -match "DENY" } | ForEach-Object { $_.Trim() }) -join " "
    Info "Deny ACE" $ace
    return $ok
}

function RestoreWrites {
    foreach ($folder in $denied) {
        $null = & icacls $folder /remove:d "*$userSid" /T /C /Q
    }
    $denied.Clear()
}

function CanCreateIn([string]$Folder) {
    try {
        $probe = [System.IO.FileStream]::new((Join-Path $Folder "smoke-write-test.tmp"), [System.IO.FileMode]::CreateNew, [System.IO.FileAccess]::Write, [System.IO.FileShare]::None, 1, [System.IO.FileOptions]::DeleteOnClose)
        $probe.Dispose()
        return $true
    }
    catch {
        return $false
    }
}

function Screenshot([string]$Name) {
    try {
        $bounds = [System.Windows.Forms.Screen]::PrimaryScreen.Bounds
        $bitmap = New-Object System.Drawing.Bitmap $bounds.Width, $bounds.Height
        $graphics = [System.Drawing.Graphics]::FromImage($bitmap)
        $graphics.CopyFromScreen($bounds.Location, [System.Drawing.Point]::Empty, $bounds.Size)
        $bitmap.Save((Join-Path $OutDir $Name), [System.Drawing.Imaging.ImageFormat]::Png)
        $graphics.Dispose()
        $bitmap.Dispose()
        Info "Screenshot" "$Name ($($bounds.Width)x$($bounds.Height))"
    }
    catch {
        Info "Screenshot" "$Name not captured: $($_.Exception.Message)"
    }
}

# WebView2 browser processes of the production profile (%LOCALAPPDATA%\GeckoPeeks\WebView2).
function WebViewProcesses {
    return @(Get-CimInstance Win32_Process -Filter "Name='msedgewebview2.exe'" -ErrorAction SilentlyContinue | Where-Object {
        $_.CommandLine -and $_.CommandLine.IndexOf($webViewFolder, [System.StringComparison]::OrdinalIgnoreCase) -ge 0
    })
}

# The next launch reuses the WebView2 profile, so the old browser processes must be gone first.
function StopAll {
    Get-Process -Name GeckoPeeks -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
    if ((WaitUntil { (WebViewProcesses).Count -eq 0 } 10000) -lt 0) {
        WebViewProcesses | ForEach-Object { Stop-Process -Id $_.ProcessId -Force -ErrorAction SilentlyContinue }
    }
    Start-Sleep -Seconds 1
}

function FileLength([string]$Path) {
    if (Test-Path -LiteralPath $Path) { return (Get-Item -LiteralPath $Path).Length }
    return 0
}

# A refused or busy file reads as "" (reported once per path), so one check fails, not the run.
function ReadText([string]$Path) {
    try {
        if (-not (Test-Path -LiteralPath $Path)) { return "" }
        return [string](Get-Content -LiteralPath $Path -Raw -ErrorAction Stop)
    }
    catch {
        if ($readFailures.Add($Path)) { Info "Could not read $Path" $_.Exception.Message }
        return ""
    }
}

function HashOf([string]$Path) {
    try { return (Get-FileHash -LiteralPath $Path -Algorithm SHA256 -ErrorAction Stop).Hash }
    catch { return "unreadable: $($_.Exception.Message)" }
}

function CountOf([string]$Text, [string]$Pattern) {
    return ([regex]::Matches($Text, $Pattern)).Count
}

New-Item -ItemType Directory -Force -Path $OutDir | Out-Null
$OutDir = (Resolve-Path -LiteralPath $OutDir).Path
$Exe = (Resolve-Path -LiteralPath $Exe).Path
$logFolder = Join-Path ([Environment]::GetFolderPath("MyDocuments")) "RiotRouteLog"
$logCsv = Join-Path $logFolder "riot-route-log.csv"
$crashLog = Join-Path $logFolder "crash.log"
$activityLog = Join-Path $logFolder "activity.log"
$appDataFolder = Join-Path ([Environment]::GetFolderPath("LocalApplicationData")) "GeckoPeeks"
$webViewFolder = Join-Path $appDataFolder "WebView2"
$fallbackFolder = Join-Path $appDataFolder "RiotRouteLog"
$fallbackCsv = Join-Path $fallbackFolder "riot-route-log.csv"
$fallbackCrash = Join-Path $fallbackFolder "crash.log"
$fallbackActivity = Join-Path $fallbackFolder "activity.log"
$crashBefore = FileLength $crashLog
$fallbackCrashBefore = FileLength $fallbackCrash
$runKey = "HKCU:\Software\Microsoft\Windows\CurrentVersion\Run"
$colourCalibrated = $false

try {
    StopAll
    Info "Exe" ("{0} ({1:N1} MB)" -f $Exe, ((Get-Item -LiteralPath $Exe).Length / 1MB))
    try {
        Info "WebView2 Runtime" ([string](Get-ItemProperty -Path "HKLM:\SOFTWARE\WOW6432Node\Microsoft\EdgeUpdate\Clients\{F3017226-FE2A-4295-8BDF-00C3A9A7E4C5}" -Name pv -ErrorAction Stop).pv)
    }
    catch {
        Info "WebView2 Runtime" "version not found in the registry"
    }

    # How v1.0.7 wrote it, for an older download: no --tray, different path. Fresh runner
    # profiles have no Run key at all.
    if (-not (Test-Path -LiteralPath $runKey)) { New-Item -Path $runKey -Force | Out-Null }
    Set-ItemProperty -Path $runKey -Name "GeckoPeeks" -Value '"C:\Users\Public\Downloads\GeckoPeeks (old).exe"'

    # --- Manual launch: window opens, the page renders, stays open, closes to the tray,
    # reopens from the tray icon.
    $app = Launch
    $shownMs = WaitUntil { WindowShown $app } 30000
    Check "Window opens on launch (load time)" ($shownMs -ge 0) "$shownMs ms"
    if ($shownMs -lt 0) { throw "The window never appeared." }

    $title = [SmokeWin32]::Title((MainWindow $app))
    Check "Title shows v$ExpectVersion" ($title -ceq "Gecko Peeks v$ExpectVersion") $title

    $runValue = (Get-ItemProperty -Path $runKey -Name "GeckoPeeks" -ErrorAction SilentlyContinue).GeckoPeeks
    Check "Start with Windows value repointed at this exe with --tray" ($runValue -eq "`"$Exe`" --tray") $runValue

    $form = MainWindow $app
    $page = WaitForPage $form 45000
    Check "Web page renders the Overview heading (load time)" ($page.Ms -ge 0) $(if ($page.Fallback) { "fallback panel: $($page.Fallback)" } else { "$($page.Ms) ms after the window opened" })
    if ($page.Ms -lt 0) { throw "The web page never rendered." }
    $document = WebDocument $form
    $missing = @($pages.Keys | Where-Object { $null -eq (WebNamed $document ($uiaType::Hyperlink) $_) })
    Check "Sidebar links all five pages" ($missing.Count -eq 0) $(if ($missing.Count) { "missing: $($missing -join ', ')" } else { "" })
    Start-Sleep -Seconds 1
    RenderCheck $form "Window render" "smoke-window-render.png"

    $steady = 0
    for ($second = 0; $second -lt 20; $second++) {
        Start-Sleep -Seconds 1
        if ($app.HasExited -or -not (WindowShown $app)) { break }
        $steady++
    }
    Check "Window stays open for 20 s while the first test runs" ($steady -eq 20) "$steady s"
    $app.Refresh()
    Check "Window responds to messages" ($app.Responding)
    $webViewMb = ((WebViewProcesses | Measure-Object -Property WorkingSetSize -Sum).Sum) / 1MB
    Info "Memory and CPU after 20 s" ("{0:N0} MB working set, {1:N1} s CPU; WebView2 {2} processes, {3:N0} MB working set" -f ($app.WorkingSet64 / 1MB), $app.TotalProcessorTime.TotalSeconds, (WebViewProcesses).Count, $webViewMb)

    $onMs = WaitUntil { ChipIs (LogView $form).Chip "On" } 10000
    $view = LogView $form
    Check "Logging chip says On" ($onMs -ge 0) $view.Chip
    $tone = ChipTone $form $view.ChipElement
    $colourCalibrated = $tone -like "Good*"
    Info "On chip colour (expected Good)" $(if ($tone) { $tone } else { "no chip pixels captured" })

    foreach ($name in @("Route", "History", "Internet", "Settings")) {
        $openMs = OpenPage $form $name
        Check "$name page opens from the sidebar" ($openMs -ge 0) "$openMs ms, heading '$(WebHeading $form $name)'"
    }

    $null = WaitUntil { (LogView $form).File -like "*.csv" } 10000
    $view = LogView $form
    $texts = SaveTexts "window-text-normal.txt" $form
    Check "Settings shows the Documents CSV" ($view.File -ieq $logCsv) $view.File
    Check "No 'Could not find file' text in the page" (-not ($texts -match "Could not find file"))
    Notice "normal log" "Logging=$($view.Chip) [$tone] | File=$($view.File) | $($view.Status)"
    RenderCheck $form "Settings render" "smoke-settings-render.png"
    Screenshot "smoke-window.png"

    # Run test now from the top bar. The button is disabled while a test runs, so this waits for
    # the first test to end. The scheduler notes the press in activity.log.
    $runsBefore = CountOf (ReadText $activityLog) "Run test now"
    $waitClock = [System.Diagnostics.Stopwatch]::StartNew()
    $pressed = WebInvoke (WebId (WebDocument $form) "btnRunTop") 240000
    $enabledMs = $waitClock.ElapsedMilliseconds
    $runMs = WaitUntil { (CountOf (ReadText $activityLog) "Run test now") -gt $runsBefore } 10000
    Check "Run test now in the page starts a test" ($pressed -and $runMs -ge 0) "pressed: $pressed after $enabledMs ms, noted in $runMs ms"

    $handoff = [System.Diagnostics.Stopwatch]::StartNew()
    $duplicate = Launch
    $duplicateExited = $duplicate.WaitForExit(15000)
    Check "Second launch hands off and exits" ($duplicateExited -and $duplicate.ExitCode -eq 0) "$($handoff.ElapsedMilliseconds) ms"
    Check "First instance keeps running with its window open" ((-not $app.HasExited) -and (WindowShown $app))

    $hiddenMs = CloseWithX $app
    Start-Sleep -Seconds 1
    Check "Close (X) hides to the tray and keeps logging" (($hiddenMs -ge 0) -and -not $app.HasExited) "$hiddenMs ms"

    $targets = TrayMouse $app @($WM_LBUTTONDBLCLK, $WM_LBUTTONUP)
    $doubleMs = WaitUntil { WindowShown $app } 10000
    Check "Tray double-click opens the window" ($doubleMs -ge 0) "$doubleMs ms, $targets hidden windows signalled"
    Start-Sleep -Seconds 5
    Check "Window stays open 5 s after the tray double-click" (WindowShown $app)
    # Hidden for a few seconds only, so the same page (Settings) is still loaded.
    $reshownMs = WaitUntil { (WebHeading (MainWindow $app) "Settings") -eq "Settings" } 10000
    Check "Page is still there after the tray reopen" ($reshownMs -ge 0) "$reshownMs ms"

    $null = CloseWithX $app
    $null = TrayMouse $app @($WM_LBUTTONDOWN, $WM_LBUTTONUP)
    $clickMs = WaitUntil { WindowShown $app } 10000
    Check "Tray single click opens the window" ($clickMs -ge 0) "$clickMs ms"

    try { [System.Windows.Forms.Cursor]::Position = New-Object System.Drawing.Point(900, 700) } catch { }
    $null = TrayMouse $app @($WM_RBUTTONUP)
    Start-Sleep -Milliseconds 1500
    Screenshot "smoke-tray-menu.png"
    StopAll

    # --- Start with Windows launch: hidden in the tray until opened.
    $tray = Launch @("--tray")
    Start-Sleep -Seconds 8
    Check "--tray start stays hidden and keeps running" ((-not $tray.HasExited) -and -not (WindowShown $tray))
    Check "--tray start makes no WebView2 processes" ((WebViewProcesses).Count -eq 0) "$((WebViewProcesses).Count) running"

    $trayForm = MainWindow $tray
    if ($trayForm -ne [IntPtr]::Zero) {
        [void][SmokeWin32]::PostMessage($trayForm, $WM_CLOSE, [IntPtr]::Zero, [IntPtr]::Zero)
    }
    Start-Sleep -Seconds 2
    Check "Outside WM_CLOSE keeps the window alive in the tray" ((-not $tray.HasExited) -and ((MainWindow $tray) -ne [IntPtr]::Zero))

    $reopen = [System.Diagnostics.Stopwatch]::StartNew()
    $opener = Launch
    $openMs = WaitUntil { WindowShown $tray } 15000
    Check "Relaunch opens the running app's window" ($openMs -ge 0) "$($reopen.ElapsedMilliseconds) ms"
    Check "Relaunch process exits" ($opener.WaitForExit(15000))
    Start-Sleep -Seconds 5
    Check "Window stays open 5 s after the relaunch" (WindowShown $tray)
    $page = WaitForPage (MainWindow $tray) 45000
    Check "Web page renders after the relaunch" ($page.Ms -ge 0) $(if ($page.Fallback) { "fallback panel: $($page.Fallback)" } else { "$($page.Ms) ms" })
    Screenshot "smoke-after-relaunch.png"

    $activity = ReadText $activityLog
    Check "Activity log records the first test" ($activity -match "Starting the first test\.")
    $lastLines = ($activity -split "\r?\n" | Where-Object { $_ } | Select-Object -Last 5) -join " / "
    Info "Activity log tail" $lastLines
    StopAll

    # --- Controlled folder access on Documents: reads work, creates and appends are refused.
    Check "No AppData log before Documents is blocked" (-not (Test-Path -LiteralPath $fallbackCsv))
    $header = "Timestamp,LeagueRunning,Upstream,HandoffNetwork,HandoffHost,HandoffIP,HandoffCity,HandoffRttMs,LastHopIP,LastHopHost,LastHopMin,LastHopAvg,LastHopMax,LastHopLossPct,RiotHopAvg,RiotHopLossPct,FullPath"
    $seedTime = (Get-Date).AddMinutes(-30).ToString("yyyy-MM-dd HH:mm:ss", [System.Globalization.CultureInfo]::InvariantCulture)
    $seed = @($seedTime, "N", "SmokeSeed", "", "", "", "", "", "", "", "", "21", "", "", "", "100", "1.1.1.1 > 192.207.0.5") -join ","
    New-Item -ItemType Directory -Force -Path $logFolder | Out-Null
    if ((FileLength $logCsv) -eq 0) {
        [System.IO.File]::WriteAllText($logCsv, "$header`r`n", [System.Text.UTF8Encoding]::new($true))
    }
    [System.IO.File]::AppendAllText($logCsv, "$seed`r`n", [System.Text.UTF8Encoding]::new($false))
    $documentsHash = (Get-FileHash -LiteralPath $logCsv -Algorithm SHA256).Hash
    $documentsRows = @(Get-Content -LiteralPath $logCsv | Where-Object { $_ -and $_ -notmatch "^\uFEFF?Timestamp," }).Count

    Check "Deny ACL set on Documents\RiotRouteLog" (DenyWrites $logFolder)
    $refusesCreate = -not (CanCreateIn $logFolder)
    $stillReads = (ReadText $logCsv) -match "SmokeSeed"
    Check "Documents\RiotRouteLog refuses new files and still reads" ($refusesCreate -and $stillReads) "refuses create: $refusesCreate, reads: $stillReads"

    $cfa = Launch
    $cfaShownMs = WaitUntil { WindowShown $cfa } 30000
    Check "Window opens with Documents blocked" ($cfaShownMs -ge 0) "$cfaShownMs ms"
    $form = MainWindow $cfa
    $page = WaitForPage $form 45000
    Check "Web page renders with Documents blocked" ($page.Ms -ge 0) $(if ($page.Fallback) { "fallback panel: $($page.Fallback)" } else { "$($page.Ms) ms" })
    $settingsMs = OpenPage $form "Settings"
    Check "Settings page opens with Documents blocked" ($settingsMs -ge 0) "$settingsMs ms"
    $movedMs = WaitUntil { (LogView $form).File -ieq $fallbackCsv } 15000
    $view = LogView $form
    $texts = SaveTexts "window-text-cfa.txt" $form
    Check "Settings shows the AppData CSV" ($movedMs -ge 0) $view.File
    $fallbackText = ReadText $fallbackCsv
    $fallbackRows = @($fallbackText -split "\r?\n" | Where-Object { $_ -and $_ -notmatch "^\uFEFF?Timestamp," }).Count
    Check "Earlier rows copied to AppData" (($fallbackText -match "SmokeSeed") -and ($fallbackRows -ge $documentsRows)) "$documentsRows in Documents, $fallbackRows in AppData"
    $documentsHashAfter = HashOf $logCsv
    Check "Documents CSV left as it was" ($documentsHashAfter -eq $documentsHash) $(if ($documentsHashAfter -like "unreadable:*") { $documentsHashAfter } else { "" })
    $statusMs = WaitUntil { (LogView $form).Status -match "Controlled folder access" } 10000
    $view = LogView $form
    Check "Status says Controlled folder access moved the log" ($statusMs -ge 0) $view.Status
    Check "Status carries no path or exception" ($view.Status -notmatch ":\\|Could not find file|Exception") $view.Status
    Check "No 'Could not find file' text in the page" (-not ($texts -match "Could not find file"))
    Check "Logging chip stays On while AppData accepts writes" (ChipIs $view.Chip "On") $view.Chip
    Notice "log moved" "Logging=$($view.Chip) | File=$($view.File) | $($view.Status)"
    RenderCheck $form "Settings render with the log moved" "smoke-cfa-render.png"
    Screenshot "smoke-cfa-moved.png"

    $pressed = WebInvoke (WebId (WebDocument $form) "btnCopyPath")
    Start-Sleep -Milliseconds 700
    $toast = WebToast $form
    $clip = try { "$(Get-Clipboard -Raw)".Trim() } catch { "" }
    if ($clip) {
        Check "Copy path copies the AppData location" ($pressed -and (($clip -ieq $fallbackFolder) -or ($clip -ieq $fallbackCsv))) "$clip | toast: $toast"
    } else {
        Info "Copy path" "clipboard not readable on this runner (pressed: $pressed, toast: $toast)"
    }

    $pressed = WebInvoke (WebId (WebDocument $form) "btnOpenFolder")
    $explorerMs = WaitUntil { (ExplorerFolders) -contains $fallbackFolder } 15000
    $explorer = @(ExplorerFolders | Select-Object -Unique)
    if ($explorerMs -ge 0) {
        Check "Open folder opens the AppData folder in Explorer" $true "$explorerMs ms"
    } elseif ($explorer.Count -gt 0) {
        Check "Open folder opens the AppData folder in Explorer" $false "Explorer shows $($explorer -join '; ') (pressed: $pressed)"
    } elseif (ExplorerTitled "RiotRouteLog") {
        Check "Open folder opens an Explorer window on RiotRouteLog" $true "Shell.Application lists no windows"
    } else {
        Check "Open folder opens the AppData folder in Explorer" $false "no Explorer window (pressed: $pressed, toast: $(WebToast $form))"
    }
    Notice "open folder" "Explorer windows: $($explorer -join '; ')"
    Screenshot "smoke-cfa-open-folder.png"
    CloseExplorerAt $fallbackFolder

    $movedNoted = WaitUntil { (ReadText $fallbackActivity) -match "Log moved to AppData" } 10000
    Check "AppData activity.log records the move" ($movedNoted -ge 0)
    $cfa.Refresh()
    Check "App keeps running after the move" ((-not $cfa.HasExited) -and $cfa.Responding)
    StopAll

    # --- Neither folder accepts writes: the chip says Blocked and the status is one short line.
    Check "Deny ACL set on the AppData log folder" (DenyWrites $fallbackFolder)
    $blocked = Launch
    $blockedShownMs = WaitUntil { WindowShown $blocked } 30000
    Check "Window opens with both folders blocked" ($blockedShownMs -ge 0) "$blockedShownMs ms"
    $form = MainWindow $blocked
    $page = WaitForPage $form 45000
    Check "Web page renders with both folders blocked" ($page.Ms -ge 0) $(if ($page.Fallback) { "fallback panel: $($page.Fallback)" } else { "$($page.Ms) ms" })
    $settingsMs = OpenPage $form "Settings"
    Check "Settings page opens with both folders blocked" ($settingsMs -ge 0) "$settingsMs ms"
    $chipMs = WaitUntil { ChipIs (LogView $form).Chip "Blocked" } 15000
    $null = WaitUntil { (LogView $form).Status.StartsWith("Blocked:") } 5000
    $view = LogView $form
    $texts = SaveTexts "window-text-blocked.txt" $form
    Check "Logging chip says Blocked" ($chipMs -ge 0) $view.Chip
    $tone = ChipTone $form $view.ChipElement
    if ($colourCalibrated -and $tone -and $tone -notlike "Unsure*") {
        Check "Blocked chip is red" ($tone -like "Bad*") $tone
    } else {
        Info "Blocked chip colour (expected Bad)" $(if ($tone) { "$tone; not checked (On chip read as Good: $colourCalibrated)" } else { "no chip pixels captured" })
    }
    Check "Blocked status is one short line" ($view.Status.StartsWith("Blocked:") -and $view.Status.Length -le 200 -and $view.Status -notmatch "[\r\n]") "$($view.Status.Length) chars: $($view.Status)"
    Check "Blocked status names the fix, not a path or exception" (($view.Status -match "folder permissions") -and ($view.Status -notmatch "Ransomware protection") -and ($view.Status -notmatch ":\\|Could not find file|Exception"))
    Check "No 'Could not find file' text in the page" (-not ($texts -match "Could not find file"))
    Notice "blocked" "Logging=$($view.Chip) [$tone] | $($view.Status)"
    Start-Sleep -Seconds 10
    $blocked.Refresh()
    Check "App keeps running while blocked" ((-not $blocked.HasExited) -and $blocked.Responding)
    RenderCheck $form "Settings render while blocked" "smoke-blocked-render.png"
    Screenshot "smoke-blocked.png"
    StopAll
    RestoreWrites
}
catch {
    Check "Smoke script finished" $false "$($_.Exception.Message) (line $($_.InvocationInfo.ScriptLineNumber))"
}
finally {
    StopAll
    RestoreWrites
    CloseExplorerAt $fallbackFolder
    Remove-ItemProperty -Path $runKey -Name "GeckoPeeks" -ErrorAction SilentlyContinue

    $crashAfter = FileLength $crashLog
    Check "No crash.log entries" ($crashAfter -eq $crashBefore) "$crashBefore -> $crashAfter bytes"
    $fallbackCrashAfter = FileLength $fallbackCrash
    Check "No crash.log entries in AppData" ($fallbackCrashAfter -eq $fallbackCrashBefore) "$fallbackCrashBefore -> $fallbackCrashAfter bytes"
    foreach ($file in @($crashLog, $activityLog, $logCsv)) {
        if (Test-Path -LiteralPath $file) { Copy-Item -LiteralPath $file -Destination $OutDir -Force }
    }
    foreach ($file in @($fallbackCrash, $fallbackActivity, $fallbackCsv)) {
        if (Test-Path -LiteralPath $file) { Copy-Item -LiteralPath $file -Destination (Join-Path $OutDir ("appdata-" + (Split-Path -Leaf $file))) -Force }
    }

    $lines = @("### Gecko Peeks $ExpectVersion smoke on windows-latest", "", "| Check | Result | Detail |", "| --- | --- | --- |")
    foreach ($result in $results) {
        $lines += "| $($result.Check) | $($result.Result) | $(($result.Detail -replace '\|', '/')) |"
    }
    $markdown = $lines -join "`n"
    $markdown | Out-File -FilePath (Join-Path $OutDir "smoke-summary.md") -Encoding utf8
    if ($env:GITHUB_STEP_SUMMARY) { $markdown | Out-File -FilePath $env:GITHUB_STEP_SUMMARY -Append -Encoding utf8 }
}

if ($failures.Count -gt 0) {
    Write-Host "::error::Smoke failed: $($failures -join '; ')"
    exit 1
}
Write-Host "Smoke passed: $($results.Count) checks."
