using System;
using System.Collections.Generic;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

namespace ProjectMQaMcp.Editor
{
    /// <summary>
    /// Presses buttons on the modal dialogs that otherwise stop this bridge dead.
    ///
    /// The awkward part is not finding the dialog, it is that while one is up the editor's main thread
    /// sits inside the dialog's own message loop and <see cref="CommandRunner"/> never ticks. A command
    /// sent after the dialog appears is therefore never read - it waits in the requests folder until
    /// something else dismisses the box. Measured the hard way in this repo: a synthesized drag that
    /// entered a native drag loop left twelve requests queued and unprocessed until Unity was killed.
    ///
    /// So the useful shape is "arm now, click when it appears". The watcher runs on a plain background
    /// thread, which keeps running while the main thread is blocked, and touches nothing but Win32 - no
    /// Unity API is thread-safe to call from there.
    ///
    /// Since <see cref="OffThreadBridge"/> started reading requests from a background thread too, a
    /// command sent *after* the dialog is up does get read, so editor_dialog_click also handles the
    /// dialog that is already on screen: it presses it on the spot and puts the outcome in that one
    /// response. That matters more than saving a round trip. Arming and then asking separately means
    /// the answer lives in a static field, and a domain reload between the two commands wipes it - the
    /// status call then cannot tell "pressed and finished" from "never armed", while the box may still
    /// be sitting there blocking everything. Measured 2026-08-13, and it cost four minutes and a
    /// Win32 rescue from outside the editor.
    ///
    /// On the buttons themselves: EditorUtility.DisplayDialog was expected to draw them with IMGUI,
    /// leaving nothing to enumerate and only the keyboard to drive them with. Measured on 2022.3.62f1
    /// for Windows, that is not what happens - the dialog is a real Win32 one (class #32770) whose
    /// buttons came back as "0:&amp;Accept | 1:&amp;Cancel", and BM_CLICK on the chosen one closed it with the
    /// window recording exactly the choice that was asked for, by index and by label alike. So the
    /// normal path reads the real labels and presses the real button.
    ///
    /// The keyboard path stays for the case where a dialog genuinely has no child buttons, where
    /// Return means accept and Escape means cancel and a third button cannot be reached. Which path
    /// ran is always reported, because "pressed the button labelled X" and "pressed Return and assumed
    /// it meant X" are very different claims.
    /// </summary>
    internal static class EditorDialogBridge
    {
        internal static bool TryExecute(string command, CommandParameters p, CommandResponse response)
        {
            switch (command)
            {
                case "editor_dialog_click": DialogClick(p, response); return true;
                case "editor_dialog_status": DialogStatus(response); return true;
                case "editor_dialog_list": DialogList(response); return true;
                default: return false;
            }
        }

#if UNITY_EDITOR_WIN

        private static readonly object Gate = new object();
        private static Thread _watcher;
        private static Watch _current;

        /// <summary>What one arming asked for, and what came of it. Read from both threads under Gate.</summary>
        private sealed class Watch
        {
            public string titleFilter;
            public string buttonLabel;
            public int buttonIndex = -1;
            public int armMs;
            public string onMiss;

            public volatile bool finished;
            public volatile bool timedOut;
            public volatile bool missed;
            public string seenTitle = string.Empty;
            public string seenButtons = string.Empty;
            public string pressedLabel = string.Empty;
            public string method = string.Empty;
            public string failure = string.Empty;
            public bool dialogClosed;
        }

