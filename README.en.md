# LightFixer BRI-721

Keyboard backlight helper for the **HONOR X16 Plus 2024 (BRI-721)** notebook on Windows 11.

The program turns the keyboard backlight on while you are using the laptop (on key
presses, and optionally on mouse movement) and turns it off after a configurable idle
period — so the light does not burn the battery for nothing. It can also set the
backlight level right after the laptop is powered on.

Русская версия: [README.md](README.md)

## Features

- Backlight comes on when you press keys and, optionally, when you move the mouse.
  HONOR's own settings only react to key presses; the mouse-movement option is
  added by this program.
- Turns off after a configurable idle time: presets from 1 second to 2 hours, or a
  custom value in hours, minutes and seconds, or never.
- Backlight at power-on: off / low / high / leave as is. HONOR's own settings cannot
  set the brightness after boot — this program can.
- A switch back to HONOR's native control: the program then sends no backlight
  commands at all and the laptop's own settings take over.
- **Fn+Space** keeps working exactly as it does without the program: it cycles the
  backlight at any time, just like in the stock configuration.
- Lives in the notification area, starts with Windows, keeps its settings.

## Getting started

1. Put `LightFixer-BRI-721.exe` into any permanent folder (for example `C:\LightFixer`).
   The program keeps `settings.json` next to it,
   so the folder must be permanent and writable.
2. Run it as administrator (the program asks for elevation itself).
3. Set your options and press **Save**.

On first run the program creates a Task Scheduler entry with administrator rights
that starts it silently in the tray 15 seconds after sign-in. There are no further
prompts.

Autostart is controlled by the tick "Start the program with Windows" in the
"Keyboard backlight when the laptop starts" card: clear it, press **Save**, and the
scheduled task is switched off; tick it again and the task is switched back on.
The second tick, "Light up before sign-in (at the password screen)", controls a
separate task: when cleared, that task is removed from Task Scheduler.

The tasks are named **LightFixer BRI-721** and **LightFixer BRI-721 (загрузка)**.
They are visible in Task Scheduler (Win+R → `taskschd.msc`), but there is no need
to touch them there: the program keeps them in line with the ticks itself.

Settings are stored in `settings.json` next to the executable, so that folder must be
writable. The diagnostic log is at `%LOCALAPPDATA%\LightFixer\lightfixer.log` and can
be opened from the tray menu. If you move the `.exe` elsewhere, the autostart task is
updated on the next launch.

In "HONOR's own settings" mode the program sends no backlight commands: it does not
set the brightness and does not change the BIOS timeout. It only reads the current
state and shows it in the window.

Requires .NET Framework 4.x (included in Windows 11). Windows 10 / 11, HONOR X16 Plus
BRI-721. Other models are not supported.

## Building from source

```
C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe -nologo -target:winexe ^
  -win32manifest:app.manifest ^
  -r:System.Management.dll -r:System.Windows.Forms.dll -r:System.Drawing.dll ^
  -out:LightFixer-BRI-721.exe LightFixer.cs
```

Or simply `powershell -ExecutionPolicy Bypass -File build.ps1`.

`app.manifest` and two icon files must be present: `app.ico` (16–256 px, the
executable's own icon) and `app-small.ico` (16–64 px, embedded for the tray icon and
the window title). Both are generated from scratch by `_icon.ps1`, with no external
assets.

## Author and licence

Author: **sergliv** — <https://4pda.to/forum/index.php?showuser=1727220>
Contact: private messages on 4pda.

Licence: **GNU General Public License v3.0 or any later version**
(GPL-3.0-or-later). The full text is in the `LICENSE` file, and at
<https://www.gnu.org/licenses/gpl-3.0.html>.

You may use, study, modify and redistribute the program under the terms of GPLv3.
Distributing modified versions requires keeping the same licence and crediting the
original author. The complete source code ships with the program.

The program uses only the standard .NET Framework library (Microsoft licence, not
GPL). There are no third-party libraries.

## Supporting the author

A donation is voluntary and optional: it does not affect getting or using the
program. It is a gift — nothing is provided in return, it grants no rights to the
program and does not change the licence.

Payments are handled by CloudTips; the recipient is the individual named on the
donation page.

## Disclaimer

