$ErrorActionPreference = 'Stop'
$dir = Split-Path -Parent $MyInvocation.MyCommand.Path
Set-Location $dir

# keep the source in UTF-8 with BOM, otherwise csc reads Russian text in the wrong codepage
$src = Join-Path $dir 'LightFixer.cs'
$text = [IO.File]::ReadAllText($src, [Text.Encoding]::UTF8)
[IO.File]::WriteAllText($src, $text, [Text.UTF8Encoding]::new($true))

$csc = 'C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe'
$exe = Join-Path $dir 'LightFixer-BRI-721.exe'
if (Test-Path $exe) { Remove-Item $exe -Force }

$man = Join-Path $dir 'app.manifest'
$ico = Join-Path $dir 'app.ico'
$icoSmall = Join-Path $dir 'app-small.ico'
$res = "$icoSmall,LightFixer.Icon.ico"

& $csc -nologo -target:winexe "-out:$exe" "-win32manifest:$man" "-win32icon:$ico" "-resource:$res" `
    -r:System.Management.dll -r:System.Windows.Forms.dll -r:System.Drawing.dll -r:System.ServiceProcess.dll `
    $src

if (Test-Path $exe) {
    $f = Get-Item $exe
    Write-Host ("OK: {0}  {1:N0} bytes" -f $f.FullName, $f.Length)
} else {
    Write-Host 'BUILD FAILED'
    exit 1
}