        private static void DialogClick(CommandParameters p, CommandResponse response)
        {
            var watch = new Watch
            {
                titleFilter = p.dialogTitle ?? string.Empty,
                buttonLabel = p.buttonLabel ?? string.Empty,
                buttonIndex = p.buttonIndex > 0 || !string.IsNullOrEmpty(p.buttonIndexText)
                    ? ParseIndex(p.buttonIndexText, p.buttonIndex)
                    : (string.IsNullOrEmpty(p.buttonLabel) ? 0 : -1),
                armMs = p.armMs > 0 ? p.armMs : 15000,
                onMiss = string.IsNullOrEmpty(p.onMiss) ? "cancel" : p.onMiss,
            };

            if (watch.buttonIndex < 0 && string.IsNullOrEmpty(watch.buttonLabel))
            {
                throw new ArgumentException("Provide buttonLabel or buttonIndex for editor_dialog_click.");
            }

            // A dialog that is already up is pressed here and now, and the outcome goes back in this
            // same response. Arming a watcher for it would work too, but the caller would then have to
            // send editor_dialog_status to learn whether anything was pressed - and if that second
            // command finds no watcher, it cannot tell "pressed and finished" from "never ran".
            // Measured 2026-08-13: a click armed against a live "Warning" box answered armed=true, the
            // very next status said no watcher had ever been armed, and the modal was still on screen.
            // Recovering meant driving Win32 from outside the editor and cost about four minutes.
            var alreadyUp = FindDialogs(watch.titleFilter);
            if (alreadyUp.Count > 0)
            {
                try
                {
                    Press(watch, alreadyUp[0]);
                }
                catch (Exception e)
                {
                    watch.failure = e.GetType().Name + ": " + e.Message;
                }

                watch.finished = true;

                // Kept so editor_dialog_status still describes this press if the caller asks anyway.
                lock (Gate)
                {
                    _current = watch;
                }

                ReportPress(watch, response);
                return;
            }

            lock (Gate)
            {
                if (_watcher != null && _watcher.IsAlive)
                {
                    throw new InvalidOperationException(
                        "A dialog watcher is already armed. Wait for it with editor_dialog_status, or let it " +
                        "time out, before arming another.");
                }

                _current = watch;
                _watcher = new Thread(() => Run(watch)) { IsBackground = true, Name = "MCP dialog watcher" };
                _watcher.Start();
            }

            response.AddOutput("armed", "true");
            response.AddOutput("mode", "armed");
            response.AddOutput("armMs", watch.armMs.ToString(CultureInfo.InvariantCulture));
            response.AddOutput("dialogTitleFilter", watch.titleFilter);
            response.AddOutput("wantButtonLabel", watch.buttonLabel);
            response.AddOutput("wantButtonIndex", watch.buttonIndex.ToString(CultureInfo.InvariantCulture));
            response.AddOutput("note",
                "Armed. Now send the command that raises the dialog - it will block the editor, and this " +
                "watcher will press the button from a background thread and let it go again. Read the " +
                "outcome with editor_dialog_status afterwards.");
        }

        /// <summary>
        /// Reports a press that already happened, in the same shape editor_dialog_status uses, so a
        /// caller can read either one without knowing which route ran. 'mode' is what tells them apart.
        /// </summary>
        private static void ReportPress(Watch watch, CommandResponse response)
        {
            response.AddOutput("armed", "false");
            response.AddOutput("mode", "pressed-immediately");
            response.AddOutput("finished", "true");
            response.AddOutput("timedOut", "false");
            response.AddOutput("missed", watch.missed ? "true" : "false");
            response.AddOutput("dialogTitle", watch.seenTitle ?? string.Empty);
            response.AddOutput("dialogButtons", watch.seenButtons ?? string.Empty);
            response.AddOutput("pressedLabel", watch.pressedLabel ?? string.Empty);
            response.AddOutput("method", watch.method ?? string.Empty);
            response.AddOutput("dialogClosed", watch.dialogClosed ? "true" : "false");

            if (!string.IsNullOrEmpty(watch.failure))
            {
                response.AddOutput("failure", watch.failure);
            }

            response.AddOutput("note",
                "The dialog was already open, so it was pressed straight away and the result is above - " +
                "there is no need to send editor_dialog_status for this one. Check dialogClosed: false " +
                "means the press was delivered but the box is still up.");
        }

