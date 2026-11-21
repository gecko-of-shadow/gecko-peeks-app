<#
.SYNOPSIS
  Uploads a Gecko Peeks exe to the update channel and promotes it.

.DESCRIPTION
  Works in Windows PowerShell 5.1 and PowerShell 7.
  1. Checks that the exe file name fits the channel and its FileVersion is -Version.
  2. Computes the SHA-256 of the whole file.
  3. Splits the exe into parts of at most 24 MiB (one Workers KV value holds at most 25 MiB).
     PUT /v1/admin/upload/<channel>/<version>/<file>/part/<n> for each part, n = 0, 1, 2, ...
     Each PUT sends the SHA-256 of that part in X-Part-Sha256. The Worker records it.
     A part that is already stored is skipped only when its recorded SHA-256 is the one of
     this part. Otherwise the script stops: that stored part came from another build.
  4. POST /v1/admin/finalize/<channel>/<version>/<file> with {size, sha256, parts, partSha256s}.
     The Worker checks that every part is stored, that the part sizes add up to the size, and
     that the recorded SHA-256 of each part is the one in partSha256s.
     The Worker does not hash the file (the Free plan has 10 ms of CPU per request).
     The app checks the SHA-256 of the whole file after the download.
  5. POST /v1/admin/promote/<channel> (skipped with -NoPromote). The Worker checks the stored
     size and SHA-256 before it moves <channel>/latest.json.
  6. After a promote, deletes old versions of the channel (Workers KV Free holds 1 GB).
     It keeps the promoted version and the -Keep - 1 newest finalized versions below it.
     Versions above the promoted one are never deleted. -Keep 0 turns this step off.

  If a stored part is from another build, the version has no meta yet. Delete it, then run again:
    DELETE /v1/admin/release/<channel>/<version>  (with the publish key)
  Or publish under a new version number.

  The publish key comes from env GECKO_PUBLISH_KEY, else from the DPAPI file
  %LOCALAPPDATA%\GeckoPeeksDev\publish-key.dat (CurrentUser). The key is never printed.
  The script prints only the target origin, the version, the size and the SHA-256.
  It sends the key only to -BaseUrl, which defaults to the real Worker.

.EXAMPLE
  .\cloud\publish-release.ps1 -Exe .\GeckoPeeksDev.exe -Version 2.0.1 -Channel devtest -Notes "Dev test"
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$Exe,
    [Parameter(Mandatory = $true)][string]$Version,
    [Parameter(Mandatory = $true)][ValidateSet("stable", "devtest")][string]$Channel,
    [string]$Notes = "",
    [switch]$NoPromote,
    # How many versions of the channel stay stored after a promote. 0 deletes nothing.
    [ValidateRange(0, 20)][int]$Keep = 2,
    # The Worker origin. Default is the real Worker. The app's GECKO_PEEKS_UPDATE_URL is never
    # read here, so a leftover dev override cannot receive the publish key.
    [string]$BaseUrl = "https://gecko-peeks-api.gecko-apps.workers.dev"
)

$ErrorActionPreference = "Stop"
Set-StrictMode -Version 2.0

function Fail([string]$message) {
    Write-Host ("publish-release: " + $message)
    exit 1
}

if ($Version -notmatch '^(0|[1-9][0-9]{0,4})\.(0|[1-9][0-9]{0,4})\.(0|[1-9][0-9]{0,4})$') {
    Fail "Version must be three numbers, like 2.0.1."
}

$fileName = @{ stable = "GeckoPeeks.exe"; devtest = "GeckoPeeksDev.exe" }[$Channel]
if (-not (Test-Path -LiteralPath $Exe -PathType Leaf)) { Fail "Exe not found: $Exe" }
$item = Get-Item -LiteralPath $Exe
if ($item.Name -ne $fileName) { Fail "Channel $Channel takes $fileName, not $($item.Name)." }

# The app compares three parts. A build whose file version differs would update in a loop.
$fileVersion = [string]$item.VersionInfo.FileVersion
if ($fileVersion -ne $Version -and $fileVersion -ne "$Version.0") {
    Fail "The exe reports FileVersion '$fileVersion', not $Version. Build it with -p:Version=$Version."
}

if ([string]::IsNullOrWhiteSpace($BaseUrl)) { Fail "BaseUrl is empty. Pass -BaseUrl or leave it at the default." }
$BaseUrl = $BaseUrl.Trim().TrimEnd("/")
$baseUri = $null
if (-not [Uri]::TryCreate($BaseUrl, [UriKind]::Absolute, [ref]$baseUri)) { Fail "BaseUrl is not a URL." }
if (-not ($baseUri.Scheme -eq "https" -or ($baseUri.Scheme -eq "http" -and $baseUri.IsLoopback))) {
    Fail "BaseUrl must be https (http only for a loopback test server)."
}
if ($baseUri.UserInfo -or $baseUri.Query -or $baseUri.Fragment) {
    Fail "BaseUrl must not hold user info, a query or a fragment."
}
# Show where the key goes before it is sent. The key itself is never printed.
Write-Host ("target " + $baseUri.GetLeftPart([UriPartial]::Authority))

