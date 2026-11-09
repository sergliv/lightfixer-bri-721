$dir = $PSScriptRoot
$rel = Join-Path $dir 'release'
$one = Join-Path $rel 'LightFixer-BRI-721'
# without this, Copy-Item writes a FILE with that name and the three copies
# overwrite each other, leaving only the README
if (Test-Path $one -PathType Leaf) { Remove-Item $one -Force }
New-Item -ItemType Directory -Path $one -Force | Out-Null
Copy-Item (Join-Path $dir 'LightFixer-BRI-721.exe') $one -Force
Copy-Item (Join-Path $dir 'LICENSE') $one -Force
Copy-Item (Join-Path $dir 'README.md') $one -Force
Copy-Item (Join-Path $dir 'README.en.md') $one -Force
$zip = Join-Path $rel 'LightFixer-BRI-721-src.zip'
if (Test-Path $zip) { Remove-Item $zip -Force }
$tmp = Join-Path $env:TEMP 'lfsrc'
if (Test-Path $tmp) { Remove-Item $tmp -Recurse -Force }
New-Item -ItemType Directory -Path $tmp -Force | Out-Null
Copy-Item (Join-Path $dir 'LightFixer.cs') $tmp -Force
Copy-Item (Join-Path $dir 'app.manifest') $tmp -Force
Copy-Item (Join-Path $dir 'app.ico') $tmp -Force
Copy-Item (Join-Path $dir 'app-small.ico') $tmp -Force
Copy-Item (Join-Path $dir '_icon.ps1') $tmp -Force
Copy-Item (Join-Path $dir 'build.ps1') $tmp -Force
Copy-Item (Join-Path $dir 'LICENSE') $tmp -Force
Copy-Item (Join-Path $dir 'README.md') $tmp -Force
Copy-Item (Join-Path $dir 'README.en.md') $tmp -Force
Compress-Archive -Path (Join-Path $tmp '*') -DestinationPath $zip -Force
Remove-Item $tmp -Recurse -Force
Get-ChildItem $rel -Recurse -File | Select-Object FullName, Length