        private static void DialogStatus(CommandResponse response)
        {
            Watch watch;
            bool alive;
            lock (Gate)
            {
                watch = _current;
                alive = _watcher != null && _watcher.IsAlive;
            }

            if (watch == null)
            {
                response.AddOutput("armed", "false");
                // Two very different things land here and the caller has to be able to tell them apart:
                // nothing was ever armed, or something was armed and the state behind it was lost -
                // which is what an assembly reload between the two commands does to these statics.
                // Either way the useful next step is the same, so it is spelled out rather than implied.
                response.AddOutput("note",
                    "No dialog watcher is on record. Either none was armed, or one was armed and its " +
                    "state was lost - a domain reload between the two commands wipes it. This says " +
                    "nothing about whether a dialog was pressed. To find out: editor_dialog_list " +
                    "answers even while the editor is blocked, so if it still lists the box, nothing " +
                    "dismissed it - send editor_dialog_click again and it will press the open dialog " +
                    "on the spot and report the outcome in that one response.");
                return;
            }

            response.AddOutput("armed", alive ? "true" : "false");
            response.AddOutput("finished", watch.finished ? "true" : "false");
            response.AddOutput("timedOut", watch.timedOut ? "true" : "false");
            // True when the button pressed was not the one asked for. The press still happened, so
            // pressedLabel is filled in - this is the flag that says not to trust it as a choice.
            response.AddOutput("missed", watch.missed ? "true" : "false");
            response.AddOutput("dialogTitle", watch.seenTitle ?? string.Empty);
            response.AddOutput("dialogButtons", watch.seenButtons ?? string.Empty);
            // The label actually pressed, when it could be read off a real button. Empty for the
            // keyboard route, where there is no label to read and 'method' says so.
            response.AddOutput("pressedLabel", watch.pressedLabel ?? string.Empty);
            response.AddOutput("method", watch.method ?? string.Empty);
            response.AddOutput("dialogClosed", watch.dialogClosed ? "true" : "false");

            if (!string.IsNullOrEmpty(watch.failure))
            {
                response.AddOutput("failure", watch.failure);
            }
        }

        private static void DialogList(CommandResponse response)
        {
            var dialogs = FindDialogs(string.Empty);
            var sb = new StringBuilder("[");
            for (var i = 0; i < dialogs.Count; i++)
            {
                if (i > 0) sb.Append(',');
                sb.Append("{\"hwnd\":\"0x").Append(dialogs[i].handle.ToInt64().ToString("X", CultureInfo.InvariantCulture));
                sb.Append("\",\"class\":\"").Append(Escape(dialogs[i].className));
                sb.Append("\",\"title\":\"").Append(Escape(dialogs[i].title));
                sb.Append("\",\"buttons\":\"").Append(Escape(DescribeButtons(ReadButtons(dialogs[i].handle))));
                sb.Append("\"}");
            }
            sb.Append(']');

            response.AddOutput("count", dialogs.Count.ToString(CultureInfo.InvariantCulture));
            response.AddOutput("dialogs", sb.ToString());
            if (dialogs.Count == 0)
            {
                response.AddOutput("note",
                    "Nothing found - which is the normal answer, because while a modal is up the editor " +
                    "does not tick and this command cannot run. Use editor_dialog_click to arm first.");
            }
        }

        // ------------------------------------------------------------------ the watcher

        private static void Run(Watch watch)
        {
            var deadline = DateTime.UtcNow.AddMilliseconds(watch.armMs);

            try
            {
                while (DateTime.UtcNow < deadline)
                {
                    var dialogs = FindDialogs(watch.titleFilter);
                    if (dialogs.Count > 0)
                    {
                        Press(watch, dialogs[0]);
                        return;
                    }

                    Thread.Sleep(120);
                }

                watch.timedOut = true;
                watch.failure = "No matching dialog appeared within " +
                                watch.armMs.ToString(CultureInfo.InvariantCulture) + "ms.";
            }
            catch (Exception e)
            {
                watch.failure = e.GetType().Name + ": " + e.Message;
            }
            finally
            {
                watch.finished = true;
            }
        }