function Read-PublishKey {
    if (-not [string]::IsNullOrWhiteSpace($env:GECKO_PUBLISH_KEY)) { return $env:GECKO_PUBLISH_KEY.Trim() }
    $path = Join-Path $env:LOCALAPPDATA "GeckoPeeksDev\publish-key.dat"
    if (-not (Test-Path -LiteralPath $path)) { return $null }
    $secure = (Get-Content -LiteralPath $path -Raw).Trim() | ConvertTo-SecureString
    $bstr = [Runtime.InteropServices.Marshal]::SecureStringToBSTR($secure)
    try { return ([Runtime.InteropServices.Marshal]::PtrToStringBSTR($bstr)).Trim() }
    finally { [Runtime.InteropServices.Marshal]::ZeroFreeBSTR($bstr) }
}

$key = Read-PublishKey
if ([string]::IsNullOrWhiteSpace($key)) {
    Fail "No publish key. Set env GECKO_PUBLISH_KEY or create %LOCALAPPDATA%\GeckoPeeksDev\publish-key.dat."
}

$sha256 = (Get-FileHash -LiteralPath $item.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
$size = [long]$item.Length

Add-Type -AssemblyName System.Net.Http
[Net.ServicePointManager]::SecurityProtocol = [Net.ServicePointManager]::SecurityProtocol -bor [Net.SecurityProtocolType]::Tls12
$http = New-Object System.Net.Http.HttpClient
$http.Timeout = [TimeSpan]::FromMinutes(30)
$http.DefaultRequestHeaders.Authorization = New-Object System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", $key)
$key = $null

# Sends one admin request. A request message cannot be sent twice, so $makeContent builds the
# body again for each attempt. Returns @{ Code; Body }: Code is "" on success, or the Worker's
# error code when it is in $accept. Body is the parsed JSON answer, or $null.
# Waits and tries again on a network error, a 5xx, or a code in $retry. A 507 is not tried
# again: the Worker says KV refused the write (for example its storage or daily write limit).
# Workers KV is eventually consistent: a part or a meta written a moment ago can still read as
# missing for up to a minute. So finalize retries "missing_part" and promote retries "not_uploaded".
function Invoke-Admin([string]$method, [string]$url, [scriptblock]$makeContent, [string[]]$accept = @(), [string[]]$retry = @(), [hashtable]$headers = @{}) {
    $path = ([Uri]$url).AbsolutePath
    for ($attempt = 1; ; $attempt++) {
        $request = New-Object System.Net.Http.HttpRequestMessage((New-Object System.Net.Http.HttpMethod($method)), $url)
        if ($makeContent) { $request.Content = & $makeContent }
        foreach ($name in $headers.Keys) { [void]$request.Headers.TryAddWithoutValidation($name, [string]$headers[$name]) }
        $status = 0
        $text = ""
        try {
            $response = $http.SendAsync($request).GetAwaiter().GetResult()
            $status = [int]$response.StatusCode
            $text = $response.Content.ReadAsStringAsync().GetAwaiter().GetResult()
        }
        catch {
            $text = $_.Exception.GetBaseException().Message
        }
        finally {
            $request.Dispose()
        }
        $parsed = $null
        try { $parsed = $text | ConvertFrom-Json } catch { }
        if ($status -ge 200 -and $status -lt 300) { return @{ Code = ""; Body = $parsed } }
        $code = ""
        if ($parsed -and ($parsed.PSObject.Properties.Name -contains "error")) { $code = [string]$parsed.error }
        if ($code -and ($accept -contains $code)) { return @{ Code = $code; Body = $parsed } }
        $transient = ($status -eq 0) -or ($status -ge 500 -and $status -ne 507) -or ($code -and ($retry -contains $code))
        if (-not $transient -or $attempt -ge 8) {
            Fail ("HTTP " + $status + " from " + $path + ": " + $text)
        }
        Write-Host ("HTTP " + $status + " " + $code + " from " + $path + ", trying again (" + $attempt + ")")
        Start-Sleep -Seconds 10
    }
}

function New-JsonContent([string]$text) {
    return New-Object System.Net.Http.StringContent($text, [Text.Encoding]::UTF8, "application/json")
}

# Must match MAX_PART_BYTES in cloud/worker/src/update.js.
$partBytes = 24 * 1024 * 1024
$partCount = [int][Math]::Ceiling($size / [double]$partBytes)
$fileUrl = "$BaseUrl/v1/admin/upload/$Channel/$Version/$fileName"

try {
    # A run that stopped part way can run again. A stored part is skipped only when the
    # SHA-256 that the Worker recorded for it is the SHA-256 of the same part of this exe.
    # A part from another build (for example a CI re-run that built the exe again) stops the
    # script, and finalize also refuses parts whose recorded SHA-256 differs from partSha256s.
    # A version that is already finalized goes on to promote, which refuses the release
    # unless the stored size and whole-file SHA-256 are the ones of this exe.
    # The app also checks the SHA-256 of the whole file after the download.
    $partSha256s = New-Object System.Collections.Generic.List[string]
    $finalized = $false
    $hasher = [Security.Cryptography.SHA256]::Create()
    $stream = [IO.File]::OpenRead($item.FullName)
    try {
        $buffer = New-Object byte[] $partBytes
        for ($n = 0; $n -lt $partCount; $n++) {
            $want = [int][Math]::Min([long]$partBytes, $size - [long]$n * $partBytes)
            $got = 0
            while ($got -lt $want) {
                $read = $stream.Read($buffer, $got, $want - $got)
                if ($read -le 0) { Fail "The exe got shorter while it was read." }
                $got += $read
            }
            $partSha = (($hasher.ComputeHash($buffer, 0, $want) | ForEach-Object { $_.ToString("x2") }) -join "")
            $partSha256s.Add($partSha)
            # Once the version is finalized, the rest of the parts are only hashed.
            if ($finalized) { continue }
            $makePart = {
                $c = New-Object System.Net.Http.ByteArrayContent($buffer, 0, $want)
                $c.Headers.ContentType = New-Object System.Net.Http.Headers.MediaTypeHeaderValue("application/octet-stream")
                $c
            }
            $result = Invoke-Admin "PUT" "$fileUrl/part/$n" $makePart @("part_exists", "version_exists") @() @{ "X-Part-Sha256" = $partSha }
            if ($result.Code -eq "version_exists") { Write-Host "version is already finalized"; $finalized = $true; continue }
            if ($result.Code -eq "part_exists") {
                $stored = $null
                if ($result.Body -and ($result.Body.PSObject.Properties.Name -contains "sha256")) { $stored = [string]$result.Body.sha256 }
                if ([string]::IsNullOrEmpty($stored)) {
                    Fail ("part " + $n + " is already stored with no SHA-256, so it cannot be checked. Delete version " + $Version + " on " + $Channel + " (DELETE /v1/admin/release/" + $Channel + "/" + $Version + ") or publish a new version.")
                }
                if ($stored -ne $partSha) {
                    Fail ("part " + $n + " is already stored from another build (stored sha256 " + $stored + ", this part " + $partSha + "). Delete version " + $Version + " on " + $Channel + " (DELETE /v1/admin/release/" + $Channel + "/" + $Version + ") or publish a new version.")
                }
            }
            $state = if ($result.Code -eq "part_exists") { "already stored, same sha256" } else { "stored" }
            Write-Host ("part " + $n + " of " + $partCount + " (" + $want + " bytes) " + $state)
        }
    }
    finally {
        $stream.Dispose()
        $hasher.Dispose()
    }

    $finalBody = @{ size = $size; sha256 = $sha256; parts = $partCount; partSha256s = $partSha256s.ToArray() } | ConvertTo-Json -Compress
    $result = Invoke-Admin "POST" "$BaseUrl/v1/admin/finalize/$Channel/$Version/$fileName" { New-JsonContent $finalBody } @("version_exists") @("missing_part")
    Write-Host $(if ($result.Code -eq "version_exists") { "finalize: already finalized" } else { "finalized" })

    if (-not $NoPromote) {
        # The app shows at most 4000 chars, and the Worker refuses a promote body over 16 KB.
        $Notes = $Notes.Replace("`r`n", "`n").Trim()
        if ($Notes.Length -gt 4000) { $Notes = $Notes.Substring(0, 3999) + [char]0x2026 }
        $body = @{ version = $Version; sha256 = $sha256; size = $size; notes = $Notes } | ConvertTo-Json -Compress
        [void](Invoke-Admin "POST" "$BaseUrl/v1/admin/promote/$Channel" { New-JsonContent $body } @() @("not_uploaded"))
        Write-Host "promoted"

        if ($Keep -gt 0) {
            # Workers KV Free holds 1 GB per account and each release is about 72 MB.
            # Keep the promoted version and the ($Keep - 1) newest finalized versions below it.
            # Delete every other version below it, finalized or not. Never touch a higher one.
            $listed = (Invoke-Admin "GET" "$BaseUrl/v1/admin/versions/$Channel" $null).Body
            $current = [Version]$Version
            $older = @($listed.versions | Where-Object { [Version]$_.version -lt $current } | Sort-Object { [Version]$_.version } -Descending)
            $kept = 1
            foreach ($v in $older) {
                if ($v.finalized -and $kept -lt $Keep) { $kept++; Write-Host ("keep " + $v.version); continue }
                if ($listed.latest -and $v.version -eq $listed.latest) { Write-Host ("keep " + $v.version + " (latest.json still names it)"); continue }
                $del = Invoke-Admin "DELETE" "$BaseUrl/v1/admin/release/$Channel/$($v.version)" $null @("is_latest")
                Write-Host $(if ($del.Code -eq "is_latest") { "keep " + $v.version + " (the Worker still reads it as latest)" } else { "deleted " + $v.version })
            }
        }
    }
}
finally {
    $http.Dispose()
}

Write-Host "version $Version"
Write-Host "size $size"
Write-Host "sha256 $sha256"
