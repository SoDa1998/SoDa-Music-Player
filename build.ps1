# Builds SoDaPlayer.exe (GUI) and soda-test.exe (console self-test) with the C# compiler that ships with Windows (.NET Framework 4.x).
#
# SoDaPlayer.exe is a single file for sharing: fonts, icons, artwork, licences and a GZip-compressed copy of
# ffmpeg.exe + ffprobe.exe are embedded. On first run FFmpeg is unpacked to %LOCALAPPDATA%\SoDaPlayer\ffmpeg\<version>.
# The FFmpeg used is the one on PATH (or set $env:FFMPEG_DIR to a folder holding ffmpeg.exe / ffprobe.exe / LICENSE).
# Pass -NoFFmpeg for a small developer build that uses the FFmpeg installed on the PC instead.
param([switch]$NoFFmpeg)
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $MyInvocation.MyCommand.Path
$csc = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
if (-not (Test-Path $csc)) { throw "csc.exe not found at $csc" }

$bin = Join-Path $root 'bin'
$obj = Join-Path $root 'obj'
$dist = Join-Path $root 'dist'
New-Item -ItemType Directory -Force $bin, $obj, $dist | Out-Null
$src = Get-ChildItem (Join-Path $root 'src') -Filter *.cs | ForEach-Object { $_.FullName }
$refs = '/r:System.dll', '/r:System.Core.dll', '/r:System.Drawing.dll', '/r:System.Windows.Forms.dll', '/r:System.Web.Extensions.dll'
$manifest = Join-Path $root 'app.manifest'
# Inter (Google Fonts, SIL OFL 1.1) and Tabler Icons (MIT) are embedded as resources named BitPerfectPlayer.Fonts.<file>.
$fontRes = Get-ChildItem (Join-Path $root 'fonts'), (Join-Path $root 'icons') -Filter *.ttf | ForEach-Object { "/resource:$($_.FullName),BitPerfectPlayer.Fonts.$($_.Name)" }
$icon = Join-Path $root 'assets\soda.ico'
$assetRes = "/resource:$icon,BitPerfectPlayer.AppIcon.ico", "/resource:$(Join-Path $root 'assets\soda-256.png'),BitPerfectPlayer.AppIcon.png",
            "/resource:$(Join-Path $root 'icons\LICENSE.txt'),BitPerfectPlayer.Licenses.Tabler-LICENSE.txt"
$common = @('/nologo', '/platform:x64', '/optimize+', '/warn:4', "/win32manifest:$manifest", "/win32icon:$icon") + $refs + $fontRes + $assetRes

# ---- Bundled FFmpeg (compressed once, re-used until the source binaries change)
$ffRes = @()
if (-not $NoFFmpeg) {
    $ffDir = $env:FFMPEG_DIR
    if (-not $ffDir) { $cmd = Get-Command ffmpeg.exe -ErrorAction SilentlyContinue; if ($cmd) { $ffDir = Split-Path $cmd.Source } }
    if (-not $ffDir -or -not (Test-Path (Join-Path $ffDir 'ffprobe.exe'))) { throw 'FFmpeg not found: put it on PATH, set $env:FFMPEG_DIR, or build with -NoFFmpeg' }
    $ffRoot = Split-Path $ffDir   # gyan.dev layout: <root>\bin\ffmpeg.exe, <root>\LICENSE, <root>\README.txt
    $sizes = @{}
    foreach ($n in 'ffmpeg.exe', 'ffprobe.exe') {
        $srcFile = Get-Item (Join-Path $ffDir $n)
        $gz = Join-Path $obj "$n.gz"
        if (-not (Test-Path $gz) -or (Get-Item $gz).LastWriteTimeUtc -lt $srcFile.LastWriteTimeUtc) {
            Write-Host "Compressing $n ..."
            $in = [IO.File]::OpenRead($srcFile.FullName); $out = [IO.File]::Create($gz)
            $z = New-Object IO.Compression.GZipStream($out, [IO.Compression.CompressionLevel]::Optimal)
            $in.CopyTo($z); $z.Dispose(); $in.Dispose()
        }
        $sizes[$n] = $srcFile.Length
        $ffRes += "/resource:$gz,BitPerfectPlayer.$n.gz"
    }
    $version = (& (Join-Path $ffDir 'ffmpeg.exe') -hide_banner -version | Select-Object -First 1) -replace '^ffmpeg version\s+', '' -replace '\s+Copyright.*$', ''
    $tag = ($version -replace '[^A-Za-z0-9._-]', '_') + '-' + $sizes['ffmpeg.exe']
    $mf = Join-Path $obj 'ffmpeg.manifest.txt'
    "version=$version`r`ntag=$tag`r`nffmpeg.exe=$($sizes['ffmpeg.exe'])`r`nffprobe.exe=$($sizes['ffprobe.exe'])" | Set-Content $mf -Encoding UTF8
    $ffRes += "/resource:$mf,BitPerfectPlayer.ffmpeg.manifest.txt"
    foreach ($lic in @(@('LICENSE', 'FFmpeg-LICENSE.txt'), @('README.txt', 'FFmpeg-README.txt'))) {
        $p = Join-Path $ffRoot $lic[0]
        if (Test-Path $p) { $ffRes += "/resource:$p,BitPerfectPlayer.Licenses.$($lic[1])" } else { Write-Warning "FFmpeg $($lic[0]) not found next to the build - add it before sharing." }
    }
}

& $csc @common @ffRes /target:winexe /main:BitPerfectPlayer.Program "/out:$bin\SoDaPlayer.exe" $src
if ($LASTEXITCODE -ne 0) { throw 'GUI build failed' }
& $csc @common /target:exe /main:BitPerfectPlayer.SelfTest "/out:$bin\soda-test.exe" $src
if ($LASTEXITCODE -ne 0) { throw 'self-test build failed' }
if (-not $NoFFmpeg) { Copy-Item "$bin\SoDaPlayer.exe" "$dist\SoDaPlayer.exe" -Force }
$msg = "Built: $bin\SoDaPlayer.exe"; if (-not $NoFFmpeg) { $msg += "  (shareable single file: $dist\SoDaPlayer.exe)" }; Write-Host $msg