        private static void Press(Watch watch, Dialog dialog)
        {
            watch.seenTitle = dialog.title;

            var buttons = ReadButtons(dialog.handle);
            watch.seenButtons = DescribeButtons(buttons);

            if (buttons.Count > 0)
            {
                var chosen = Choose(buttons, watch);

                if (chosen < 0)
                {
                    // Leaving it alone would be worse than pressing the wrong thing. Measured: a watcher
                    // armed for a label the dialog did not have pressed nothing, the modal stayed up, and
                    // the editor never ticked again - every later command timed out until Unity was
                    // killed. So by default the last button is pressed to hand the editor back, and the
                    // failure below says plainly that it was not the one that was asked for.
                    if (string.Equals(watch.onMiss, "leave", StringComparison.OrdinalIgnoreCase))
                    {
                        watch.failure = "No button matched, and onMiss was 'leave', so nothing was pressed. " +
                                        "The editor stays blocked until this dialog is dismissed by hand. " +
                                        "The dialog offered: " + watch.seenButtons;
                        return;
                    }

                    chosen = buttons.Count - 1;
                    watch.failure = "No button matched '" +
                                    (string.IsNullOrEmpty(watch.buttonLabel)
                                        ? "index " + watch.buttonIndex.ToString(CultureInfo.InvariantCulture)
                                        : watch.buttonLabel) +
                                    "'. Pressed '" + buttons[chosen].text + "' instead to let the editor run " +
                                    "again - treat this as a failed press, not a choice. The dialog offered: " +
                                    watch.seenButtons;
                    watch.missed = true;
                }

                NativeMethods.SendMessage(buttons[chosen].handle, NativeMethods.BM_CLICK, IntPtr.Zero, IntPtr.Zero);
                watch.pressedLabel = buttons[chosen].text;
                watch.method = watch.missed ? "bm-click-fallback" : "bm-click";
            }
            else
            {
                // IMGUI-drawn buttons: nothing to enumerate and nothing to click, so the only lever is
                // the keyboard. Return accepts, Escape cancels; a third button cannot be reached at all.
                var wantsCancel = watch.buttonIndex == 1
                                  || LooksLikeCancel(watch.buttonLabel);

                var key = wantsCancel ? NativeMethods.VK_ESCAPE : NativeMethods.VK_RETURN;
                if (!SendKeyTo(dialog.handle, key))
                {
                    watch.failure = "The dialog has no child buttons to click and the key could not be posted.";
                    return;
                }

                watch.method = wantsCancel ? "keyboard-escape" : "keyboard-return";
                if (watch.buttonIndex > 1)
                {
                    watch.failure = "This dialog draws its buttons with IMGUI, so only the accept (Return) and " +
                                    "cancel (Escape) buttons can be reached. Index " +
                                    watch.buttonIndex.ToString(CultureInfo.InvariantCulture) +
                                    " is not reachable; Return was sent instead.";
                }
            }

            // Whether the box actually went away is the only proof the press landed.
            for (var i = 0; i < 40 && NativeMethods.IsWindow(dialog.handle) &&
                            NativeMethods.IsWindowVisible(dialog.handle); i++)
            {
                Thread.Sleep(50);
            }

            watch.dialogClosed = !NativeMethods.IsWindow(dialog.handle) ||
                                 !NativeMethods.IsWindowVisible(dialog.handle);

            if (!watch.dialogClosed && string.IsNullOrEmpty(watch.failure))
            {
                watch.failure = "The press was delivered but the dialog is still on screen after 2s.";
            }
        }

        private static int Choose(List<Button> buttons, Watch watch)
        {
            if (!string.IsNullOrEmpty(watch.buttonLabel))
            {
                for (var i = 0; i < buttons.Count; i++)
                {
                    if (Contains(buttons[i].text, watch.buttonLabel)) return i;
                }

                return -1;
            }

            return watch.buttonIndex >= 0 && watch.buttonIndex < buttons.Count ? watch.buttonIndex : -1;
        }

