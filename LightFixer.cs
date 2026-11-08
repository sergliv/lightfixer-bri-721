// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 sergliv <https://4pda.to/forum/index.php?showuser=1727220>
//
// LightFixer BRI-721 - утилита управления подсветкой клавиатуры ноутбука
// HONOR X16 Plus BRI-721.
//
// This program is free software: you can redistribute it and/or modify
// it under the terms of the GNU General Public License as published by
// the Free Software Foundation, either version 3 of the License, or
// (at your option) any later version.
//
// This program is distributed in the hope that it will be useful,
// but WITHOUT ANY WARRANTY; without even the implied warranty of
// MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
// GNU General Public License for more details.
//
// You should have received a copy of the GNU General Public License
// along with this program. If not, see <https://www.gnu.org/licenses/>.

using System;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Globalization;
using System.IO;
using System.Management;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Windows.Forms;

namespace LightFixer
{
    // ---------------------------------------------------------------- settings
    internal sealed class Settings
    {
        public string Level = "low";        // current level: "low" | "high" | "off"
        public string Startup = "last";     // on boot: "off" | "low" | "high" | "last"
        public string LastLevel = "low";    // level that was in use before shutdown
        public int IdleSeconds = 5;         // 0 = never off
        public bool Enabled = true;
        public string Control = "ours";     // "ours" | "native"
        public bool MouseWake = true;
        public bool BootLight = true;       // light at the password screen, before sign-in
        public string Lang = "";            // interface language: "ru" | "en"
        public int FwTimeout = 0;           // the laptop's own timeout, kept to restore it on exit
        public string Path;

        public int Version;

        public void Load()
        {
            lock (this)
            {
                try
                {
                    if (!File.Exists(Path)) return;
                    string t = File.ReadAllText(Path, Encoding.UTF8);
                    Match m = Regex.Match(t, "\"level\"\\s*:\\s*\"([^\"]+)\"");
                    if (m.Success) Level = m.Groups[1].Value;
                    m = Regex.Match(t, "\"idleSeconds\"\\s*:\\s*(-?\\d+)");
                    if (m.Success) int.TryParse(m.Groups[1].Value, out IdleSeconds);
                    m = Regex.Match(t, "\"service\"\\s*:\\s*(true|false)");
                    if (m.Success) Enabled = m.Groups[1].Value == "true";
                    m = Regex.Match(t, "\"control\"\\s*:\\s*\"([^\"]+)\"");
                    if (m.Success) Control = m.Groups[1].Value;
                    m = Regex.Match(t, "\"mouseWake\"\\s*:\\s*(true|false)");
                    if (m.Success) MouseWake = m.Groups[1].Value == "true";
                    m = Regex.Match(t, "\"bootLight\"\\s*:\\s*(true|false)");
                    if (m.Success) BootLight = m.Groups[1].Value == "true";
                    m = Regex.Match(t, "\"startup\"\\s*:\\s*\"([^\"]+)\"");
                    if (m.Success) Startup = m.Groups[1].Value;
                    m = Regex.Match(t, "\"lastLevel\"\\s*:\\s*\"([^\"]+)\"");
                    if (m.Success) LastLevel = m.Groups[1].Value;
                    m = Regex.Match(t, "\"lang\"\\s*:\\s*\"([^\"]+)\"");
                    if (m.Success) Lang = m.Groups[1].Value;
                    m = Regex.Match(t, "\"fwTimeout\"\\s*:\\s*(\\d+)");
                    if (m.Success) int.TryParse(m.Groups[1].Value, out FwTimeout);
                }
                catch { }

                // a damaged file must not produce nonsense values
                if (IdleSeconds < 0 || IdleSeconds > 86399) IdleSeconds = 5;
                if (Level != "low" && Level != "high") Level = "off";
                if (LastLevel != "low" && LastLevel != "high") LastLevel = "low";
                if (Control != "native") Control = "ours";
                if (Startup != "off" && Startup != "low" && Startup != "high") Startup = "last";
            }
        }

        public bool Save()
        {
            lock (this)
            {
                try
                {
                    string json = "{\n" +
                        "    \"level\":  \"" + Level + "\",\n" +
                        "    \"startup\":  \"" + Startup + "\",\n" +
                        "    \"lastLevel\":  \"" + LastLevel + "\",\n" +
                        "    \"service\":  " + (Enabled ? "true" : "false") + ",\n" +
                        "    \"control\":  \"" + Control + "\",\n" +
                        "    \"mouseWake\":  " + (MouseWake ? "true" : "false") + ",\n" +
                        "    \"bootLight\":  " + (BootLight ? "true" : "false") + ",\n" +
                        "    \"idleSeconds\":  " + IdleSeconds.ToString(CultureInfo.InvariantCulture) + ",\n" +
                        "    \"lang\":  \"" + Lang + "\",\n" +
                        "    \"fwTimeout\":  " + FwTimeout.ToString(CultureInfo.InvariantCulture) + "\n" +
                        "}\n";
                    // write to a temporary file first: a crash mid-write must not wipe the settings
                    string tmp = Path + ".tmp";
                    File.WriteAllText(tmp, json, new UTF8Encoding(false));
                    if (File.Exists(Path)) File.Replace(tmp, Path, Path + ".bak");
                    else File.Move(tmp, Path);
                    Interlocked.Increment(ref Version);
                    return true;
                }
                catch (Exception ex)
                {
                    // the reason belongs in the log; the window shows a short, plain message
                    try { Engine.Log("settings not saved: " + ex); } catch { }
                    return false;
                }
            }
        }
    }

    // ------------------------------------------------------------------ engine
    internal static class Engine
    {
        private const int ModeOff = 2;
        private const int ModeLow = 3;
        private const int ModeHigh = 4;

        [StructLayout(LayoutKind.Sequential)]
        private struct LASTINPUTINFO { public uint cbSize; public uint dwTime; }

        [DllImport("user32.dll")] private static extern bool GetLastInputInfo(ref LASTINPUTINFO plii);

        private delegate IntPtr HookProc(int nCode, IntPtr wParam, IntPtr lParam);
        [DllImport("user32.dll")] private static extern IntPtr SetWindowsHookEx(int idHook, HookProc lpfn, IntPtr hMod, uint dwThreadId);
        [DllImport("user32.dll")] private static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);
        [DllImport("user32.dll")] private static extern bool GetMessage(out MSG lpMsg, IntPtr hWnd, uint wMsgFilterMin, uint wMsgFilterMax);
        [DllImport("user32.dll")] private static extern bool TranslateMessage(ref MSG lpMsg);
        [DllImport("user32.dll")] private static extern IntPtr DispatchMessage(ref MSG lpMsg);

        [StructLayout(LayoutKind.Sequential)]
        private struct MSG
        {
            public IntPtr hwnd; public uint message; public IntPtr wParam;
            public IntPtr lParam; public uint time; public int ptX; public int ptY;
        }

        private static Settings S;
        private static string logPath;
        private static ManagementObject dev;

        private static volatile bool stateOn;
        private static bool levelOff;
        private static int levelMode = ModeLow;
        private static int expected = -1;
        private static bool stateKnown;
        private static string appliedControl = "";
        private static DateTime startupGrace = DateTime.MinValue;
        private static DateTime suppressUntil = DateTime.MinValue;
        private static string lastLogState = "";
        private static DateTime lastErrLog = DateTime.MinValue;
        private static int seenVersion = -1;

        // 16 is the firmware's own "auto" mode: the laptop starts deciding on its own
        // when the light is on. We do not let it stay there and take the light back.
        private const int ModeAuto = 16;
        // after Fn+Space turned the light on by hand it must stay on for a while,
        // otherwise the idle rule puts it out again at once and the press looks dead
        private static DateTime fnHoldUntil = DateTime.MinValue;
        // Fn+Space switched the light off by hand: the light stays dark until Fn+Space
        // brings it back or the user saves the settings, which always win
        private static bool userKilled;
        // after sleep the laptop changes the light by itself; that must not be taken for
        // a press of Fn+Space, so for a while nothing is adopted as the user's choice
        private static DateTime resumeGrace = DateTime.MinValue;
        private static DateTime lastLoopTick = DateTime.MinValue;
        private static int obsLast = -999;
        // the 8-second grace after taking the light over belongs to the program's start
        // only, never to a settings save in the window
        private static bool initialSetupDone;

        private static volatile uint lastKeyTick = unchecked((uint)Environment.TickCount);
        private static volatile bool hookFailed;
        private static HookProc keyProc;

        public static bool LightIsOn { get { return stateOn; } }
        public static int CurrentMode { get { return expected; } }

        public static void Start(Settings settings)
        {
            S = settings;

            string dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "LightFixer");
            try { Directory.CreateDirectory(dir); } catch { }
            logPath = Path.Combine(dir, "lightfixer.log");