THE PROGRAM IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR IMPLIED,
INCLUDING THE WARRANTIES OF MERCHANTABILITY AND FITNESS FOR A PARTICULAR PURPOSE.
TO THE MAXIMUM EXTENT PERMITTED BY LAW, THE AUTHOR SHALL NOT BE LIABLE FOR ANY
DAMAGES, LOSS OF DATA, HARDWARE DAMAGE OR OTHER CONSEQUENCES ARISING FROM THE USE OF
OR INABILITY TO USE THE PROGRAM.

The program talks to the BIOS WMI/ACPI interface to control the backlight. Use it at
your own risk. Creating a system restore point before the first launch is recommended.

## How it works

The utility uses the interface Windows itself exposes for this device: the
`root\wmi` WMI namespace, class `OemWMIMethod`, method `OemWMIfun`, object
`ACPI\PNP0C14\HWMI_0`. This is the documented ACPI–WMI mapping mechanism: it is
visible to any process with administrator rights and needs neither drivers nor
third-party software.

The parameter values (command numbers for the backlight mode and the timeout) were
determined from the device itself — by analysing its ACPI tables and probing the WMI
object's methods on a laptop of this model, and from previously published public
information.

The source code is original, written from scratch. The distribution contains no HONOR
files: no executables, no images, no text. The WMI command numbers were derived from
the laptop's own ACPI tables and from public write-ups, not by decompiling anyone
else's software.

## Security and battery use

**No network at all.** The executable contains no networking components
(`System.Net`, `Socket`, `TcpClient`, `HttpWebRequest`, `WebClient`), and while it
runs the program holds no network connection. The only addresses in the file are
Microsoft namespace URLs from the manifest.

**Registry and other processes.** The program does not touch the registry, does not
load third-party libraries and does not inject code into other processes. Of the
system libraries it uses only `user32.dll` (a standard Windows library) and a single
WMI method of the firmware.

**Keyboard.** To bring the light back after the idle pause the program installs a
system keyboard hook (`SetWindowsHookEx`). The handler stores **only the time** of
the last keypress: neither the key code nor any typed text is read or recorded
anywhere.

**Privileges.** The program runs as administrator — the ACPI method of the firmware
is unavailable otherwise. The boot task runs as SYSTEM so that the light is already
on while the Windows password is typed.

**For installation.** Install the program into a folder only administrators can
write to, for example `C:\Program Files\LightFixer\`. If it sits in a folder that is
writable by everyone, replacing the executable gives code execution with
administrator rights, and as SYSTEM at boot.

**Battery.** While you work, the program asks the firmware for the current mode ten
times a second: otherwise a press of Fn+Space would be missed (the firmware holds
the "auto" step for about 50 ms). Measured on this model — **about 0.7 % of one
core**, roughly 1–2 % of battery life. When the keyboard and the mouse are left
alone for more than 30 seconds, polling stops and the drain drops to almost nothing.

**Sleep.** The program does not block sleep (it never calls `SetThreadExecutionState`),
creates no wake timers, and its scheduled task cannot wake the laptop. After waking
up, the backlight is left alone for 20 seconds so the firmware can settle.

## On-screen popup (HONOR)

When you press Fn+Space, the laptop shows its own popup, drawn by HONOR's
`OSD_Daemon.exe` from `C:\Program Files\HONOR\OSD`, installed by HONOR PC Manager
18.0.0.81 (C001HONOR). **LightFixer neither reads nor modifies these files** — the
popup is always the stock one.

One mismatch can follow from this. On the "auto" step the backlight is in fact at low
brightness (the program converts that step to "low" immediately), while the popup
shows the word "Auto". That is the vendor's design, and LightFixer does not change
that word: the vendor's files stay untouched.

When brightness is changed from the program's window (the **Save** button), HONOR
shows no popup — the change is visible on the keyboard itself.

## Trademarks and non-affiliation with HONOR

HONOR and X16 Plus are trademarks of their respective owners. BRI-721 is a laptop
model designation.

LightFixer BRI-721 is an independent project. It is not affiliated with, endorsed,
sponsored or supported by HONOR (including HONOR Device Co., Ltd.), and it is not an
official HONOR product. The model name is used solely to describe compatibility.