        private static bool LooksLikeCancel(string label)
        {
            if (string.IsNullOrEmpty(label)) return false;
            return Contains(label, "cancel") || Contains(label, "no") || Contains(label, "취소");
        }

        private static bool Contains(string haystack, string needle)
        {
            if (string.IsNullOrEmpty(haystack) || string.IsNullOrEmpty(needle)) return false;
            return haystack.IndexOf(needle, StringComparison.OrdinalIgnoreCase) >= 0;
        }

        /// <summary>
        /// Posts a key to a window owned by another thread. The input queues have to be attached first,
        /// otherwise the dialog's thread does not consider this one entitled to give it focus and the
        /// key is dropped.
        /// </summary>
        private static bool SendKeyTo(IntPtr hwnd, int virtualKey)
        {
            var target = NativeMethods.GetWindowThreadProcessId(hwnd, out _);
            var self = NativeMethods.GetCurrentThreadId();
            var attached = target != self && NativeMethods.AttachThreadInput(self, target, true);

            try
            {
                NativeMethods.SetForegroundWindow(hwnd);

                var focus = NativeMethods.GetFocus();
                var destination = focus != IntPtr.Zero ? focus : hwnd;

                var down = NativeMethods.PostMessage(destination, NativeMethods.WM_KEYDOWN,
                    new IntPtr(virtualKey), IntPtr.Zero);
                var up = NativeMethods.PostMessage(destination, NativeMethods.WM_KEYUP,
                    new IntPtr(virtualKey), IntPtr.Zero);
                return down && up;
            }
            finally
            {
                if (attached)
                {
                    NativeMethods.AttachThreadInput(self, target, false);
                }
            }
        }

        // ------------------------------------------------------------------ window scraping

        private struct Dialog
        {
            public IntPtr handle;
            public string title;
            public string className;
        }

        private struct Button
        {
            public IntPtr handle;
            public string text;
        }

        private static List<Dialog> FindDialogs(string titleFilter)
        {
            var found = new List<Dialog>();
            var processId = NativeMethods.GetCurrentProcessId();
            var main = NativeMethods.GetActiveWindow();

            NativeMethods.EnumWindows((hwnd, lParam) =>
            {
                try
                {
                    if (!NativeMethods.IsWindowVisible(hwnd)) return true;

                    uint owner;
                    NativeMethods.GetWindowThreadProcessId(hwnd, out owner);
                    if (owner != processId) return true;

                    var className = ReadClassName(hwnd);
                    var title = ReadTitle(hwnd);

                    // A dialog is either a real Windows dialog box, or a Unity container that is not the
                    // main editor window and is disabled-parent style. Class is the reliable half; the
                    // rest is filtered by the caller's title so a normal tool window is never pressed.
                    var isNativeDialog = className == "#32770";
                    if (!isNativeDialog)
                    {
                        if (hwnd == main) return true;
                        if (string.IsNullOrEmpty(titleFilter)) return true;
                    }

                    if (!string.IsNullOrEmpty(titleFilter) && !Contains(title, titleFilter)) return true;

                    found.Add(new Dialog { handle = hwnd, title = title, className = className });
                }
                catch (Exception)
                {
                    // One bad window must not abort the sweep.
                }

                return true;
            }, IntPtr.Zero);

            return found;
        }

        private static List<Button> ReadButtons(IntPtr dialog)
        {
            var buttons = new List<Button>();

            NativeMethods.EnumChildWindows(dialog, (child, lParam) =>
            {
                try
                {
                    if (ReadClassName(child) != "Button") return true;
                    if (!NativeMethods.IsWindowVisible(child)) return true;
                    buttons.Add(new Button { handle = child, text = ReadTitle(child) });
                }
                catch (Exception)
                {
                }

                return true;
            }, IntPtr.Zero);

            return buttons;
        }

