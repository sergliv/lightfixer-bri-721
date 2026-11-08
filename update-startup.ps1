# Пересобирает программу и обновляет автозапуск:
#  - при входе в систему — сразу, без прежних 15 секунд;
#  - при включении ноутбука, до ввода пароля, — чтобы подсветка уже горела.
# Запускать, когда программа закрыта (значок в трее -> Выход).

$ErrorActionPreference = 'Stop'
$dir = Split-Path -Parent $PSCommandPath
$exe = Join-Path $dir 'LightFixer-BRI-721.exe'
$log = Join-Path $dir 'update-startup.log'

function Say($m) {
    $line = (Get-Date -Format 'HH:mm:ss') + '  ' + $m
    Add-Content -Path $log -Value $line -Encoding UTF8
    Write-Host $m
}

# создание задачи от имени SYSTEM требует прав администратора
$isAdmin = ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
if (-not $isAdmin) {
    Start-Process pwsh -Verb RunAs -ArgumentList '-NoProfile','-ExecutionPolicy','Bypass','-File',"`"$PSCommandPath`""
    return
}

Say '--- обновление автозапуска ---'

# планировщик по умолчанию не запускает задачу от батареи — на ноутбуке это
# как раз главный случай, поэтому запрет снимается у обеих задач
function Relax-Tasks {
    $relax = New-ScheduledTaskSettingsSet -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries -StartWhenAvailable
    foreach ($t in @('LightFixer BRI-721', 'LightFixer BRI-721 (загрузка)')) {
        if (-not (Get-ScheduledTask -TaskName $t -ErrorAction SilentlyContinue)) { continue }
        try {
            Set-ScheduledTask -TaskName $t -Settings $relax | Out-Null
            Say ('ограничение «только от сети» снято: ' + $t)
        } catch { Say ('ОШИБКА настроек задачи ' + $t + ': ' + $_.Exception.Message) }
    }
}

Relax-Tasks

# программа должна быть закрыта, иначе файл занят
$free = $false
try { $fs = [IO.File]::Open($exe, 'Open', 'ReadWrite', 'None'); $fs.Close(); $free = $true } catch { }
if (-not $free) {
    Say 'ПРОГРАММА ЕЩЁ РАБОТАЕТ.'
    Say 'Закройте её: правый клик по значку в трее -> Выход.'
    Say 'Затем запустите этот файл снова.'
    exit 1
}

# новая сборка
Say 'собираю программу...'
& pwsh -NoProfile -ExecutionPolicy Bypass -File (Join-Path $dir 'build.ps1') | ForEach-Object { Say $_ }

# запуск при входе в систему, без задержки
schtasks.exe /Create /TN "LightFixer BRI-721" /TR "`"$exe`" /tray" /SC ONLOGON /RL HIGHEST /F | Out-Null
if ($LASTEXITCODE -eq 0) { Say 'задача при входе обновлена (задержка убрана)' } else { Say ('ОШИБКА задачи при входе: ' + $LASTEXITCODE) }

# запуск при включении ноутбука, до входа в систему — только если это включено
# галочкой в окне программы ("bootLight"; без ключа в файле считается включённым)
$bootOn = $true
$setFile = Join-Path $dir 'settings.json'
if (Test-Path $setFile) {
    $txt = Get-Content $setFile -Raw -Encoding UTF8
    if ($txt -match '"bootLight"\s*:\s*(true|false)') { $bootOn = ($Matches[1] -eq 'true') }
}
if ($bootOn) {
    schtasks.exe /Create /TN "LightFixer BRI-721 (загрузка)" /TR "`"$exe`" /boot" /SC ONSTART /RU SYSTEM /RL HIGHEST /F | Out-Null
    if ($LASTEXITCODE -eq 0) { Say 'задача при включении создана (свет до ввода пароля)' } else { Say ('ОШИБКА задачи при включении: ' + $LASTEXITCODE) }
} else {
    schtasks.exe /Delete /TN "LightFixer BRI-721 (загрузка)" /F 2>$null | Out-Null
    Say 'свет до ввода пароля выключен галочкой в программе — задача не создаётся'
}

Relax-Tasks

# проверка: запускаем загрузочную задачу сейчас и смотрим, что она сделала
# (программа в режиме /boot только ставит свет и выходит, работающей копии не мешает)
$bootLog = Join-Path $dir 'boot.log'
if ($bootOn) {
    if (Test-Path $bootLog) { Remove-Item $bootLog -Force }
    schtasks.exe /Run /TN "LightFixer BRI-721 (загрузка)" | Out-Null
    Start-Sleep -Seconds 6
    if (Test-Path $bootLog) { Say 'проверка до входа в систему:'; Get-Content $bootLog -Encoding UTF8 | ForEach-Object { Say ('  ' + $_) } }
    else { Say 'ВНИМАНИЕ: загрузочная задача не оставила журнал, проверьте после перезагрузки' }
}

Say 'запускаю программу...'
Start-Process -FilePath $exe -ArgumentList '/tray'
Say 'готово'