            Thread t = new Thread(Loop);
            t.IsBackground = true;
            t.Name = "LightFixer engine";
            t.Start();
        }

        private static readonly object logLock = new object();

        public static void Log(string msg)
        {
            try
            {
                if (logPath == null) return;
                lock (logLock)
                {
                    string line = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + "  " + msg + Environment.NewLine;
                    File.AppendAllText(logPath, line, Encoding.UTF8);
                    FileInfo fi = new FileInfo(logPath);
                    if (fi.Length > 256 * 1024)
                    {
                        string[] all = File.ReadAllLines(logPath, Encoding.UTF8);
                        int keep = Math.Max(0, all.Length - 400);
                        File.WriteAllText(logPath, string.Join(Environment.NewLine, all, keep, all.Length - keep) + Environment.NewLine, Encoding.UTF8);
                    }
                }
            }
            catch { }
        }

        // ---------- WMI ----------
        private static bool Connect()
        {
            if (dev != null) return true;
            try
            {
                ManagementObjectSearcher s = new ManagementObjectSearcher(@"root\wmi", "SELECT * FROM OemWMIMethod");
                ManagementObject fallback = null;
                foreach (ManagementObject o in s.Get())
                {
                    string name = Convert.ToString(o["InstanceName"]);
                    if (name.IndexOf("HWMI_0", StringComparison.OrdinalIgnoreCase) >= 0) { dev = o; return true; }
                    if (fallback == null) fallback = o;
                }
                dev = fallback;
                return dev != null;
            }
            catch { dev = null; return false; }
        }

        private static int Send(ushort baseCmd, ushort value, out int data)
        {
            data = -1;
            if (!Connect()) return 255;
            try
            {
                ulong cmd = (ulong)baseCmd | ((ulong)value << 16);
                byte[] buf = new byte[64];
                Array.Copy(BitConverter.GetBytes(cmd), buf, 8);
                ManagementBaseObject inP = dev.GetMethodParameters("OemWMIfun");
                inP["u8Input"] = buf;
                ManagementBaseObject outP = dev.InvokeMethod("OemWMIfun", inP, null);
                byte[] outBuf = outP["u8Output"] as byte[];
                if (outBuf != null && outBuf.Length >= 2) { data = outBuf[1]; return outBuf[0]; }
                return 255;
            }
            catch { dev = null; return 255; }
        }

        private static int GetMode() { int v; Send(0x1306, 0, out v); return v; }
        private static int GetTimeout() { int v; Send(0x1206, 0, out v); return v; }
        private static int SetMode(int mode) { int v; return Send(0x1406, (ushort)mode, out v); }
        private static int SetTimeout(int seconds) { int v; return Send(0x1106, (ushort)seconds, out v); }

        // The keyboard is lit at the password screen: Windows runs this at power-on,
        // before anybody signs in. It only puts the light on the saved level and makes
        // sure the laptop's own timer cannot put it out a minute later. Then it leaves;
        // the tray copy started at sign-in carries on as usual.
        // The log goes next to the program (not to the system profile), so the start
        // before sign-in leaves no trace anywhere else on the laptop.
        public static void BootApply(Settings cfg)
        {
            string dir = Path.GetDirectoryName(System.Reflection.Assembly.GetExecutingAssembly().Location);
            logPath = Path.Combine(dir, "boot.log");
            try
            {
                if (cfg == null) { BootNote("boot: settings were not read, nothing done"); return; }
                // the tick in the window can be removed: then the task may still exist
                // for a while, and it must do nothing at all
                if (!cfg.BootLight) { BootNote("boot: switched off in the window, nothing done"); return; }
                string lvl = cfg.Startup;
                if (lvl != "off" && lvl != "low" && lvl != "high") lvl = cfg.LastLevel;
                if (lvl != "off" && lvl != "low" && lvl != "high") lvl = "low";
                BootNote("boot start: level " + lvl + ", control " + cfg.Control);
                if (cfg.Control != "ours")
                {
                    BootNote("boot: HONOR mode, the laptop keeps the light");
                    return;
                }
                int m = (lvl == "off") ? ModeOff : (lvl == "high" ? ModeHigh : ModeLow);
                int rc = SetMode(m);
                BootNote("boot: light " + m + " rc=" + rc);
                int trc = SetTimeout(0);
                BootNote("boot: laptop timer set to never rc=" + trc + ", done");
            }
            catch (Exception ex) { BootNote("boot failed: " + ex); }
        }

        // written straight to the file next to the program: the usual journal lives in
        // the profile of whoever is signed in, and at power-on nobody is signed in yet
        private static void BootNote(string msg)
        {
            try
            {
                string path = logPath;
                if (path == null) return;
                lock (logLock) File.AppendAllText(path, DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + "  " + msg + Environment.NewLine, Encoding.UTF8);
            }
            catch { }
        }

        // the program leaves: the firmware timeout it suppressed is put back, so the
        // laptop handles the light again exactly as it did before the program was
        // ever installed
        public static void HandBackToLaptop()
        {
            try
            {
                if (S.Control != "ours") return;
                int t = (S.FwTimeout > 0) ? S.FwTimeout : 15;
                int rc = SetTimeout(t);
                Log("exit: backlight returned to the laptop, timeout " + t + "s rc=" + rc);
            }
            catch { }
        }

        // ---------- idle ----------
        private static long IdleMsAll()
        {
            LASTINPUTINFO li = new LASTINPUTINFO();
            li.cbSize = (uint)Marshal.SizeOf(li);
            if (!GetLastInputInfo(ref li)) return 0;
            uint now = unchecked((uint)Environment.TickCount);
            return unchecked(now - li.dwTime);
        }

        private static void StartKeyboardHook()
        {
            Thread t = new Thread(delegate()
            {
                keyProc = delegate(int nCode, IntPtr wParam, IntPtr lParam)
                {
                    if (nCode >= 0 && (wParam == (IntPtr)0x100 || wParam == (IntPtr)0x104))
                        lastKeyTick = unchecked((uint)Environment.TickCount);
                    return CallNextHookEx(IntPtr.Zero, nCode, wParam, lParam);
                };
                IntPtr hk = SetWindowsHookEx(13, keyProc, IntPtr.Zero, 0);
                if (hk == IntPtr.Zero)
                {
                    // without the hook the light would never wake from keys when the
                    // mouse option is off; fall back to the system idle timer
                    Log("WARN: keyboard hook failed, err=" + Marshal.GetLastWin32Error());
                    hookFailed = true;
                }
                MSG msg;
                while (GetMessage(out msg, IntPtr.Zero, 0, 0))
                {
                    TranslateMessage(ref msg);
                    DispatchMessage(ref msg);
                }
            });
            t.IsBackground = true;
            t.Start();
        }

        private static long IdleMs()
        {
            // the hook may be unavailable: then the system idle timer is the only source
            if (S.MouseWake || hookFailed) return IdleMsAll();
            uint now = unchecked((uint)Environment.TickCount);
            return unchecked(now - lastKeyTick);
        }

        internal static void ResetFromSettings()
        {
            if (S.Level == "high") { levelMode = ModeHigh; levelOff = false; }
            else if (S.Level == "low") { levelMode = ModeLow; levelOff = false; }
            else levelOff = true;
            userKilled = false;      // saved settings always win over a switched-off light
            startupGrace = DateTime.MinValue;
            fnHoldUntil = DateTime.MinValue;
            stateKnown = false;
            appliedControl = "";
        }

        // ---- noticing presses of Fn+Space ----
        // when we last switched the light off ourselves: a dark light the user asked for
        // stays dark until the next press, the idle rule must not fight that.
        private static DateTime lastOffAt = DateTime.MinValue;

        // The firmware has FOUR steps: off (2) -> auto (16) -> low (3) -> high (4) -> off.
        // Windows never sees Fn+Space at all, so the only way to know a key was pressed is
        // to look at the light: a change we did not make ourselves is the user's hand.
        //
        // A press is remembered and NEVER overridden. Every attempt to "correct" the
        // firmware - turning auto into low, putting the light back after a press - was
        // itself what made quick presses vanish and the popup never appear.
        private static void AdoptUserChoice(int m, DateTime now)
        {
            // a press of Fn+Space is a switch of the moment, it must not rewrite the
            // settings the user saved in the window - those live until he saves again
            if (m == ModeOff)
            {
                userKilled = true;
                lastOffAt = now;
                fnHoldUntil = now;                       // an "off" is an "off": no timer fights it
            }
            else
            {
                userKilled = false;
                // "auto" (16) is the second step and looks exactly like "low", so the
                // brightness level is only taken from a real low or high press
                if (m == ModeLow || m == ModeHigh) levelMode = m;
                // the light was asked for by hand: it stays lit for the pause set in the
                // window, exactly like after any other key press - no hidden extra minute,
                // which used to keep the light on for a whole minute against the setting
                fnHoldUntil = now.AddSeconds(S.IdleSeconds);
            }
            stateOn = (m != ModeOff);
            expected = m;
            stateKnown = true;
            Log("Fn+Space: light " + (m == ModeOff ? "off" : (m == ModeHigh ? "high" : (m == ModeAuto ? "auto" : "low"))));
        }

        // Fn+Space must be noticed before the idle rule makes up its mind, otherwise the
        // press is taken for our own light and quietly put out again five seconds later.
        private static void NoticeUserSwitch(DateTime now)
        {
            if (S.Control != "ours") return;      // "Штатные настройки HONOR": hands off
            if (!S.Enabled) return;

            int m = GetMode();
            if (m < 0) return;
            if (m == expected) return;            // our own light, nothing to learn
            if (m != ModeOff && m != ModeLow && m != ModeHigh && m != ModeAuto) return;

            // right after waking the light is changed by the laptop itself, not by a hand
            if (now < resumeGrace) { stateOn = (m != ModeOff); return; }
            if (now < startupGrace) return;

            // "auto" (16) is the second step of the cycle and its light looks exactly like
            // "low", so a press would show "low" twice in a row. It is turned into a real
            // low at once, and that makes the cycle exactly off -> low -> high -> off.
            // The switch is made ONCE, at the moment the step is seen - never from the
            // running loop: a repeated write is what used to swallow quick presses.
            if (m == ModeAuto)
            {
                int rc = SetMode(ModeLow);
                if (rc != 0) Log("auto -> low failed rc=" + rc);
                AdoptUserChoice(ModeLow, now);
                return;
            }

            AdoptUserChoice(m, now);
        }

        // what the keyboard must be on right after Windows starts
        private static void ApplyStartupChoice()
        {
            if (S.Startup == "off") S.Level = "off";
            else if (S.Startup == "low") S.Level = "low";
            else if (S.Startup == "high") S.Level = "high";
            else S.Level = S.LastLevel;   // "как было в прошлый раз"
            S.Save();
        }

        // ---------- main loop ----------
        private static void Loop()
        {
            try { LoopBody(); }
            catch (Exception ex) { Log("FATAL: engine stopped: " + ex); }
        }

        private static void LoopBody()
        {
            ApplyStartupChoice();
            seenVersion = S.Version;
            ResetFromSettings();
            StartKeyboardHook();
            Log("LightFixer started; level=" + S.Level + " idle=" + S.IdleSeconds + "s control=" + S.Control + " mouseWake=" + S.MouseWake);

            DateTime deadline = DateTime.Now.AddSeconds(120);
            while (DateTime.Now < deadline && !Connect()) Thread.Sleep(3000);
            Log(dev != null ? "BIOS WMI ready" : "ERROR: BIOS WMI unavailable");

            DateTime lastSettings = DateTime.MinValue;
            DateTime lastMode = DateTime.MinValue;
            DateTime lastTimeout = DateTime.MinValue;
            bool fwRead = false;

            while (true)
            {
                try
                {
                    DateTime now = DateTime.Now;

                    // the loop ticks five times a second; a gap means the laptop was asleep
                    if ((now - lastLoopTick).TotalSeconds > 5)
                    {
                        if (lastLoopTick != DateTime.MinValue) Log("wake-up: the light is left alone for 20 s");
                        resumeGrace = now.AddSeconds(20);
                    }
                    lastLoopTick = now;

                    if (S.Version != seenVersion)
                    {
                        seenVersion = S.Version;
                        ResetFromSettings();
                        lastTimeout = DateTime.MinValue;
                        lastMode = DateTime.MinValue;
                    }
                    else if ((now - lastSettings).TotalSeconds >= 3)
                    {
                        lastSettings = now;
                        S.Load();
                    }

                    // diagnostic mode: a file "observe.txt" next to the program makes it
                    // only watch the firmware and write down every change, touching nothing.
                    // it is used to learn the real Fn+Space cycle instead of guessing it.
                    if (File.Exists(Path.Combine(Path.GetDirectoryName(Application.ExecutablePath), "observe.txt")))
                    {
                        int om = GetMode();
                        if (om != obsLast)
                        {
                            obsLast = om;
                            Log("obs " + now.ToString("HH:mm:ss.fff") + " mode=" + om + " idle=" + (IdleMs() / 1000) + "s");
                        }
                        Thread.Sleep(40);
                        continue;
                    }

                    if ((now - lastTimeout).TotalSeconds >= 20)
                    {
                        lastTimeout = now;
                        // in HONOR mode we do not touch the BIOS at all
                        if (S.Control == "ours")
                        {
                            // remember the laptop's own timeout: it is put back on exit and
                            // whenever HONOR takes the light over, so its utility never shows
                            // "never" because of us. Read again after every return from HONOR
                            // mode, where the user may have changed it in HONOR's own window.
                            if (!fwRead)
                            {
                                int t = GetTimeout();
                                // only a real pause is stored, and only when it changed:
                                // saving on every read bumped the settings version, which
                                // reset the state and made the whole loop spin forever
                                if (t > 0 && t != S.FwTimeout) { S.FwTimeout = t; S.Save(); }
                                fwRead = true;
                                Log("laptop's own pause read: " + t + "s");
                            }
                            SetTimeout(0);
                        }
                    }

                    if (!S.Enabled)
                    {
                        if (stateOn) { SetMode(ModeOff); expected = ModeOff; stateOn = false; lastOffAt = now; }
                        if (lastLogState != "disabled") { Log("disabled by settings"); lastLogState = "disabled"; }
                        Thread.Sleep(1000);
                        continue;
                    }

                    if (appliedControl != S.Control)
                    {
                        appliedControl = S.Control;
                        Log("control mode: " + S.Control);
                        // the switch takes effect at once, not on the next start
                        if (S.Control == "ours")
                        {
                            // HONOR may have been used in between: its pause is read again
                            // before we take the light over and put the pause to zero
                            fwRead = false;
                            lastTimeout = DateTime.MinValue;
                        }
                        else
                        {
                            // HONOR takes the light over: its own pause goes back at once.
                            // without this the laptop keeps our "never" and HONOR's utility
                            // shows "never" instead of the 15 seconds the user had set there
                            int cur = GetTimeout();
                            int t = S.FwTimeout;
                            if (cur == 0 && t > 0)
                            {
                                int trc = SetTimeout(t);
                                Log("HONOR mode: the laptop's own pause " + t + "s put back rc=" + trc);
                            }
                            else
                            {
                                Log("HONOR mode: the laptop's own pause is " + cur + "s, left alone");
                            }
                        }
                    }

                    // "native": HONOR sets the light, we only look at it and never command it
                    if (!stateKnown && S.Control != "native")
                    {
                        int m = GetMode();
                        expected = m;
                        stateOn = (m != ModeOff);
                        stateKnown = true;
                        if (!levelOff)
                        {
                            int rc = SetMode(levelMode);
                            if (rc == 0) { stateOn = true; expected = levelMode; }
                            Log("light set to " + levelMode + " rc=" + rc + "; idle " + S.IdleSeconds + "s");
                            // the short grace belongs to the program's own start only. It used
                            // to be applied after every save too, so a newly set idle pause was
                            // ignored for eight seconds and the light stayed on "a bit longer"
                            if (!initialSetupDone) startupGrace = now.AddSeconds(8);
                        }
                        else
                        {
                            SetMode(ModeOff);
                            expected = ModeOff;
                            lastOffAt = now;
                            Log("level is off, keeping dark");
                            if (!initialSetupDone) startupGrace = now.AddSeconds(8);
                        }
                        initialSetupDone = true;
                        lastMode = now;
                    }

                    if ((now - lastMode).TotalSeconds >= 8)
                    {
                        lastMode = now;
                        if (S.Control != "native")
                        {
                            int m = GetMode();
                            if (m >= 0 && m != expected)
                            {
                                // the firmware changed the light behind our back (sleep, wake-up):
                                // only the state is noted, the mode itself is left alone
                                stateOn = (m != ModeOff);
                                Log("light changed outside: mode " + m);
                            }
                        }
                    }

                    NoticeUserSwitch(now);   // Fn+Space first, the idle rule after it

                    if (S.Control != "native")
                    {
                        long idleMs = IdleMs();
                        bool shouldBeOn = true;
                        if (levelOff || userKilled) shouldBeOn = false;
                        else if (now < startupGrace) shouldBeOn = true;
                        else if (now < fnHoldUntil) shouldBeOn = true;   // just switched on by Fn+Space
                        else if (S.IdleSeconds > 0) shouldBeOn = idleMs < (S.IdleSeconds * 1000L);

                        if (shouldBeOn && !stateOn)
                        {
                            int rc = SetMode(levelMode);
                            if (rc == 0) { stateOn = true; expected = levelMode; }
                            if (lastLogState != "on") { Log("ON at " + levelMode + " (idle " + (idleMs / 1000) + "s rc=" + rc + ")"); lastLogState = "on"; }
                        }
                        else if (!shouldBeOn && stateOn)
                        {
                            int rc = SetMode(ModeOff);
                            if (rc == 0) { stateOn = false; expected = ModeOff; lastOffAt = now; }
                            if (lastLogState != "off") { Log("OFF after " + (idleMs / 1000) + "s idle (rc=" + rc + ")"); lastLogState = "off"; }
                        }

                        // nothing is put back on purpose: a press of Fn+Space always wins, and
                        // the only thing the program does by itself is going dark after idle
                        // and coming back when the user touches the keyboard or the mouse.
                    }
                }
                catch (Exception ex)
                {
                    dev = null;
                    if ((DateTime.Now - lastErrLog).TotalSeconds >= 30)
                    {
                        lastErrLog = DateTime.Now;
                        Log("ERROR: " + ex.Message);
                    }
                    Thread.Sleep(2000);
                }

                // 100 ms, not 200: Fn+Space passes through the firmware's "auto" state for a
                // very short moment, and a slower look misses it - the light then stays in
                // auto and the press appears not to work.
                Thread.Sleep((S.Control == "ours" && S.Enabled) ? 100 : 400);
            }
        }
    }

    // -------------------------------------------------------------------- app
    internal static class Program
    {
        private const string TaskName = "LightFixer BRI-721";

        // the author's own CloudTips page: opens straight on the donation form
        private const string DonateUrl = "https://pay.cloudtips.ru/p/36cd28db";

        private static Settings S = new Settings();
        private static Form form;
        private static NotifyIcon tray;
        private static Icon appIcon;
        private static bool dirty, loading;
        private static RadioButton rbOff, rbOn, rbLast;
        private static Segmented segLevel;   // low / high, a switch inside "always on"
        private static Segmented segLang;    // kept in a field: a refused language change must put it back
        private static DropDown cbTime;
        private static CheckBox cbMouse;
        private static CheckBox cbAutostart, cbBoot;   // program start: with Windows, before sign-in
        private static CardPanel cardBehaviour, cardStartup, cardSupport;
        // kept in a field on purpose: a ToolTip that nothing references is collected
        // by the garbage collector and the hints silently stop appearing
        private static ToolTip tipStartup;
        private static Segmented segMode;
        private static Label lblIdleHint, lblStatus, lblModeHint, lblCard1, lblCard2;
        private static Label lblOwnTime, lblOwnH, lblOwnM, lblOwnS;
        private static SpinBox nHours, nMinutes, nSeconds;
        private static PictureBox picStatus;
        private static System.Windows.Forms.Timer tick;

        // -1 means "your own value", typed in the three boxes under the list
        private static readonly int[] IdleValues =
            { 0, 1, 2, 3, 5, 10, 15, 20, 30, 45, 60, 120, 180, 300, 600, 900, 1800, 3600, 7200, -1 };

        // interface language: S.Lang is "ru" or "en"; an empty value means the user
        // has not chosen yet, then it follows the Windows display language
        private static bool En { get { return S.Lang == "en"; } }

        private static string L(string ru, string en) { return En ? en : ru; }

        private static string LangFromSystem()
        {
            string c = "";
            try { c = CultureInfo.CurrentUICulture.TwoLetterISOLanguageName; }
            catch { }
            return (c == "ru") ? "ru" : "en";
        }

        private static string[] IdleLabelsNow()
        {
            return new string[]
            {
                L("Никогда (горит всё время)", "Never (always on)"),
                L("1 секунда", "1 second"),
                L("2 секунды", "2 seconds"),
                L("3 секунды", "3 seconds"),
                L("5 секунд", "5 seconds"),
                L("10 секунд", "10 seconds"),
                L("15 секунд", "15 seconds"),
                L("20 секунд", "20 seconds"),
                L("30 секунд", "30 seconds"),
                L("45 секунд", "45 seconds"),
                L("1 минута", "1 minute"),
                L("2 минуты", "2 minutes"),
                L("3 минуты", "3 minutes"),
                L("5 минут", "5 minutes"),
                L("10 минут", "10 minutes"),
                L("15 минут", "15 minutes"),
                L("30 минут", "30 minutes"),
                L("1 час", "1 hour"),
                L("2 часа", "2 hours"),
                L("Своё время…", "Custom time…")
            };
        }

        private static SpinBox MakeSpin(int min, int max)
        {
            SpinBox n = new SpinBox();
            n.Minimum = min;
            n.Maximum = max;
            n.Cursor = Cursors.Hand;
            return n;
        }

        private static Label MakeUnit(string text)
        {
            Label l = new Label();
            l.Text = text;
            l.ForeColor = CHint;
            l.TextAlign = ContentAlignment.MiddleLeft;
            return l;
        }

        // true when the list shows "your own value"
        private static bool OwnTimeSelected { get { return cbTime != null && cbTime.SelectedIndex == IdleValues.Length - 1; } }

        private static int OwnSeconds
        {
            get
            {
                int sec = (int)nHours.Value * 3600 + (int)nMinutes.Value * 60 + (int)nSeconds.Value;
                return sec;
            }
        }

        // the three boxes are shown only for "your own value"
        private static void ShowOwnTime(bool show)
        {
            if (lblOwnTime == null) return;
            show = true;   // the three boxes stay in place: the card must not jump
            lblOwnTime.Visible = show;
            nHours.Visible = show;
            nMinutes.Visible = show;
            nSeconds.Visible = show;
            lblOwnH.Visible = show;
            lblOwnM.Visible = show;
            lblOwnS.Visible = show;
        }

        // "1 ч 2 мин 3 с" — how the typed value is shown back to the user
        private static string PrettySeconds(int sec)
        {
            if (sec <= 0) return L("не задано", "not set");
            int h = sec / 3600, m = (sec % 3600) / 60, s = sec % 60;
            string r = "";
            if (h > 0) r += h + L(" ч ", " h ");
            if (m > 0) r += m + L(" мин ", " min ");
            if (s > 0) r += s + L(" с", " s");
            return r.Trim();
        }

        private static void IdleHintNow()
        {
            if (lblIdleHint == null || cbTime == null) return;
            bool always = !OwnTimeSelected && IdleValues[cbTime.SelectedIndex] == 0;
            if (always)
            {
                lblIdleHint.Text = L("подсветка горит всё время, пока ноутбук включён",
                                     "the backlight stays on while the laptop is on");
                return;
            }
            bool mouse = (cbMouse != null) ? cbMouse.Checked : S.MouseWake;
            string t = mouse
                ? L("если ничего не нажимать и не двигать мышью", "if nothing is pressed and the mouse does not move")
                : L("если не нажимать клавиши", "if no key is pressed");
            if (OwnTimeSelected) t += "  ·  " + PrettySeconds(OwnSeconds);
            lblIdleHint.Text = t;
        }

        private static string BaseDir { get { return Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location); } }

        [DllImport("user32.dll")] private static extern bool SetProcessDPIAware();
        [DllImport("user32.dll")] private static extern uint GetDpiForSystem();
        [DllImport("user32.dll")] private static extern bool DestroyIcon(IntPtr hIcon);

        [STAThread]
        private static void Main(string[] args)
        {
            // must be called before any window exists, otherwise Windows blows the
            // window up to 175 % and it slides off the screen on a scaled display
            try { SetProcessDPIAware(); } catch { }

            bool hidden = false;
            bool boot = false;
            foreach (string a in args)
            {
                if (a.Equals("/tray", StringComparison.OrdinalIgnoreCase) || a.Equals("-tray", StringComparison.OrdinalIgnoreCase)) hidden = true;
                if (a.Equals("/boot", StringComparison.OrdinalIgnoreCase) || a.Equals("-boot", StringComparison.OrdinalIgnoreCase)) boot = true;
            }

            // "/boot" is the task that Windows runs at power-on, BEFORE the password is
            // typed: it lights the keyboard and leaves at once. No window, no tray, no
            // second copy - the usual "/tray" start at sign-in takes over afterwards.
            // It is handled before the single-copy check so the two never argue.
            if (boot)
            {
                S.Path = Path.Combine(BaseDir, "settings.json");
                S.Load();
                Engine.BootApply(S);
                return;
            }

            Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
            Application.ThreadException += delegate(object s, ThreadExceptionEventArgs e)
            {
                Engine.Log("FATAL ui: " + e.Exception);
            };
            AppDomain.CurrentDomain.UnhandledException += delegate(object s, UnhandledExceptionEventArgs e)
            {
                Engine.Log("FATAL app: " + e.ExceptionObject);
            };

            bool created;
            Mutex mtx = new Mutex(true, "LightFixer_SingleInstance", out created);
            if (!created)
            {
                if (!hidden) MessageBox.Show(L("LightFixer BRI-721 уже запущен. Его значок — в трее, рядом с часами.", "LightFixer BRI-721 is already running. Its icon is in the tray, next to the clock."), "LightFixer BRI-721");
                return;
            }

            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);

            S.Path = Path.Combine(BaseDir, "settings.json");
            S.Load();
            if (S.Lang != "ru" && S.Lang != "en") S.Lang = LangFromSystem();

            // autostart is a fixed behaviour now, not a switch: the task is always
            // created, and an existing one is refreshed (the program may have moved)
            if (TaskExists())
            {
                bool wasOn = TaskEnabled();
                InstallTask();
                if (!wasOn) Run("schtasks.exe", "/Change /TN \"" + TaskName + "\" /DISABLE");
            }
            else
            {
                InstallTask();
            }
            taskChecked = DateTime.MinValue;

            Engine.Start(S);
            BuildForm();
            BuildTray();

            if (!hidden) { form.Show(); form.Activate(); }
            Application.Run();
        }

        // ---------- helpers ----------
        private static string Run(string exe, string args)
        {
            try
            {
                ProcessStartInfo psi = new ProcessStartInfo(exe, args);
                psi.UseShellExecute = false;
                psi.RedirectStandardOutput = true;
                psi.RedirectStandardError = true;
                psi.CreateNoWindow = true;
                // schtasks prints in the OEM code page, not in UTF-8
                try { psi.StandardOutputEncoding = Encoding.GetEncoding(866); psi.StandardErrorEncoding = Encoding.GetEncoding(866); } catch { }
                using (Process p = Process.Start(psi))
                {
                    string o = p.StandardOutput.ReadToEnd() + p.StandardError.ReadToEnd();
                    p.WaitForExit(20000);
                    return o;
                }
            }
            catch { return "ERROR"; }
        }

        // exit code is the only reliable answer: the word "error" is localised
        private static bool RunOk(string exe, string args)
        {
            try
            {
                ProcessStartInfo psi = new ProcessStartInfo(exe, args);
                psi.UseShellExecute = false;
                psi.RedirectStandardOutput = true;
                psi.RedirectStandardError = true;
                psi.CreateNoWindow = true;
                using (Process p = Process.Start(psi))
                {
                    p.StandardOutput.ReadToEnd();
                    p.StandardError.ReadToEnd();
                    if (!p.WaitForExit(20000)) return false;
                    return p.ExitCode == 0;
                }
            }
            catch { return false; }
        }

        private static bool TaskExists() { return RunOk("schtasks.exe", "/Query /TN \"" + TaskName + "\""); }

        // asking the scheduler every two seconds spawned about one process per second
        private static DateTime taskChecked = DateTime.MinValue;
        private static bool taskCached;

        private static bool TaskEnabled()
        {
            if (DateTime.Now - taskChecked < TimeSpan.FromSeconds(30)) return taskCached;
            taskChecked = DateTime.Now;
            bool res = false;
            if (TaskExists())
            {
                string o = Run("schtasks.exe", "/Query /TN \"" + TaskName + "\" /FO LIST");
                res = o.IndexOf("Disabled", StringComparison.OrdinalIgnoreCase) < 0
                   && o.IndexOf("Отключено", StringComparison.OrdinalIgnoreCase) < 0;
            }
            taskCached = res;
            return res;
        }

        private static void InstallTask()
        {
            // the real path of the running exe, not a guessed one
            string exe = Assembly.GetExecutingAssembly().Location;
            if (string.IsNullOrEmpty(exe) || !File.Exists(exe)) exe = Path.Combine(BaseDir, "LightFixer-BRI-721.exe");
            // started at sign-in, without the old 15-second wait: the light comes on
            // as soon as the desktop appears
            Run("schtasks.exe", "/Create /TN \"" + TaskName + "\" /TR \"\\\"" + exe + "\\\" /tray\" " +
                "/SC ONLOGON /RL HIGHEST /F");
            RelaxTask(TaskName);
            InstallBootTask();
            taskChecked = DateTime.MinValue;
        }

        // The same program started at power-on, BEFORE anybody signs in, as the system
        // account: the keyboard is already lit while the password is typed. It puts the
        // light on the saved level and quits at once, no window and no second copy; the
        // "/tray" copy above takes over after sign-in.
        // The tick in the window decides this: removed, the task is deleted as well, and
        // then the laptop is left exactly as the maker shipped it.
        private static void InstallBootTask()
        {
            string name = TaskName + " (загрузка)";
            if (!S.BootLight)
            {
                if (RunOk("schtasks.exe", "/Query /TN \"" + name + "\""))
                    Run("schtasks.exe", "/Delete /TN \"" + name + "\" /F");
                return;
            }
            string exe = Assembly.GetExecutingAssembly().Location;
            if (string.IsNullOrEmpty(exe) || !File.Exists(exe)) exe = Path.Combine(BaseDir, "LightFixer-BRI-721.exe");
            Run("schtasks.exe", "/Create /TN \"" + name + "\" /TR \"\\\"" + exe + "\\\" /boot\" " +
                "/SC ONSTART /RU SYSTEM /RL HIGHEST /F");
            RelaxTask(name);
        }

        // The scheduler creates every task with "do not start on battery" and "stop on
        // battery" - and on a laptop that is exactly the case where the light matters.
        // schtasks has no switch for these, so they are turned off right after creation.
        // Without this a task re-created by the program quietly stopped starting after a
        // reboot on battery power.
        private static void RelaxTask(string name)
        {
            try
            {
                Run("powershell.exe", "-NoProfile -ExecutionPolicy Bypass -Command \"Set-ScheduledTask -TaskName '" + name +
                    "' -Settings (New-ScheduledTaskSettingsSet -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries -StartWhenAvailable)\"");
            }
            catch { }
        }

        private static void SetAutostart(bool on)
        {
            if (on) InstallTask();   // /F recreates it, so a moved exe is picked up
            else
            {
                if (!TaskExists()) InstallTask();   // make it first, then switch it off
                Run("schtasks.exe", "/Change /TN \"" + TaskName + "\" /DISABLE");
            }
            taskChecked = DateTime.MinValue;
        }

        // The two ticks under the "start-up" card are not just written to the file:
        // Windows is told to start or stop doing it, so the very next switch-on already
        // behaves exactly as the window shows. Nothing has to be set up by hand.
        private static void ApplyStartUpTicks()
        {
            try
            {
                bool want = (cbAutostart != null) && cbAutostart.Checked;
                if (want != TaskEnabled()) SetAutostart(want);
                InstallBootTask();
                taskChecked = DateTime.MinValue;
            }
            catch (Exception ex) { try { Engine.Log("start-up settings not applied: " + ex.Message); } catch { } }
        }

        private static Icon MakeIcon()
        {
            // the real icon set (16..256) is embedded into the exe; Windows picks
            // the size it needs for the tray, the taskbar, the window and Alt+Tab
            if (appIcon != null) return appIcon;
            try
            {
                Stream st = Assembly.GetExecutingAssembly().GetManifestResourceStream("LightFixer.Icon.ico");
                if (st != null)
                {
                    using (st) { appIcon = new Icon(st); }
                    return appIcon;
                }
            }
            catch { }
            return DummyIcon();
        }

        // fallback only: drawn in code if the embedded resource is somehow missing
        private static Icon DummyIcon()
        {
            Bitmap bmp = new Bitmap(32, 32);
            using (Graphics g = Graphics.FromImage(bmp))
            {
                g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.None;
                g.Clear(Color.Transparent);
                using (Brush bg = new SolidBrush(Color.FromArgb(0x47, 0x52, 0xD6))) g.FillRectangle(bg, 2, 9, 28, 15);
                using (Brush key = new SolidBrush(Color.White))
                {
                    for (int i = 0; i < 5; i++) g.FillRectangle(key, 5 + i * 5, 12, 3, 3);
                    g.FillRectangle(key, 5, 18, 22, 3);
                }
            }
            // FromHandle does not own the HICON: clone the icon and free the handle
            IntPtr h = bmp.GetHicon();
            Icon res = (Icon)Icon.FromHandle(h).Clone();
            DestroyIcon(h);
            bmp.Dispose();
            return res;
        }

        // ---------- tray ----------
        private static void BuildTray()
        {
            tray = new NotifyIcon();
            tray.Icon = MakeIcon();
            tray.Text = L("LightFixer BRI-721 — подсветка клавиатуры HONOR", "LightFixer BRI-721 — HONOR keyboard backlight");
            tray.Visible = true;
            tray.DoubleClick += delegate { ShowWindow(); };

            ContextMenu menu = new ContextMenu();

            MenuItem miOpen = new MenuItem(L("Открыть настройки", "Open settings"));
            miOpen.DefaultItem = true;
            miOpen.Click += delegate { ShowWindow(); };
            menu.MenuItems.Add(miOpen);
            menu.MenuItems.Add("-");

            // the scheduler is the one place that really decides whether the
            // program starts with Windows, so the tick mirrors the task, not a variable
            MenuItem miAuto = new MenuItem(L("Запускать программу при включении компьютера",
                                              "Start the program when the computer starts"));
            miAuto.Checked = TaskEnabled();
            miAuto.Click += delegate
            {
                bool wanted = !miAuto.Checked;
                SetAutostart(wanted);
                miAuto.Checked = TaskEnabled();
                if (miAuto.Checked != wanted)
                    MessageBox.Show(L("Автозапуск не изменился: Windows не дала доступ к Планировщику заданий.",
                                      "Autostart was not changed: Windows denied access to Task Scheduler."),
                                    "LightFixer BRI-721");
            };
            menu.MenuItems.Add(miAuto);
            menu.MenuItems.Add("-");

            MenuItem miAbout = new MenuItem(L("О программе", "About"));
            miAbout.Click += delegate { ShowAbout(); };
            menu.MenuItems.Add(miAbout);

            MenuItem miExit = new MenuItem(L("Выход", "Exit"));
            miExit.Click += delegate { if (TryLeave()) ExitApp(); };
            menu.MenuItems.Add(miExit);

            tray.ContextMenu = menu;

            tick = new System.Windows.Forms.Timer();
            tick.Interval = 2000;
            tick.Tick += delegate { UpdateUi(); };
            tick.Start();

            UpdateUi();
        }

        private static void SetLang(string lang)
        {
            if (S.Lang == lang) return;
            // do not lose unsaved changes; the switch has already moved, so put it back
            if (!TryLeave()) { if (segLang != null) segLang.SelectedIndex = En ? 1 : 0; return; }
            S.Lang = lang;
            S.Save();
            SwitchLanguage();
        }

        private static void SwitchLanguage()
        {
            // the tray menu and the window are drawn once, so both are rebuilt
            if (tick != null) { tick.Stop(); tick.Dispose(); tick = null; }
            if (tray != null) { tray.Visible = false; tray.Dispose(); tray = null; }
            BuildTray();
            bool shown = (form != null) && form.Visible;
            if (form != null) { form.Hide(); form.Dispose(); form = null; }
            BuildForm();
            if (shown) { form.Show(); form.Activate(); }
        }

        private static void OpenLog()
        {
            string log = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "LightFixer", "lightfixer.log");
            try { if (File.Exists(log)) Process.Start("notepad.exe", "\"" + log + "\""); } catch { }
        }

        private static void UpdateUi()
        {
            if (tray != null)
            {
                // the hover hint carries the name only: the state is already visible
                // in the window, and a changing hint reads as a notification
                tray.Text = "LightFixer BRI-721";
            }
            // the form is refreshed only on show and after saving: a 2-second refresh
            // used to undo choices the user had made but not saved yet
        }

        private static void ShowWindow()
        {
            if (!form.Visible) form.Show();
            form.WindowState = FormWindowState.Normal;
            form.Activate();
            // unsaved edits stay on screen; rebuilding the fields would silently drop them
            if (!dirty) RefreshForm();
        }

        private static void ExitApp()
        {
            // closing the program hands the light back to the laptop: the firmware
            // timeout we suppressed while running is put back, so the keys fade out
            // after the same pause as before the program was installed
            try { Engine.HandBackToLaptop(); }
            catch { }
            if (tray != null) { tray.Visible = false; tray.Dispose(); }
            Application.Exit();
        }
        // ---------- form ----------
        // ---------- form ----------
        // palette: taken from our own Shtampio project (ui/css/tokens.css)
        private static readonly Color CAccent = Color.FromArgb(0x47, 0x52, 0xD6);
        private static readonly Color CAccentHover = Color.FromArgb(0x3B, 0x45, 0xC2);
        private static readonly Color CAccentPress = Color.FromArgb(0x31, 0x3A, 0xA8);
        private static readonly Color CBack = Color.FromArgb(0xF3, 0xF3, 0xF6);
        private static readonly Color CText = Color.FromArgb(0x1B, 0x1C, 0x22);
        private static readonly Color CSecond = Color.FromArgb(0x55, 0x58, 0x66);
        private static readonly Color CHint = Color.FromArgb(0x6B, 0x6E, 0x7B);
        private static readonly Color CLink = Color.FromArgb(0x3E, 0x49, 0xC9);
        private static readonly Color CFooter = Color.FromArgb(0x6B, 0x6E, 0x7B);
        private static readonly Color CDisabledText = Color.FromArgb(0xA3, 0xA5, 0xAF);
        private static readonly Color CLine = Color.FromArgb(0xE2, 0xE3, 0xE9);

        private static void BuildForm()
        {
            // the window is rebuilt on every language change: the old tips would leak
            if (tipStartup != null) { tipStartup.Dispose(); tipStartup = null; }
            form = new Form();
            form.Text = "LightFixer BRI-721  v1.0";
            form.AutoScaleMode = AutoScaleMode.None;
            form.ClientSize = new Size(460, 828);
            form.StartPosition = FormStartPosition.CenterScreen;
            form.FormBorderStyle = FormBorderStyle.FixedSingle;
            form.MaximizeBox = false;
            form.BackColor = CBack;
            form.Font = new Font("Segoe UI", 9f);
            form.Icon = MakeIcon();

            // ---- header: icon, name, version, About
            PictureBox picLogo = new PictureBox();
            picLogo.SizeMode = PictureBoxSizeMode.Zoom;
            picLogo.SetBounds(20, 18, 32, 32);
            try { picLogo.Image = new Bitmap(MakeIcon().ToBitmap(), new Size(32, 32)); }
            catch { }
            form.Controls.Add(picLogo);

            Label title = new Label();
            title.Text = "LightFixer BRI-721   v1.0";
            title.Font = new Font("Segoe UI", 14f, FontStyle.Bold);
            title.ForeColor = CText;
            title.TextAlign = ContentAlignment.MiddleLeft;
            title.SetBounds(64, 14, 280, 26);
            form.Controls.Add(title);

            Label sub = new Label();
            sub.Text = L("Управление подсветкой клавиатуры HONOR BRI-721",
                         "Keyboard backlight control for HONOR BRI-721");
            sub.ForeColor = CHint;
            sub.SetBounds(64, 40, 376, 18);
            form.Controls.Add(sub);

            LinkLabel lnkAbout = new LinkLabel();
            lnkAbout.Text = L("О программе", "About");
            lnkAbout.TextAlign = ContentAlignment.MiddleRight;
            lnkAbout.LinkColor = CLink;
            lnkAbout.SetBounds(352, 792, 88, 22);
            lnkAbout.LinkClicked += delegate { ShowAbout(); };
            form.Controls.Add(lnkAbout);

            // ---- who controls the light: one decision, one switch
            Label lblModeTitle = new Label();
            lblModeTitle.Text = L("Кто управляет подсветкой", "Who controls the backlight");
            lblModeTitle.Font = new Font("Segoe UI", 9.5f, FontStyle.Bold);
            lblModeTitle.ForeColor = CText;
            lblModeTitle.TextAlign = ContentAlignment.MiddleLeft;
            lblModeTitle.SetBounds(20, 62, 420, 20);
            form.Controls.Add(lblModeTitle);

            segMode = new Segmented();
            segMode.Items[0] = L("Программа LightFixer", "LightFixer (program)");
            segMode.Items[1] = L("Ноутбук HONOR", "HONOR laptop");
            segMode.SetBounds(20, 86, 420, 48);
            segMode.SelectedIndex = 0;
            form.Controls.Add(segMode);

            lblModeHint = new Label();
            lblModeHint.Font = new Font("Segoe UI", 8f);
            lblModeHint.ForeColor = CHint;
            lblModeHint.SetBounds(20, 140, 420, 18);
            form.Controls.Add(lblModeHint);

            // ---- card 1: when the backlight is on
            cardBehaviour = new CardPanel();
            cardBehaviour.SetBounds(20, 168, 420, 216);
            form.Controls.Add(cardBehaviour);

            lblCard1 = new Label();
            lblCard1.Text = L("Когда включается подсветка", "When the backlight comes on");
            lblCard1.Font = new Font("Segoe UI", 10f, FontStyle.Bold);
            lblCard1.ForeColor = CText;
            lblCard1.SetBounds(16, 14, 388, 20);
            cardBehaviour.Controls.Add(lblCard1);

            cbMouse = new CheckBox();
            cbMouse.Text = L("Включается, когда двигаете мышь", "Turns on when you move the mouse");
            cbMouse.SetBounds(16, 44, 388, 22);
            cardBehaviour.Controls.Add(cbMouse);

            Label hintMouse = new Label();
            hintMouse.Text = L("Без галочки подсветка включается только от нажатия клавиш", "Without this, the backlight turns on only from a key press");
            hintMouse.Font = new Font("Segoe UI", 8.25f);
            hintMouse.ForeColor = CHint;
            hintMouse.SetBounds(36, 66, 368, 16);
            cardBehaviour.Controls.Add(hintMouse);

            Panel sep1 = new Panel();
            sep1.BackColor = CLine;
            sep1.SetBounds(16, 94, 388, 1);
            cardBehaviour.Controls.Add(sep1);

            Label lblIdle = new Label();
            lblIdle.Text = L("Гаснет после простоя:", "Turns off after idle:");
            lblIdle.ForeColor = CText;
            lblIdle.TextAlign = ContentAlignment.MiddleLeft;
            lblIdle.SetBounds(16, 108, 150, 26);
            cardBehaviour.Controls.Add(lblIdle);

            cbTime = new DropDown();
            cbTime.SetBounds(170, 107, 234, 28);
            foreach (string s in IdleLabelsNow()) cbTime.Items.Add(s);
            cardBehaviour.Controls.Add(cbTime);

            // ---- your own value: hours, minutes, seconds
            lblOwnTime = new Label();
            lblOwnTime.Text = L("Своё время:", "Custom time:");
            lblOwnTime.ForeColor = CText;
            lblOwnTime.TextAlign = ContentAlignment.MiddleLeft;
            lblOwnTime.SetBounds(16, 168, 82, 28);
            cardBehaviour.Controls.Add(lblOwnTime);

            nHours = MakeSpin(0, 23);
            nHours.SetBounds(100, 169, 66, 28);
            cardBehaviour.Controls.Add(nHours);

            lblOwnH = MakeUnit(L("час.", "hr"));
            lblOwnH.SetBounds(168, 168, 32, 28);
            cardBehaviour.Controls.Add(lblOwnH);

            nMinutes = MakeSpin(0, 59);
            nMinutes.SetBounds(202, 169, 66, 28);
            cardBehaviour.Controls.Add(nMinutes);

            lblOwnM = MakeUnit(L("мин.", "min"));
            lblOwnM.SetBounds(270, 168, 32, 28);
            cardBehaviour.Controls.Add(lblOwnM);

            nSeconds = MakeSpin(0, 59);
            nSeconds.SetBounds(304, 169, 66, 28);
            cardBehaviour.Controls.Add(nSeconds);

            lblOwnS = MakeUnit(L("сек.", "sec"));
            lblOwnS.SetBounds(372, 168, 32, 28);
            cardBehaviour.Controls.Add(lblOwnS);

            lblIdleHint = new Label();
            lblIdleHint.Text = L("если ничего не нажимать и не двигать мышью", "if nothing is pressed and the mouse does not move");
            lblIdleHint.Font = new Font("Segoe UI", 8.25f);
            lblIdleHint.ForeColor = CHint;
            lblIdleHint.SetBounds(36, 140, 368, 16);
            cardBehaviour.Controls.Add(lblIdleHint);

            // ---- card 2: brightness at Windows startup
            cardStartup = new CardPanel();
            cardStartup.SetBounds(20, 396, 420, 216);
            form.Controls.Add(cardStartup);

            lblCard2 = new Label();
            lblCard2.Text = L("Подсветка клавиатуры при включении ноутбука",
                              "Keyboard backlight when the laptop starts");
            lblCard2.Font = new Font("Segoe UI", 10f, FontStyle.Bold);
            lblCard2.ForeColor = CText;
            lblCard2.SetBounds(16, 14, 392, 20);
            cardStartup.Controls.Add(lblCard2);

            tipStartup = new ToolTip();
            tipStartup.InitialDelay = 350;
            tipStartup.ReshowDelay = 120;
            tipStartup.AutoPopDelay = 20000;

            // one choice per line: a single row of four circles read as "four equal
            // options" and nobody understood where the brightness level belonged
            rbLast = new RadioButton();
            rbLast.Text = L("Как было до выключения ноутбука", "As it was before the laptop was turned off");
            rbLast.SetBounds(16, 36, 388, 20);
            cardStartup.Controls.Add(rbLast);
            tipStartup.SetToolTip(rbLast,
                L("Программа запоминает яркость подсветки перед выключением ноутбука\r\nи включает такую же при следующем запуске, как будто подсветку никто не трогал.",
                  "The program remembers the backlight level before shutdown\r\nand restores the same level at the next start, as if it had never changed."));

            // no tinted plate here: a coloured block looked like "this option is chosen"
            // even when another one was. The level is marked by its indent and the word "Brightness:"
            // the small lines under each choice were dropped to make room for the two
            // start ticks below; every choice keeps its full explanation in the tooltip

            rbOn = new RadioButton();
            rbOn.Text = L("Всегда включена", "Always on");
            rbOn.SetBounds(16, 60, 388, 20);
            cardStartup.Controls.Add(rbOn);
            tipStartup.SetToolTip(rbOn,
                L("Подсветка включается сама при каждом запуске ноутбука —\r\nнезависимо от того, была она включена или выключена.",
                  "The backlight comes on by itself at every start —\r\nwhether it was on or off before."));

            Label lblLevel = new Label();
            lblLevel.Text = L("Яркость:", "Brightness:");
            lblLevel.Font = new Font("Segoe UI", 9f);
            lblLevel.ForeColor = CHint;
            lblLevel.TextAlign = ContentAlignment.MiddleLeft;
            lblLevel.SetBounds(36, 88, 84, 24);
            cardStartup.Controls.Add(lblLevel);

            // the level is a switch of its own, not a third radio option: as a radio
            // button it fought with "always on" for the same selection and locked up
            segLevel = new Segmented();
            segLevel.Font = new Font("Segoe UI", 9f);
            segLevel.Items[0] = L("Низкая", "Low");
            segLevel.Items[1] = L("Высокая", "High");
            segLevel.SetBounds(126, 88, 150, 24);
            cardStartup.Controls.Add(segLevel);
            tipStartup.SetToolTip(segLevel,
                L("Насколько ярко горит подсветка: низкая — мягкий свет, высокая — максимальная.",
                  "How bright the backlight is: low for soft light, high for the maximum."));

            rbOff = new RadioButton();
            rbOff.Text = L("Всегда выключена", "Always off");
            rbOff.SetBounds(16, 116, 388, 20);
            cardStartup.Controls.Add(rbOff);
            tipStartup.SetToolTip(rbOff,
                L("При запуске ноутбука подсветка остаётся выключенной.\r\nПрограмма будет гасить её, даже если включить клавишами.",
                  "The backlight stays off at startup.\r\nThe program keeps it off even if you turn it on with the keys."));

            // ---- the program itself: two things Windows must do on its own
            Panel sepStart = new Panel();
            sepStart.BackColor = CLine;
            sepStart.SetBounds(16, 142, 388, 1);
            cardStartup.Controls.Add(sepStart);

            cbAutostart = new CheckBox();
            cbAutostart.Text = L("Запускать программу вместе с Windows", "Start the program with Windows");
            cbAutostart.SetBounds(16, 146, 388, 20);
            cardStartup.Controls.Add(cbAutostart);
            tipStartup.SetToolTip(cbAutostart,
                L("Значок программы в трее появится сразу после входа в Windows,\r\nи подсветка будет работать по правилам этой программы.\r\nБез этой галочки программа не запускается сама — и подсветку\r\nпридётся настраивать клавишами ноутбука.",
                  "The tray icon appears right after you sign in to Windows\r\nand the light follows this program's rules.\r\nWithout the tick the program does not start by itself, so the\r\nlight has to be set with the laptop keys."));

            cbBoot = new CheckBox();
            cbBoot.Text = L("Включать подсветку до входа в Windows (при вводе пароля)",
                            "Light up before sign-in (at the password screen)");
            cbBoot.SetBounds(16, 168, 388, 20);
            cardStartup.Controls.Add(cbBoot);
            tipStartup.SetToolTip(cbBoot,
                L("Клавиатура загорается уже при вводе пароля Windows, сразу после включения ноутбука.\r\nРаботает и без входа в систему: отдельная задача Windows, окно не появляется.",
                  "The keyboard lights up while the Windows password is typed, right after power-on.\r\nIt works even before sign-in: a separate Windows task, no window appears."));

            // ---- save state, always in the same slot so nothing jumps
            picStatus = new PictureBox();
            picStatus.SetBounds(20, 614, 16, 16);
            form.Controls.Add(picStatus);

            lblStatus = new Label();
            lblStatus.ForeColor = CHint;
            lblStatus.TextAlign = ContentAlignment.MiddleLeft;
            lblStatus.SetBounds(44, 612, 396, 20);
            form.Controls.Add(lblStatus);

            // ---- support: a visible button, but nothing is sold and nothing is unlocked
            cardSupport = new CardPanel();
            cardSupport.SetBounds(20, 648, 420, 80);
            cardSupport.Dim = true;
            form.Controls.Add(cardSupport);

            FlatButton btnDonate = new FlatButton();
            btnDonate.Text = L("Поблагодарить автора  ♥", "Thank the author  ♥");
            btnDonate.BackColor = CBack;
            btnDonate.Fill = Color.FromArgb(0xEE, 0xF0, 0xFF);
            btnDonate.HoverFill = Color.FromArgb(0xE3, 0xE6, 0xFF);
            btnDonate.PressFill = Color.FromArgb(0xD8, 0xDC, 0xFF);
            btnDonate.Border = Color.FromArgb(0xB9, 0xBF, 0xF5);
            btnDonate.TextColor = CLink;
            btnDonate.SetBounds(110, 8, 200, 28);
            btnDonate.Click += delegate
            {
                try { Process.Start(new ProcessStartInfo(DonateUrl) { UseShellExecute = true }); }
                catch { }
            };
            cardSupport.Controls.Add(btnDonate);

            Label lblSupport = new Label();
            lblSupport.Text = L("Программа распространяется бесплатно.\r\nБлагодарность — по желанию, это не оплата.",
                                "The program is free of charge.\r\nA thank-you is optional and is not a payment.");
            lblSupport.Font = new Font("Segoe UI", 8.25f);
            lblSupport.ForeColor = CHint;
            lblSupport.TextAlign = ContentAlignment.MiddleCenter;
            lblSupport.SetBounds(16, 40, 388, 34);
            cardSupport.Controls.Add(lblSupport);

            // ---- buttons: the destructive one far from the main one
            // the least important action is a plain link, not a button: it must not
            // compete with Save, and it must not look like the first thing to press
            LinkLabel btnQuit = new LinkLabel();
            btnQuit.Text = L("Выйти", "Exit");
            btnQuit.Font = new Font("Segoe UI", 9f, FontStyle.Bold);
            btnQuit.LinkColor = CSecond;
            btnQuit.ActiveLinkColor = CAccent;
            btnQuit.VisitedLinkColor = CSecond;
            btnQuit.LinkBehavior = LinkBehavior.HoverUnderline;
            btnQuit.TextAlign = ContentAlignment.MiddleCenter;
            btnQuit.AutoSize = false;
            btnQuit.SetBounds(20, 736, 84, 36);
            form.Controls.Add(btnQuit);

            FlatButton btnTray = new FlatButton();
            btnTray.Text = L("Свернуть в значок в трее", "Minimize to tray");
            btnTray.BackColor = CBack;
            btnTray.SetBounds(108, 736, 184, 36);
            form.Controls.Add(btnTray);

            FlatButton btnSave = new FlatButton();
            btnSave.Text = L("Сохранить", "Save");
            btnSave.BackColor = CBack;
            btnSave.Fill = CAccent;
            btnSave.HoverFill = CAccentHover;
            btnSave.PressFill = CAccentPress;
            btnSave.Border = Color.FromArgb(0, 0, 0, 0);
            btnSave.TextColor = Color.White;
            btnSave.SetBounds(304, 736, 100, 36);
            form.Controls.Add(btnSave);

            // ---- footer
            Panel sepFooter = new Panel();
            sepFooter.BackColor = CLine;
            sepFooter.SetBounds(20, 780, 420, 1);
            form.Controls.Add(sepFooter);

            Label lblCopyright = new Label();
            lblCopyright.Text = "© 2026 sergliv  ·  GNU GPL v3.0+";
            lblCopyright.Font = new Font("Segoe UI", 8f);
            lblCopyright.ForeColor = CFooter;
            lblCopyright.TextAlign = ContentAlignment.MiddleLeft;
            lblCopyright.SetBounds(20, 792, 168, 22);
            form.Controls.Add(lblCopyright);

            LinkLabel lnk4pda = new LinkLabel();
            lnk4pda.Text = L("Автор на 4pda", "Author on 4pda");
            lnk4pda.Font = new Font("Segoe UI", 8f);
            lnk4pda.LinkColor = CFooter;
            lnk4pda.TextAlign = ContentAlignment.MiddleLeft;
            lnk4pda.SetBounds(196, 796, 140, 22);
            lnk4pda.LinkClicked += delegate
            {
                try { Process.Start(new ProcessStartInfo("https://4pda.to/forum/index.php?showuser=1727220") { UseShellExecute = true }); }
                catch { }
            };
            form.Controls.Add(lnk4pda);

            segLang = new Segmented();
            segLang.Small = true;
            segLang.ShowDot = false;
            segLang.Items[0] = "RU";
            segLang.Items[1] = "EN";
            segLang.SelectedIndex = En ? 1 : 0;
            segLang.SetBounds(352, 14, 88, 22);
            segLang.SelectedIndexChanged += delegate
            {
                string lang = (segLang.SelectedIndex == 1) ? "en" : "ru";
                form.BeginInvoke(new MethodInvoker(delegate { SetLang(lang); }));
            };
            form.Controls.Add(segLang);
            segLang.BringToFront();

            // every control that can change a setting marks the form as unsaved
            segMode.SelectedIndexChanged += delegate
            {
                // the switch has to work at once: the greyed cards would otherwise lie
                // until "Save" is pressed
                if (loading) return;
                S.Control = (segMode.SelectedIndex == 1) ? "native" : "ours";
                Engine.ResetFromSettings();
                UpdateEnabled();
                SetDirty();
            };
            // the three choices live in different containers, so Windows would let two
            // of them stay ticked at once: each one clears the others by hand
            rbOff.CheckedChanged += delegate
            {
                if (!loading && rbOff.Checked && rbOn.Checked) rbOn.Checked = false;
                SetDirty();
                UpdateStartupLevel();
            };
            rbOn.CheckedChanged += delegate
            {
                if (!loading && rbOn.Checked)
                {
                    if (rbLast.Checked) rbLast.Checked = false;
                    if (rbOff.Checked) rbOff.Checked = false;
                }
                UpdateStartupLevel();
                SetDirty();
            };
            segLevel.SelectedIndexChanged += delegate
            {
                // picking a level means the backlight is on
                if (!loading && !rbOn.Checked) rbOn.Checked = true;
                SetDirty();
            };
            rbLast.CheckedChanged += delegate
            {
                if (!loading && rbLast.Checked && rbOn.Checked) rbOn.Checked = false;
                SetDirty();
                UpdateStartupLevel();
            };
            cbMouse.CheckedChanged += delegate { SetDirty(); };
            cbAutostart.CheckedChanged += delegate { SetDirty(); };
            cbBoot.CheckedChanged += delegate { SetDirty(); };

            cbTime.SelectedIndexChanged += delegate
            {
                // a preset also fills the three boxes, so they always show the current value
                int v = IdleValues[cbTime.SelectedIndex];
                if (v >= 0)
                {
                    loading = true;
                    nHours.Value = v / 3600;
                    nMinutes.Value = (v % 3600) / 60;
                    nSeconds.Value = v % 60;
                    loading = false;
                }
                ShowOwnTime(OwnTimeSelected);
                IdleHintNow();
                SetDirty();
            };

            // typing a number of hours / minutes / seconds is a setting change as well
            // typing a number also switches the list to "your own value"
            EventHandler own = delegate
            {
                if (loading) { IdleHintNow(); return; }
                if (!OwnTimeSelected)
                {
                    loading = true;
                    cbTime.SelectedIndex = IdleValues.Length - 1;
                    loading = false;
                }
                ShowOwnTime(true);
                IdleHintNow();
                SetDirty();
            };
            nHours.ValueChanged += own;
            nMinutes.ValueChanged += own;
            nSeconds.ValueChanged += own;

            btnSave.Click += delegate { SaveSettings(); };

            btnTray.Click += delegate { if (TryLeave()) form.Hide(); };
            btnQuit.Click += delegate { if (TryLeave()) ExitApp(); };

            form.FormClosing += delegate(object s, FormClosingEventArgs e)
            {
                if (e.CloseReason == CloseReason.UserClosing)
                {
                    e.Cancel = true;
                    if (TryLeave()) form.Hide();
                }
            };

            form.Resize += delegate { if (form.WindowState == FormWindowState.Minimized) form.Hide(); };

            // Windows scaling (125 %, 150 %, 175 %...): the program is DPI-aware,
            // so the window is scaled here by hand; fonts grow with the DPI on their own
            float scale = 1f;
            try { scale = GetDpiForSystem() / 96f; } catch { }
            if (scale < 1f) scale = 1f;
            if (scale > 1.01f)
            {
                form.Scale(new SizeF(scale, scale));
                // centre the already grown window by hand: CenterScreen measures before scaling
                Rectangle wa = Screen.PrimaryScreen.WorkingArea;
                form.StartPosition = FormStartPosition.Manual;
                form.Location = new Point(
                    wa.Left + Math.Max(0, (wa.Width - form.Width) / 2),
                    wa.Top + Math.Max(0, (wa.Height - form.Height) / 2));
            }

            Engine.Log("ui: dpi=" + GetDpiForSystem() + " scale=" + scale +
                       " size=" + form.Width + "x" + form.Height +
                       " loc=" + form.Location.X + "," + form.Location.Y +
                       " screen=" + Screen.PrimaryScreen.WorkingArea.Width + "x" + Screen.PrimaryScreen.WorkingArea.Height);

            RefreshForm();
        }

        private static void RefreshForm()
        {
            loading = true;
            segMode.SelectedIndex = (S.Control == "native") ? 1 : 0;

            rbOff.Checked = (S.Startup == "off");
            rbOn.Checked = (S.Startup == "low" || S.Startup == "high");
            segLevel.SelectedIndex = (S.Startup == "high") ? 1 : 0;
            rbLast.Checked = (S.Startup == "last" || (S.Startup != "off" && S.Startup != "low" && S.Startup != "high"));
            UpdateStartupLevel();

            cbMouse.Checked = S.MouseWake;
            // the first tick mirrors the scheduler, not a variable of our own: Windows is
            // the one that really decides whether the program starts
            cbAutostart.Checked = TaskEnabled();
            cbBoot.Checked = S.BootLight;

            int idx = Array.IndexOf(IdleValues, S.IdleSeconds);
            if (idx < 0) idx = IdleValues.Length - 1;   // not in the list - show it as "your own value"
            cbTime.SelectedIndex = idx;
            // the boxes cannot hold more than 23:59:59, so a bigger value must not crash the form
            int safeSec = Math.Min(S.IdleSeconds, 86399);
            nHours.Value = safeSec / 3600;
            nMinutes.Value = (safeSec % 3600) / 60;
            nSeconds.Value = safeSec % 60;
            ShowOwnTime(OwnTimeSelected);
            IdleHintNow();

            UpdateEnabled();
            loading = false;
            dirty = false;
            SetStatus(0, L("Настройки сохранены. Клавиши Fn+Пробел работают как обычно.",
                              "Settings are saved. Fn+Space works as usual."));
            if (form != null) form.Text = "LightFixer BRI-721  v1.0";
        }

        // ---------- unsaved changes ----------
        private static void SetDirty()
        {
            if (loading) return;
            dirty = true;
            SetStatus(1, L("Изменения не сохранены — нажмите «Сохранить».", "Unsaved changes — press Save"));
            // a dot in the title is the first thing the eye finds
            if (form != null) form.Text = "LightFixer BRI-721  •";
        }

        // 0 - nothing, 1 - unsaved, 2 - saved, 3 - error
        private static void SetStatus(int kind, string text)
        {
            if (lblStatus == null || picStatus == null) return;
            Color c = CHint;
            if (kind == 1) c = Color.FromArgb(0x8F, 0x5B, 0x00);
            else if (kind == 2) c = Color.FromArgb(0x1C, 0x7A, 0x4B);
            else if (kind == 3) c = Color.FromArgb(0xBF, 0x2E, 0x45);
            Image old = picStatus.Image;
            picStatus.Image = (kind == 0) ? null : StatusImage(kind);
            if (old != null) old.Dispose();
            lblStatus.Text = text;
            lblStatus.ForeColor = c;
        }

        private static Image StatusImage(int kind)
        {
            Bitmap b = new Bitmap(16, 16);
            using (Graphics g = Graphics.FromImage(b))
            {
                g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
                if (kind == 1)
                {
                    using (Pen p = new Pen(Color.FromArgb(0x8F, 0x5B, 0x00), 2f)) g.DrawEllipse(p, 3f, 3f, 9f, 9f);
                    using (Brush br = new SolidBrush(CAccent)) g.FillEllipse(br, 5.5f, 5.5f, 4f, 4f);
                }
                else if (kind == 2)
                {
                    using (Pen p = new Pen(Color.FromArgb(0x1C, 0x7A, 0x4B), 2f))
                    {
                        g.DrawEllipse(p, 1.5f, 1.5f, 12f, 12f);
                        g.DrawLines(p, new PointF[] { new PointF(4.5f, 8f), new PointF(7f, 10.5f), new PointF(11.5f, 5f) });
                    }
                }
                else
                {
                    using (Pen p = new Pen(Color.FromArgb(0xBF, 0x2E, 0x45), 2f))
                    {
                        g.DrawEllipse(p, 1.5f, 1.5f, 12f, 12f);
                        g.DrawLine(p, 8f, 4.5f, 8f, 9f);
                        g.DrawLine(p, 8f, 11f, 8f, 11.5f);
                    }
                }
            }
            return b;
        }

        // saves exactly what is on screen; returns false if the file could not be written
        private static bool SaveSettings()
        {
            try
            {
                S.Control = (segMode.SelectedIndex == 1) ? "native" : "ours";
                if (OwnTimeSelected)
                {
                    int own = OwnSeconds;
                    if (own > 86399) own = 86399;
                    S.IdleSeconds = own;                  // 0 = never off, the same as the first item
                    if (own == 0)
                    {
                        // all three boxes are zero: this means "never", show it as such
                        loading = true;
                        cbTime.SelectedIndex = 0;
                        loading = false;
                    }
                    else
                    {
                        nHours.Value = own / 3600;
                        nMinutes.Value = (own % 3600) / 60;
                        nSeconds.Value = own % 60;
                    }
                }
                else S.IdleSeconds = IdleValues[cbTime.SelectedIndex];
                S.MouseWake = cbMouse.Checked;
                S.BootLight = cbBoot.Checked;
                if (rbOff.Checked) S.Startup = "off";
                else if (rbLast.Checked) S.Startup = "last";
                else if (rbOn.Checked) S.Startup = (segLevel.SelectedIndex == 1) ? "high" : "low";
                else S.Startup = "last";

                // the window also sets the light right now, not only on the next boot;
                // in the HONOR mode the light belongs to the laptop, so it is left alone
                if (segMode.SelectedIndex == 0)
                {
                    if (S.Startup == "off") S.Level = "off";
                    else if (S.Startup == "low") S.Level = "low";
                    else if (S.Startup == "high") S.Level = "high";
                    else S.Level = S.LastLevel;
                }

                // a failed write must never be shown as "saved"
                if (!S.Save())
                {
                    SetStatus(3, L("Не сохранено. Файл настроек недоступен.",
                                   "Not saved. The settings file is locked."));
                    return false;
                }

                dirty = false;
                if (form != null) form.Text = "LightFixer BRI-721  v1.0";
                ApplyStartUpTicks();
                string savedText = L("Сохранено. Уже действует.", "Saved. Now in effect.");
                SetStatus(2, savedText);

                // the confirmation is a one-off message, it clears itself
                System.Windows.Forms.Timer clear = new System.Windows.Forms.Timer();
                clear.Interval = 4000;
                clear.Tick += delegate
                {
                    clear.Stop(); clear.Dispose();
                    if (lblStatus != null && lblStatus.Text == savedText) SetStatus(0, "");
                };
                clear.Start();
                return true;
            }
            catch (Exception ex)
            {
                SetStatus(3, L("Не удалось сохранить: ", "Could not save: ") + ex.Message);
                return false;
            }
        }

        // asked before the window is hidden or the program is closed
        private static bool TryLeave()
        {
            if (!dirty) return true;
            DialogResult r = MessageBox.Show(form,
                L("Настройки изменены, но не сохранены.\n\nСохранить их перед закрытием?", "Settings were changed but not saved.\n\nSave them before closing?"),
                "LightFixer BRI-721", MessageBoxButtons.YesNoCancel, MessageBoxIcon.Warning);
            if (r == DialogResult.Cancel) return false;
            if (r == DialogResult.Yes) return SaveSettings();
            dirty = false;   // "no" means: drop the changes deliberately
            return true;
        }

        // when HONOR manages the light, everything of ours is greyed out and not clickable
        private static void UpdateEnabled()
        {
            bool ours = (segMode.SelectedIndex == 0);
            cardBehaviour.Dim = !ours;
            cardStartup.Dim = !ours;
            cardBehaviour.Enabled = ours;
            cardStartup.Enabled = ours;
            lblCard1.ForeColor = ours ? CText : CDisabledText;
            lblCard2.ForeColor = ours ? CText : CDisabledText;
            lblModeHint.Text = ours
                ? L("Включается от нажатия клавиш и движения мыши, гаснет по таймеру ниже.",
                    "Turns on from keys or mouse movement and turns off by the timer below.")
                : L("Подсветкой управляет ноутбук. Настройки ниже неактивны.", "HONOR controls the backlight. The options below are inactive.");
            cardBehaviour.Invalidate();
            cardStartup.Invalidate();
            UpdateStartupLevel();
        }

        // the level belongs to the "on" state: without it there is nothing to set
        private static void UpdateStartupLevel()
        {
            if (segLevel == null || rbOn == null) return;
            // the level stays visible but grey until "always on" is chosen: that is how
            // a person sees there is a setting here without reading any hint
            segLevel.Enabled = rbOn.Checked;
        }

        // the text lives in a rich box: a message box cannot show clickable links
        // a small label with its own font, remembered so the font can be set after scaling
        private static Label AboutText(Control host, System.Collections.Hashtable fset, Font f, Color c,
                                       int x, int y, int w, int h, string text, ContentAlignment align)
        {
            Label l = new Label();
            l.Text = text;
            l.ForeColor = c;
            l.TextAlign = align;
            l.AutoSize = false;
            l.SetBounds(x, y, w, h);
            host.Controls.Add(l);
            fset[l] = f;
            return l;
        }

        private static CardPanel AboutCard(Panel host, int y, int h, bool dim)
        {
            CardPanel c = new CardPanel();
            c.Dim = dim;
            c.SetBounds(24, y, 575, h);
            host.Controls.Add(c);
            return c;
        }

        private static void ShowAbout()
        {
            using (Form w = new Form())
            {
                // laid out in 96-dpi units and scaled once; the fonts are set AFTER
                // scaling, otherwise on a 175 % screen they grow twice and become huge
                float sc = GetDpiForSystem() / 96f;
                if (sc < 1f) sc = 1f;

                w.AutoScaleMode = AutoScaleMode.None;
                w.Text = L("О программе", "About");
                w.ClientSize = new Size(640, 752);
                w.StartPosition = FormStartPosition.Manual;
                w.FormBorderStyle = FormBorderStyle.FixedSingle;
                w.MaximizeBox = false;
                w.MinimizeBox = false;
                w.ShowInTaskbar = false;
                w.BackColor = CBack;
                w.Icon = MakeIcon();
                w.KeyPreview = true;
                w.KeyDown += delegate(object s, KeyEventArgs e)
                {
                    if (e.KeyCode == Keys.Escape) w.Close();
                };

                Font fTitle = new Font("Segoe UI", 13f, FontStyle.Bold);
                Font fHead = new Font("Segoe UI", 9.5f, FontStyle.Bold);
                Font fSub = new Font("Segoe UI", 9.5f);
                Font fBody = new Font("Segoe UI", 9f);
                Font fBodyB = new Font("Segoe UI", 9f, FontStyle.Bold);
                Font fSmall = new Font("Segoe UI", 8.5f);
                Font fSmallB = new Font("Segoe UI", 8.5f, FontStyle.Bold);
                Font fPill = fSmallB;
                System.Collections.Hashtable fset = new System.Collections.Hashtable();

                // ---- header: icon, name, subtitle, version
                Panel head = new Panel();
                head.BackColor = Color.White;
                head.SetBounds(0, 0, 640, 92);
                w.Controls.Add(head);

                PictureBox logo = new PictureBox();
                logo.SizeMode = PictureBoxSizeMode.StretchImage;
                logo.SetBounds(24, 22, 48, 48);
                try
                {
                    int isz = (int)Math.Round(48 * sc);
                    Bitmap bm = new Bitmap(isz, isz);
                    using (Graphics g = Graphics.FromImage(bm))
                    {
                        g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
                        g.DrawIcon(MakeIcon(), new Rectangle(0, 0, isz, isz));
                    }
                    logo.Image = bm;
                }
                catch { }
                head.Controls.Add(logo);

                AboutText(head, fset, fTitle, CText, 84, 20, 400, 30, "LightFixer BRI-721", ContentAlignment.MiddleLeft);
                AboutText(head, fset, fSub, CHint, 84, 50, 420, 18,
                          L("Управление подсветкой клавиатуры HONOR BRI-721",
                            "Keyboard backlight control for HONOR BRI-721"), ContentAlignment.MiddleLeft);

                Label pill = new Label();
                pill.Text = L("версия 1.0", "version 1.0");
                pill.ForeColor = CLink;
                pill.TextAlign = ContentAlignment.MiddleCenter;
                pill.AutoSize = false;
                pill.BackColor = Color.FromArgb(0xEE, 0xF0, 0xFF);
                pill.SetBounds(504, 22, 112, 24);
                head.Controls.Add(pill);
                fset[pill] = fPill;

                Panel headLine = new Panel();
                headLine.BackColor = CLine;
                headLine.SetBounds(0, 92, 640, 1);
                w.Controls.Add(headLine);

                // ---- body: four cards, everything visible without scrolling
                Panel body = new Panel();
                body.AutoScroll = true;
                body.BackColor = CBack;
                body.SetBounds(0, 93, 640, 578);
                w.Controls.Add(body);

                // card 1: what the program adds
                CardPanel c1 = AboutCard(body, 16, 119, false);
                AboutText(c1, fset, fHead, CText, 16, 14, 543, 17,
                          L("Что добавляет программа", "What the program adds"), ContentAlignment.MiddleLeft);

                string[] adds = new string[] {
                    L("Подсветка включается сама после запуска ноутбука — с выбранной яркостью.",
                      "The backlight comes on right after startup at the brightness you chose."),
                    L("Время простоя — любое: часы, минуты и секунды, а не только готовые значения.",
                      "Any idle time: hours, minutes and seconds, not only preset values."),
                    L("Подсветка загорается и от движения мыши, а не только от нажатия клавиш.",
                      "Mouse movement lights it up, not only key presses.") };
                for (int i = 0; i < adds.Length; i++)
                {
                    int y = 41 + i * 24;
                    AboutText(c1, fset, fBodyB, CLink, 16, y, 12, 16, "•", ContentAlignment.MiddleLeft);
                    AboutText(c1, fset, fBody, CText, 32, y, 527, 16, adds[i], ContentAlignment.MiddleLeft);
                }

                // card 2: how to use it
                CardPanel c2 = AboutCard(body, 147, 137, false);
                AboutText(c2, fset, fHead, CText, 16, 14, 543, 17,
                          L("Как пользоваться", "How to use it"), ContentAlignment.MiddleLeft);

                string[] steps = new string[] {
                    L("Выберите, кто управляет подсветкой: программа или ноутбук.",
                      "Choose who controls the backlight: the program or the laptop."),
                    L("Отметьте, зажигать ли её от мыши, и время простоя.",
                      "Tick whether the mouse should light it up, and set the idle time."),
                    L("Укажите яркость при включении и перезагрузке ноутбука.",
                      "Choose the brightness for power-on and restart."),
                    L("Нажмите «Сохранить» — настройка начнёт действовать сразу.",
                      "Press Save — the setting starts working at once.") };
                for (int i = 0; i < steps.Length; i++)
                {
                    int y = 41 + i * 22;
                    AboutText(c2, fset, fBodyB, CLink, 16, y, 16, 16, (i + 1) + ".", ContentAlignment.MiddleLeft);
                    AboutText(c2, fset, fBody, CText, 34, y, 525, 16, steps[i], ContentAlignment.MiddleLeft);
                }

                // card 3: keys and tray icon
                CardPanel c3 = AboutCard(body, 296, 93, false);
                AboutText(c3, fset, fHead, CText, 16, 14, 543, 17,
                          L("Клавиши и значок в трее", "Keys and the tray icon"), ContentAlignment.MiddleLeft);
                AboutText(c3, fset, fBody, CText, 16, 41, 543, 16,
                          L("Fn+Пробел работают всегда — как и без программы.",
                            "Fn+Space keeps working, exactly as without the program."), ContentAlignment.MiddleLeft);
                AboutText(c3, fset, fBody, CText, 16, 63, 543, 16,
                          L("«Свернуть в значок» убирает окно; вернуть — двойной щелчок по синему значку у часов.",
                            "Minimize to tray hides the window; double-click the blue tray icon to bring it back."), ContentAlignment.MiddleLeft);

                // card 4: licence and rights
                CardPanel c4 = AboutCard(body, 402, 158, true);
                AboutText(c4, fset, fHead, CText, 16, 14, 543, 17,
                          L("Лицензия и права", "Licence and rights"), ContentAlignment.MiddleLeft);
                AboutText(c4, fset, fSmall, CHint, 16, 41, 543, 17,
                          L("Copyright © 2026 sergliv. Лицензия GNU GPL v3.0 или более поздняя.",
                            "Copyright © 2026 sergliv. GNU GPL v3.0 or later."), ContentAlignment.MiddleLeft);
                AboutText(c4, fset, fSmall, CHint, 16, 60, 543, 17,
                          L("Исходный код — в архиве src.zip рядом с программой.",
                            "The source code is in src.zip next to the program."), ContentAlignment.MiddleLeft);
                AboutText(c4, fset, fSmall, CHint, 16, 79, 543, 17,
                          L("Программа предоставляется «как есть», без гарантий.",
                            "The program is provided \"as is\", without warranty."), ContentAlignment.MiddleLeft);
                AboutText(c4, fset, fSmall, CHint, 16, 98, 543, 17,
                          L("Не связана с HONOR и не одобрена компанией HONOR.",
                            "Not affiliated with or approved by HONOR."), ContentAlignment.MiddleLeft);
                AboutText(c4, fset, fSmall, CHint, 16, 117, 543, 17,
                          L("HONOR и BRI-721 — обозначения их правообладателей.",
                            "HONOR and BRI-721 are designations of their owners."), ContentAlignment.MiddleLeft);

                LinkLabel lnkLic = new LinkLabel();
                lnkLic.Text = L("Текст лицензии: gnu.org/licenses/gpl-3.0.ru.html",
                                "Licence text: gnu.org/licenses/gpl-3.0.html");
                lnkLic.LinkBehavior = LinkBehavior.HoverUnderline;
                lnkLic.LinkColor = CLink;
                lnkLic.ActiveLinkColor = Color.FromArgb(0x1F, 0x49, 0xB8);
                lnkLic.VisitedLinkColor = CLink;
                lnkLic.UseMnemonic = false;
                lnkLic.AutoSize = false;
                lnkLic.SetBounds(16, 138, 543, 17);
                string licUrl = En ? "https://www.gnu.org/licenses/gpl-3.0.html" : "https://www.gnu.org/licenses/gpl-3.0.ru.html";
                lnkLic.LinkClicked += delegate
                {
                    try { Process.Start(new ProcessStartInfo(licUrl) { UseShellExecute = true }); }
                    catch { }
                };
                c4.Controls.Add(lnkLic);
                fset[lnkLic] = fSmall;

                // ---- footer: copyright, author, close
                Panel foot = new Panel();
                foot.BackColor = Color.White;
                foot.SetBounds(0, 672, 640, 80);
                w.Controls.Add(foot);

                Panel footLine = new Panel();
                footLine.BackColor = CLine;
                footLine.SetBounds(0, 671, 640, 1);
                w.Controls.Add(footLine);

                AboutText(foot, fset, fSmall, CHint, 24, 20, 320, 15,
                          L("© 2026 sergliv  ·  GNU GPL v3.0 или более поздняя",
                            "© 2026 sergliv  ·  GNU GPL v3.0 or later"), ContentAlignment.MiddleLeft);

                LinkLabel lnkAuthor = new LinkLabel();
                lnkAuthor.Text = L("Автор на 4pda.to", "Author on 4pda.to");
                lnkAuthor.LinkBehavior = LinkBehavior.HoverUnderline;
                lnkAuthor.LinkColor = CLink;
                lnkAuthor.ActiveLinkColor = Color.FromArgb(0x1F, 0x49, 0xB8);
                lnkAuthor.VisitedLinkColor = CLink;
                lnkAuthor.AutoSize = false;
                lnkAuthor.SetBounds(24, 42, 200, 15);
                lnkAuthor.LinkClicked += delegate
                {
                    try { Process.Start(new ProcessStartInfo("https://4pda.to/forum/index.php?showuser=1727220") { UseShellExecute = true }); }
                    catch { }
                };
                foot.Controls.Add(lnkAuthor);
                fset[lnkAuthor] = fSmall;

                FlatButton btnClose = new FlatButton();
                btnClose.Text = L("Закрыть", "Close");
                btnClose.Fill = CAccent;
                btnClose.HoverFill = CAccentHover;
                btnClose.PressFill = CAccentPress;
                btnClose.Border = Color.FromArgb(0, 0, 0, 0);
                btnClose.TextColor = Color.White;
                btnClose.SetBounds(508, 23, 108, 34);
                btnClose.Click += delegate { w.Close(); };
                foot.Controls.Add(btnClose);
                fset[btnClose] = fBodyB;

                // one scaling pass for the whole window, then the fonts in points
                if (sc > 1f) w.Scale(new SizeF(sc, sc));
                foreach (System.Collections.DictionaryEntry e in fset)
                    ((Control)e.Key).Font = (Font)e.Value;

                w.AcceptButton = btnClose;
                w.ActiveControl = btnClose;

                Rectangle wa = Screen.PrimaryScreen.WorkingArea;
                w.Location = new Point(
                    wa.Left + Math.Max(0, (wa.Width - w.Width) / 2),
                    wa.Top + Math.Max(0, (wa.Height - w.Height) / 2));

                w.ShowDialog(form != null && form.Visible ? form : null);
            }
        }
    }

    // -------------------------------------------------------- drawn controls
    // three small controls, drawn by hand: the standard ones cannot be rounded

    internal sealed class CardPanel : Panel
    {
        public bool Dim;    // inactive look: a very light grey card

        public CardPanel()
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint |
                     ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            BackColor = Color.Transparent;
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            e.Graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            Rectangle r = new Rectangle(0, 0, Width - 1, Height - 1);
            using (GraphicsPath p = Round(r, 10))
            using (SolidBrush b = new SolidBrush(Dim ? Color.FromArgb(0xF8, 0xF8, 0xFA) : Color.White))
            using (Pen pen = new Pen(Dim ? Color.FromArgb(0xED, 0xEE, 0xF3) : Color.FromArgb(0xE2, 0xE3, 0xE9), 1f))
            {
                e.Graphics.FillPath(b, p);
                e.Graphics.DrawPath(pen, p);
            }
        }

        protected override void OnResize(EventArgs e)
        {
            base.OnResize(e);
            if (Width > 4 && Height > 4)
            {
                using (GraphicsPath p = Round(new Rectangle(0, 0, Width, Height), 10))
                    Region = new Region(p);
            }
        }

        public static GraphicsPath Round(Rectangle r, int rad)
        {
            int d = rad * 2;
            GraphicsPath p = new GraphicsPath();
            p.AddArc(r.X, r.Y, d, d, 180, 90);
            p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
            p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
            p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
            p.CloseFigure();
            return p;
        }
    }

    internal sealed class FlatButton : Button
    {
        public Color Fill = Color.White;
        public Color HoverFill = Color.FromArgb(0xF8, 0xF8, 0xFA);
        public Color PressFill = Color.FromArgb(0xEF, 0xEF, 0xF3);
        public Color Border = Color.FromArgb(0xC9, 0xCB, 0xD4);
        public Color TextColor = Color.FromArgb(0x1B, 0x1C, 0x22);

        private bool hover, down;

        public FlatButton()
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint |
                     ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            FlatStyle = FlatStyle.Flat;
            FlatAppearance.BorderSize = 0;
            UseVisualStyleBackColor = false;   // otherwise Button ignores BackColor
            BackColor = Color.White;
            Font = new Font("Segoe UI", 9f, FontStyle.Bold);
            Cursor = Cursors.Hand;
        }

        protected override void OnMouseEnter(EventArgs e) { hover = true; Invalidate(); base.OnMouseEnter(e); }
        protected override void OnMouseLeave(EventArgs e) { hover = false; down = false; Invalidate(); base.OnMouseLeave(e); }
        protected override void OnMouseDown(MouseEventArgs e) { down = true; Invalidate(); base.OnMouseDown(e); }
        protected override void OnMouseUp(MouseEventArgs e) { down = false; Invalidate(); base.OnMouseUp(e); }

        // rounded region: the base Button class would otherwise leave square corner marks
        protected override void OnResize(EventArgs e)
        {
            base.OnResize(e);
            if (Width > 4 && Height > 4)
            {
                using (GraphicsPath p = CardPanel.Round(new Rectangle(0, 0, Width, Height), 8))
                    Region = new Region(p);
            }
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            Rectangle r = new Rectangle(0, 0, Width - 1, Height - 1);
            using (SolidBrush bg = new SolidBrush(Fill)) g.FillRectangle(bg, 0, 0, Width, Height);
            using (GraphicsPath p = CardPanel.Round(r, 8))
            {
                Color fill = down ? (PressFill.A > 0 ? PressFill : Darken(Fill, 0.88)) : (hover ? HoverFill : Fill);
                using (SolidBrush b = new SolidBrush(fill)) g.FillPath(b, p);
                if (Border.A > 0)
                    using (Pen pen = new Pen(Border, 1f)) g.DrawPath(pen, p);
            }
            Color tc = down ? Darken(TextColor, 0.85) : TextColor;
            TextRenderer.DrawText(g, Text, Font, new Rectangle(0, 0, Width, Height), tc,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);

            // Tab must be visible: UserPaint hides the system focus frame
            if (Focused && ShowFocusCues && Width > 8 && Height > 8)
            {
                using (GraphicsPath fp = CardPanel.Round(new Rectangle(2, 2, Width - 5, Height - 5), 7))
                using (Pen pen = new Pen(Fill.GetBrightness() > 0.6f
                                             ? Color.FromArgb(0x47, 0x52, 0xD6) : Color.White, 2f))
                    g.DrawPath(pen, fp);
            }
        }

        public static Color Darken(Color c, double k)
        {
            return Color.FromArgb(c.A,
                (int)(c.R * k), (int)(c.G * k), (int)(c.B * k));
        }
    }

    // two halves in one track: reads as "one of two", not as "a list item"
    internal sealed class Segmented : Control
    {
        public string[] Items = new string[] { "", "" };
        public bool Small;      // used for the language switch in the footer
        public bool ShowDot = true;

        private int sel;
        private bool hover;

        public event EventHandler SelectedIndexChanged;

        public int SelectedIndex
        {
            get { return sel; }
            set
            {
                int v = (value == 1) ? 1 : 0;
                if (v == sel) { Invalidate(); return; }
                sel = v;
                Invalidate();
                if (SelectedIndexChanged != null) SelectedIndexChanged(this, EventArgs.Empty);
            }
        }

        public Segmented()
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint |
                     ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw |
                     ControlStyles.Selectable, true);
            TabStop = true;
            Cursor = Cursors.Hand;
            Font = new Font("Segoe UI", 8.5f);
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            // a disabled switch must not react to clicks at all
            if (!Enabled) { base.OnMouseDown(e); return; }
            Focus();
            SelectedIndex = (e.X < Width / 2) ? 0 : 1;
            base.OnMouseDown(e);
        }

        protected override void OnEnabledChanged(EventArgs e)
        {
            // the hand cursor would promise a click that does nothing
            Cursor = Enabled ? Cursors.Hand : Cursors.Default;
            Invalidate();
            base.OnEnabledChanged(e);
        }

        protected override void OnMouseEnter(EventArgs e) { hover = true; Invalidate(); base.OnMouseEnter(e); }
        protected override void OnMouseLeave(EventArgs e) { hover = false; Invalidate(); base.OnMouseLeave(e); }
        protected override void OnGotFocus(EventArgs e) { Invalidate(); base.OnGotFocus(e); }
        protected override void OnLostFocus(EventArgs e) { Invalidate(); base.OnLostFocus(e); }
        protected override bool IsInputKey(Keys keyData) { return true; }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            if (e.KeyCode == Keys.Left) { SelectedIndex = 0; e.Handled = true; }
            else if (e.KeyCode == Keys.Right) { SelectedIndex = 1; e.Handled = true; }
            base.OnKeyDown(e);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            int rad = Small ? 6 : 10;
            int inset = Small ? 2 : 4;

            Rectangle full = new Rectangle(0, 0, Width - 1, Height - 1);
            using (GraphicsPath track = CardPanel.Round(full, rad))
            {
                Color trackFill = Small ? Color.FromArgb(0xEF, 0xEF, 0xF3) : Color.FromArgb(0xE4, 0xE5, 0xEB);
                if (!Enabled) trackFill = Color.FromArgb(0xF2, 0xF3, 0xF6);
                using (SolidBrush tb = new SolidBrush(trackFill)) g.FillPath(tb, track);
                if (!Small)
                    using (Pen tp = new Pen(Enabled ? Color.FromArgb(0xC9, 0xCB, 0xD4)
                                                   : Color.FromArgb(0xE2, 0xE4, 0xEA), 1f)) g.DrawPath(tp, track);
            }

            int half = (Width - inset * 2) / 2;
            Rectangle act = new Rectangle(inset + sel * half, inset, half, Height - inset * 2);

            using (GraphicsPath ap = CardPanel.Round(act, Small ? 4 : 8))
            {
                if (Small)
                {
                    using (SolidBrush ab = new SolidBrush(Color.White)) g.FillPath(ab, ap);
                    using (Pen pen = new Pen(Color.FromArgb(0xE2, 0xE3, 0xE9), 1f)) g.DrawPath(pen, ap);
                }
                else if (!Enabled)
                {
                    // still shows which half is chosen, but plainly greyed out
                    using (SolidBrush ab = new SolidBrush(Color.FromArgb(0xDF, 0xE1, 0xE8))) g.FillPath(ab, ap);
                    using (Pen pen = new Pen(Color.FromArgb(0xD0, 0xD2, 0xDB), 1f)) g.DrawPath(pen, ap);
                }
                else
                {
                    // the chosen half is a solid accent pill: the choice must be visible at a glance
                    using (System.Drawing.Drawing2D.LinearGradientBrush ab =
                        new System.Drawing.Drawing2D.LinearGradientBrush(act, Color.FromArgb(0x58, 0x63, 0xE4), Color.FromArgb(0x3B, 0x45, 0xC2), 90f))
                        g.FillPath(ab, ap);
                    using (Pen pen = new Pen(Color.FromArgb(0x33, 0x3C, 0xA6), 1f)) g.DrawPath(pen, ap);
                }
            }

            for (int i = 0; i < 2; i++)
            {
                Rectangle cell = new Rectangle(inset + i * half, inset, half, Height - inset * 2);
                bool active = (i == sel);
                Color c;
                if (active) c = Small ? Color.FromArgb(0x1B, 0x1C, 0x22) : Color.White;
                else c = (hover && !Small) ? Color.FromArgb(0x33, 0x36, 0x42) : Color.FromArgb(0x55, 0x58, 0x66);
                // grey text says "not available right now" without hiding the label
                if (!Enabled) c = active ? Color.FromArgb(0xA8, 0xAC, 0xB8) : Color.FromArgb(0xB8, 0xBC, 0xC6);
                Font f = new Font(Font.FontFamily, Font.Size, active ? FontStyle.Bold : FontStyle.Regular);
                TextRenderer.DrawText(g, Items[i], f, cell, c,
                    TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
                f.Dispose();
            }

            if (Focused)
            {
                using (GraphicsPath fp = CardPanel.Round(new Rectangle(act.X + 1, act.Y + 1, act.Width - 2, act.Height - 2), Small ? 4 : 8))
                using (Pen pen = new Pen(Small ? Color.FromArgb(0x47, 0x52, 0xD6) : Color.FromArgb(0x1B, 0x1C, 0x22), 1.5f))
                    g.DrawPath(pen, fp);
            }
        }
    }
    // --------------------------------------------------- time picker controls
    // The stock ComboBox and NumericUpDown keep their own grey system chrome:
    // square frame, gradient arrow, tiny spinner. Neither can be rounded, so
    // both are drawn here from scratch in the same flat style as the cards.

    internal sealed class DropDown : Control
    {
        public sealed class ItemList
        {
            private readonly System.Collections.ArrayList items = new System.Collections.ArrayList();
            public int Count { get { return items.Count; } }
            public string this[int index] { get { return (string)items[index]; } }
            public void Add(string text) { items.Add(text); }
            public void Clear() { items.Clear(); }
        }

        public readonly ItemList Items = new ItemList();
        public EventHandler SelectedIndexChanged;

        private int sel = -1;
        private bool hover;
        private Form popup;

        public DropDown()
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint |
                     ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            BackColor = Color.White;
            Font = new Font("Segoe UI", 9f);
            Cursor = Cursors.Hand;
            TabStop = true;
        }

        public int SelectedIndex
        {
            get { return sel; }
            set
            {
                int v = value;
                if (v < -1) v = -1;
                if (v > Items.Count - 1) v = Items.Count - 1;
                if (v == sel) return;
                sel = v;
                Invalidate();
                if (SelectedIndexChanged != null) SelectedIndexChanged(this, EventArgs.Empty);
            }
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;

            // the field: the same rounded white card the rest of the window uses
            using (GraphicsPath path = CardPanel.Round(new Rectangle(0, 0, Width - 1, Height - 1), 8))
            {
                using (SolidBrush b = new SolidBrush(Enabled ? Color.White : Color.FromArgb(0xFA, 0xFB, 0xFC)))
                    g.FillPath(b, path);
                Color edge;
                if (!Enabled) edge = Color.FromArgb(0xED, 0xEF, 0xF3);
                else if (hover) edge = Color.FromArgb(0xB9, 0xBE, 0xCC);
                else edge = Color.FromArgb(0xD5, 0xD9, 0xE0);
                using (Pen pen = new Pen(edge, 1f)) g.DrawPath(pen, path);
                if (Focused && Enabled)
                {
                    using (GraphicsPath inner = CardPanel.Round(new Rectangle(1, 1, Width - 3, Height - 3), 7))
                    using (Pen pen = new Pen(Color.FromArgb(0x47, 0x52, 0xD6), 1.5f))
                        g.DrawPath(pen, inner);
                }
            }

            // the chosen line, cut with an ellipsis if it is too long for the field
            string text = (sel >= 0 && sel < Items.Count) ? Items[sel] : "";
            TextRenderer.DrawText(g, text, Font, new Rectangle(12, 1, Width - 40, Height - 1),
                Enabled ? Color.FromArgb(0x1B, 0x1C, 0x22) : Color.FromArgb(0xA3, 0xA5, 0xAF),
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis |
                TextFormatFlags.NoPrefix);

            // the arrow: two round-capped strokes read better than a glyph
            int cx = Width - 18, cy = Height / 2 + 1;
            using (Pen pen = new Pen(Enabled ? Color.FromArgb(0x8A, 0x8E, 0x9C) : Color.FromArgb(0xC6, 0xC9, 0xD2), 1.6f))
            {
                pen.StartCap = LineCap.Round;
                pen.EndCap = LineCap.Round;
                g.DrawLine(pen, cx - 4, cy - 2, cx, cy + 2);
                g.DrawLine(pen, cx, cy + 2, cx + 4, cy - 2);
            }
        }

        protected override void OnMouseEnter(EventArgs e)
        {
            base.OnMouseEnter(e);
            hover = true;
            Invalidate();
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            base.OnMouseLeave(e);
            hover = false;
            Invalidate();
        }

        protected override void OnGotFocus(EventArgs e)
        {
            base.OnGotFocus(e);
            Invalidate();
        }

        protected override void OnLostFocus(EventArgs e)
        {
            base.OnLostFocus(e);
            Invalidate();
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            Focus();
            ShowList();
        }

        protected override void OnMouseWheel(MouseEventArgs e)
        {
            base.OnMouseWheel(e);
            if (!Enabled || Items.Count == 0) return;
            if (e.Delta > 0) SelectedIndex = sel - 1;
            else if (e.Delta < 0) SelectedIndex = sel + 1;
        }

        protected override bool IsInputKey(Keys keyData)
        {
            if (keyData == Keys.Up || keyData == Keys.Down) return true;
            return base.IsInputKey(keyData);
        }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            base.OnKeyDown(e);
            if (!Enabled) return;
            if (e.KeyCode == Keys.Down) { SelectedIndex = sel + 1; e.Handled = true; }
            else if (e.KeyCode == Keys.Up) { SelectedIndex = sel - 1; e.Handled = true; }
            else if (e.KeyCode == Keys.Enter || e.KeyCode == Keys.Space) { ShowList(); e.Handled = true; }
        }

        // the list is a borderless window of its own: a real popup keeps the
        // chosen row under the field and closes as soon as the user looks away
        private void ShowList()
        {
            if (!Enabled || Items.Count == 0 || popup != null) return;

            int rowH = 28;
            int rows = Math.Min(Items.Count, 9);

            Form f = new Form();
            f.FormBorderStyle = FormBorderStyle.None;
            f.StartPosition = FormStartPosition.Manual;
            f.ShowInTaskbar = false;
            f.BackColor = Color.White;
            f.Padding = new Padding(1);

            ListBox lb = new ListBox();
            lb.BorderStyle = BorderStyle.None;
            lb.DrawMode = DrawMode.OwnerDrawFixed;
            lb.ItemHeight = rowH;
            lb.IntegralHeight = false;
            lb.BackColor = Color.White;
            lb.ForeColor = Color.FromArgb(0x1B, 0x1C, 0x22);
            lb.Font = Font;
            for (int i = 0; i < Items.Count; i++) lb.Items.Add(Items[i]);
            lb.SelectedIndex = sel;
            lb.SetBounds(1, 1, Width - 2, rows * rowH);

            lb.DrawItem += delegate(object s, DrawItemEventArgs de)
            {
                if (de.Index < 0) return;
                bool chosen = (de.State & DrawItemState.Selected) == DrawItemState.Selected;
                Color back = chosen ? Color.FromArgb(0xF1, 0xF2, 0xF7) : Color.White;
                using (SolidBrush b = new SolidBrush(back))
                    de.Graphics.FillRectangle(b, de.Bounds);
                TextRenderer.DrawText(de.Graphics, (string)lb.Items[de.Index], Font,
                    new Rectangle(de.Bounds.X + 11, de.Bounds.Y, de.Bounds.Width - 16, de.Bounds.Height),
                    Color.FromArgb(0x1B, 0x1C, 0x22),
                    TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis |
                    TextFormatFlags.NoPrefix);
            };

            lb.MouseUp += delegate(object s, MouseEventArgs me)
            {
                int i = lb.IndexFromPoint(me.Location);
                if (i >= 0) { SelectedIndex = i; CloseList(); }
            };
            lb.KeyDown += delegate(object s, KeyEventArgs ke)
            {
                if (ke.KeyCode == Keys.Escape) CloseList();
                else if (ke.KeyCode == Keys.Enter)
                {
                    SelectedIndex = lb.SelectedIndex;
                    CloseList();
                }
            };

            f.Controls.Add(lb);

            // keep the list on screen: it opens downwards unless there is no room
            Rectangle wa = Screen.FromControl(this).WorkingArea;
            Point at = PointToScreen(new Point(0, Height));
            int listH = rows * rowH + 2;
            if (at.Y + listH > wa.Bottom) at.Y = PointToScreen(new Point(0, 0)).Y - listH;
            if (at.X + Width > wa.Right) at.X = wa.Right - Width;
            f.Location = at;
            f.ClientSize = new Size(Width, listH);

            lb.Width = Width - 2;
            lb.Height = rows * rowH;

            popup = f;
            f.FormClosed += delegate { popup = null; };
            f.Deactivate += delegate { CloseList(); };
            f.Show(this);
            lb.Focus();
        }

        private void CloseList()
        {
            if (popup == null) return;
            Form f = popup;
            popup = null;
            try { f.Close(); } catch { }
        }
    }

    // a number field with two small arrows of its own: step up on the top half
    // of the arrow strip, step down on the bottom half
    internal sealed class SpinBox : Control
    {
        public int Minimum = 0;
        public int Maximum = 59;
        public EventHandler ValueChanged;

        private int val;
        private bool hover, overUp, overDown;

        public SpinBox()
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint |
                     ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            BackColor = Color.White;
            Font = new Font("Segoe UI", 9.5f);
            TabStop = true;
        }

        public int Value
        {
            get { return val; }
            set
            {
                int v = value;
                if (v < Minimum) v = Minimum;
                if (v > Maximum) v = Maximum;
                if (v == val) return;
                val = v;
                Invalidate();
                if (ValueChanged != null) ValueChanged(this, EventArgs.Empty);
            }
        }

        private Rectangle UpZone
        {
            get { return new Rectangle(Width - 20, 2, 18, (Height - 4) / 2); }
        }

        private Rectangle DownZone
        {
            get { return new Rectangle(Width - 20, Height / 2, 18, Height - 2 - Height / 2); }
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;

            using (GraphicsPath path = CardPanel.Round(new Rectangle(0, 0, Width - 1, Height - 1), 8))
            {
                using (SolidBrush b = new SolidBrush(Enabled ? Color.White : Color.FromArgb(0xFA, 0xFB, 0xFC)))
                    g.FillPath(b, path);
                Color edge;
                if (!Enabled) edge = Color.FromArgb(0xED, 0xEF, 0xF3);
                else if (hover) edge = Color.FromArgb(0xB9, 0xBE, 0xCC);
                else edge = Color.FromArgb(0xD5, 0xD9, 0xE0);
                using (Pen pen = new Pen(edge, 1f)) g.DrawPath(pen, path);
                if (Focused && Enabled)
                {
                    using (GraphicsPath inner = CardPanel.Round(new Rectangle(1, 1, Width - 3, Height - 3), 7))
                    using (Pen pen = new Pen(Color.FromArgb(0x47, 0x52, 0xD6), 1.5f))
                        g.DrawPath(pen, inner);
                }
            }

            // the digits, centred in the space left of the arrow strip
            string text = val.ToString(CultureInfo.InvariantCulture);
            TextRenderer.DrawText(g, text, Font, new Rectangle(0, 0, Width - 20, Height),
                Enabled ? Color.FromArgb(0x1B, 0x1C, 0x22) : Color.FromArgb(0xA3, 0xA5, 0xAF),
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);

            // the arrow strip: a hairline and two soft triangles
            Color strip = Enabled ? Color.FromArgb(0xEA, 0xEB, 0xF0) : Color.FromArgb(0xF2, 0xF3, 0xF6);
            using (Pen pen = new Pen(strip, 1f))
            {
                g.DrawLine(pen, Width - 20, 5, Width - 20, Height - 5);
                g.DrawLine(pen, Width - 18, Height / 2, Width - 3, Height / 2);
            }

            Color arrow = Enabled ? Color.FromArgb(0x6B, 0x6E, 0x7B) : Color.FromArgb(0xC6, 0xC9, 0xD2);
            using (SolidBrush b = new SolidBrush(overUp && Enabled ? Color.FromArgb(0x47, 0x52, 0xD6) : arrow))
            {
                int cx = Width - 11, cy = 2 + (Height - 4) / 4;
                g.FillPolygon(b, new Point[] {
                    new Point(cx - 4, cy + 2), new Point(cx + 4, cy + 2), new Point(cx, cy - 2) });
            }
            using (SolidBrush b = new SolidBrush(overDown && Enabled ? Color.FromArgb(0x47, 0x52, 0xD6) : arrow))
            {
                int cx = Width - 11, cy = Height / 2 + (Height - Height / 2) / 2 - 1;
                g.FillPolygon(b, new Point[] {
                    new Point(cx - 4, cy - 2), new Point(cx + 4, cy - 2), new Point(cx, cy + 2) });
            }
        }

        protected override void OnMouseEnter(EventArgs e)
        {
            base.OnMouseEnter(e);
            hover = true;
            Invalidate();
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            base.OnMouseLeave(e);
            hover = overUp = overDown = false;
            Invalidate();
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            bool u = UpZone.Contains(e.Location);
            bool d = DownZone.Contains(e.Location);
            if (u != overUp || d != overDown)
            {
                overUp = u;
                overDown = d;
                Invalidate();
            }
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            if (!Enabled) return;
            Focus();
            if (UpZone.Contains(e.Location)) Value = val + 1;
            else if (DownZone.Contains(e.Location)) Value = val - 1;
        }

        protected override void OnMouseWheel(MouseEventArgs e)
        {
            base.OnMouseWheel(e);
            if (!Enabled) return;
            if (e.Delta > 0) Value = val + 1;
            else if (e.Delta < 0) Value = val - 1;
        }

        protected override void OnGotFocus(EventArgs e) { base.OnGotFocus(e); Invalidate(); }
        protected override void OnLostFocus(EventArgs e) { base.OnLostFocus(e); Invalidate(); }

        protected override bool IsInputKey(Keys keyData)
        {
            if (keyData == Keys.Up || keyData == Keys.Down) return true;
            return base.IsInputKey(keyData);
        }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            base.OnKeyDown(e);
            if (!Enabled) return;
            if (e.KeyCode == Keys.Up) { Value = val + 1; e.Handled = true; }
            else if (e.KeyCode == Keys.Down) { Value = val - 1; e.Handled = true; }
            else if (e.KeyCode == Keys.PageUp) { Value = val + 10; e.Handled = true; }
            else if (e.KeyCode == Keys.PageDown) { Value = val - 10; e.Handled = true; }
        }
    }

}