        private static string DescribeButtons(List<Button> buttons)
        {
            if (buttons.Count == 0) return string.Empty;

            var parts = new string[buttons.Count];
            for (var i = 0; i < buttons.Count; i++)
            {
                parts[i] = i.ToString(CultureInfo.InvariantCulture) + ":" + buttons[i].text;
            }

            return string.Join(" | ", parts);
        }

        private static string ReadTitle(IntPtr hwnd)
        {
            var text = new StringBuilder(512);
            NativeMethods.GetWindowText(hwnd, text, text.Capacity);
            return text.ToString();
        }

        private static string ReadClassName(IntPtr hwnd)
        {
            var name = new StringBuilder(256);
            NativeMethods.GetClassName(hwnd, name, name.Capacity);
            return name.ToString();
        }

        private static int ParseIndex(string text, int fallback)
        {
            int parsed;
            if (!string.IsNullOrEmpty(text) &&
                int.TryParse(text.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out parsed))
            {
                return parsed;
            }

            return fallback;
        }

        private static string Escape(string value)
        {
            if (string.IsNullOrEmpty(value)) return string.Empty;
            return value.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\n", " ").Replace("\r", " ");
        }

        private static class NativeMethods
        {
            internal const uint BM_CLICK = 0x00F5;
            internal const uint WM_KEYDOWN = 0x0100;
            internal const uint WM_KEYUP = 0x0101;
            internal const int VK_RETURN = 0x0D;
            internal const int VK_ESCAPE = 0x1B;

            internal delegate bool EnumWindowsProc(IntPtr hwnd, IntPtr lParam);

            [DllImport("user32.dll", SetLastError = true)]
            internal static extern bool EnumWindows(EnumWindowsProc callback, IntPtr lParam);

            [DllImport("user32.dll", SetLastError = true)]
            internal static extern bool EnumChildWindows(IntPtr parent, EnumWindowsProc callback, IntPtr lParam);

            [DllImport("user32.dll", SetLastError = true)]
            internal static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint processId);

            [DllImport("user32.dll", SetLastError = true)]
            internal static extern bool IsWindowVisible(IntPtr hwnd);

            [DllImport("user32.dll", SetLastError = true)]
            internal static extern bool IsWindow(IntPtr hwnd);

            [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode, EntryPoint = "GetWindowTextW")]
            internal static extern int GetWindowText(IntPtr hwnd, StringBuilder text, int count);

            [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode, EntryPoint = "GetClassNameW")]
            internal static extern int GetClassName(IntPtr hwnd, StringBuilder name, int count);

            [DllImport("user32.dll", SetLastError = true)]
            internal static extern IntPtr SendMessage(IntPtr hwnd, uint message, IntPtr wParam, IntPtr lParam);

            [DllImport("user32.dll", SetLastError = true)]
            internal static extern bool PostMessage(IntPtr hwnd, uint message, IntPtr wParam, IntPtr lParam);

            [DllImport("user32.dll", SetLastError = true)]
            internal static extern bool AttachThreadInput(uint attachTo, uint attachFrom, bool attach);

            [DllImport("kernel32.dll")]
            internal static extern uint GetCurrentThreadId();

            [DllImport("kernel32.dll")]
            internal static extern uint GetCurrentProcessId();

            [DllImport("user32.dll", SetLastError = true)]
            internal static extern bool SetForegroundWindow(IntPtr hwnd);

            [DllImport("user32.dll", SetLastError = true)]
            internal static extern IntPtr GetFocus();

            [DllImport("user32.dll", SetLastError = true)]
            internal static extern IntPtr GetActiveWindow();
        }

#else
        private static void DialogClick(CommandParameters p, CommandResponse response)
        {
            throw new NotSupportedException("editor_dialog_click needs the Windows editor.");
        }

        private static void DialogStatus(CommandResponse response)
        {
            throw new NotSupportedException("editor_dialog_status needs the Windows editor.");
        }

        private static void DialogList(CommandResponse response)
        {
            throw new NotSupportedException("editor_dialog_list needs the Windows editor.");
        }
#endif
    }
}
