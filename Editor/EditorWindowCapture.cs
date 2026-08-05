using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using UnityEditor;
using UnityEngine;
using Object = UnityEngine.Object;

namespace ProjectMQaMcp.Editor
{
    /// <summary>
    /// Captures the real on-screen pixels of one EditorWindow, docked or floating.
    ///
    /// The older editor_window_screenshot path goes through InternalEditorUtility.ReadScreenPixel, which
    /// reads the *main* editor window's framebuffer. A window living in its own floating container is
    /// simply not in those pixels, so that path either refuses or writes a blank image, and even for a
    /// docked window it hands back whatever happens to sit at those coordinates - a GameView, a
    /// SceneView, a neighbouring tool.
    ///
    /// This class works from the operating system's window instead:
    ///   1. Find the OS window (HWND) that hosts the target EditorWindow's ContainerWindow. Unity does
    ///      not hand out that handle, so it is recovered by matching the container's rect against the
    ///      process's top-level windows - which also yields the points-to-pixels scale as a by-product,
    ///      so DPI never has to be assumed.
    ///   2. Ask that window for its own pixels with PrintWindow(PW_RENDERFULLCONTENT), which reads the
    ///      window's composition surface. It sees only that window, needs no focus, and does not care
    ///      what is stacked on top of it.
    ///   3. Crop to the target EditorWindow's view rect, so sibling tabs and other views in the same
    ///      container are excluded.
    ///
    /// Two fallbacks follow when a driver or Windows version refuses PrintWindow: a plain screen read of
    /// the same rect (correct pixels, but occluded regions show whatever is on top - reported as
    /// occluded), and finally the legacy framebuffer read for docked windows. Whichever ran is reported
    /// as captureBackend, and a capture that comes back empty is a failure with a reason, never a
    /// successful blank PNG.
    /// </summary>
    internal static class EditorWindowCapture
    {
        internal const string BackendPrintWindow = "gdi-printwindow";
        internal const string BackendScreen = "gdi-screen";
        internal const string BackendFramebuffer = "unity-framebuffer";

        internal sealed class Options
        {
            /// <summary>"auto" (default), or one backend name to force for diagnosis.</summary>
            public string backend = "auto";

            /// <summary>Include the dock tab strip / host chrome around the window's content.</summary>
            public bool includeChrome;

            /// <summary>Milliseconds to let the compositor catch up after the repaint before reading pixels.</summary>
            public int settleMs = 24;

            /// <summary>Treat a single-colour capture as a real result instead of trying the next backend.</summary>
            public bool allowUniform;

            /// <summary>
            /// Draw the process's own popups and modal dialogs that sit over the target into the same
            /// image, instead of returning the target as if nothing were on top of it.
            /// </summary>
            public bool includePopups;
        }

        internal sealed class Result
        {
            public bool success;
            public string backend = "none";
            public string failureReason;

            public string outputPath;
            public long pngBytes;
            public bool pngExists;
            public int imageWidth;
            public int imageHeight;
            public bool uniformColor;
            public bool occluded;

            public Rect windowRect;
            public Rect hostRect;
            public Rect containerRect;
            public Rect captureRect;      // desktop points actually captured
            public float pixelsPerPoint = 1f;
            public float scale = 1f;      // points -> physical pixels, measured from the matched OS window
            public bool inMainWindow;

            public long hwnd;
            public string hwndTitle = string.Empty;
            public Rect hwndRect;         // physical pixels
            public string mappingBasis = "none";
            public int systemDpi;
            public string visibleView = string.Empty;

            public int popupsComposited = -1;   // -1 = includePopups was not asked for
            public string popupTitles = string.Empty;

            public readonly List<string> notes = new List<string>();
            public readonly List<string> attempts = new List<string>();
        }

        // ------------------------------------------------------------------ entry point

        internal static Result Capture(EditorWindow window, string outputPath, Options options)
        {
            if (window == null) throw new ArgumentNullException("window");
            options = options ?? new Options();

            var result = new Result { outputPath = outputPath };

            // Repaint without focusing: the capture must not reorder the user's windows, and
            // RepaintImmediately refreshes the view in place.
            EditorToolBridge.RepaintImmediate(window);

            result.windowRect = window.position;
            result.hostRect = EditorToolBridge.HostScreenRect(window);
            result.containerRect = EditorToolBridge.ContainerRect(window);
            result.pixelsPerPoint = EditorGUIUtility.pixelsPerPoint > 0f ? EditorGUIUtility.pixelsPerPoint : 1f;

            var targetContainer = EditorToolBridge.ContainerOf(window);
            var mainContainer = EditorToolBridge.MainContainer();
            result.inMainWindow = targetContainer != null && mainContainer != null
                                  && ReferenceEquals(targetContainer, mainContainer);

            // A window sharing a dock area with others only owns pixels while it is the selected tab.
            // Capturing a background tab would hand back the tab that *is* drawn - the classic "asked for
            // Scene, got Game" - so it is refused by name rather than silently mis-answered.
            var visible = EditorToolBridge.VisibleViewOf(window);
            result.visibleView = visible != null ? visible.GetType().FullName : string.Empty;
            if (visible != null && !ReferenceEquals(visible, window))
            {
                var visibleTitle = visible.titleContent != null ? visible.titleContent.text : visible.GetType().Name;
                result.failureReason =
                    "This window is a background tab in its dock area, so it has no pixels on screen right now - " +
                    "the visible tab is '" + visibleTitle + "' (" + visible.GetType().FullName + "), and capturing " +
                    "would return that window instead. Select the tab first (editor_window_focus) and retry; this " +
                    "command deliberately never changes focus or window order on its own.";
                return result;
            }

            // The host view is the rect the window actually owns on the desktop. Its own position is
            // container-relative and cannot place a capture on its own.
            var viewRect = result.hostRect.width > 0f && result.hostRect.height > 0f
                ? result.hostRect
                : result.windowRect;

            if (viewRect.width < 1f || viewRect.height < 1f)
            {
                result.failureReason = "The target window reports an empty rect (" + EditorToolBridge.RectJson(viewRect) +
                                       "). It is probably minimized or was never shown.";
                return result;
            }

            if (!options.includeChrome)
            {
                var border = EditorToolBridge.BorderSize(window, result.notes);
                if (border != null)
                {
                    var inset = new Rect(
                        viewRect.x + border.left,
                        viewRect.y + border.top,
                        viewRect.width - border.left - border.right,
                        viewRect.height - border.top - border.bottom);

                    if (inset.width >= 1f && inset.height >= 1f)
                    {
                        result.notes.Add("chrome inset applied: l=" + border.left + " t=" + border.top +
                                         " r=" + border.right + " b=" + border.bottom);
                        viewRect = inset;
                    }
                }
            }

            result.captureRect = viewRect;

            var containerRect = result.containerRect.width > 0f ? result.containerRect : viewRect;
            var wanted = (options.backend ?? "auto").Trim().ToLowerInvariant();

#if UNITY_EDITOR_WIN
            if (wanted == "auto" || wanted == BackendPrintWindow || wanted == "printwindow" ||
                wanted == BackendScreen || wanted == "screen")
            {
                OsWindow os;
                if (TryResolveOsWindow(containerRect, result, out os))
                {
                    result.hwnd = os.handle.ToInt64();
                    result.hwndTitle = os.title;
                    result.hwndRect = new Rect(os.windowRect.left, os.windowRect.top, os.windowRect.Width, os.windowRect.Height);
                    result.mappingBasis = os.basis;
                    result.scale = os.scale;
                    result.systemDpi = os.dpi;

                    if (NativeMethods.IsIconic(os.handle))
                    {
                        result.failureReason = "The OS window hosting this EditorWindow is minimized, so it has no on-screen pixels. " +
                                               "Restore it and retry.";
                        return result;
                    }

                    if (options.settleMs > 0)
                    {
                        System.Threading.Thread.Sleep(Mathf.Clamp(options.settleMs, 0, 2000));
                    }

                    if (wanted == "auto" || wanted == BackendPrintWindow || wanted == "printwindow")
                    {
                        if (TryBackend(BackendPrintWindow, os, viewRect, outputPath, options, result))
                        {
                            return result;
                        }
                    }

                    if (wanted == "auto" || wanted == BackendScreen || wanted == "screen")
                    {
                        result.occluded = IsOccluded(os.handle, viewRect, os.scale);

                        // This backend copies whatever the desktop shows at those coordinates. With
                        // another window on top that is somebody else's content, and handing it back
                        // would be worse than returning nothing - so it is only allowed when the caller
                        // asked for this backend by name.
                        if (result.occluded && wanted == "auto")
                        {
                            result.attempts.Add(BackendScreen +
                                ": skipped because another window covers the target, so a screen read would " +
                                "capture that window instead");
                        }
                        else if (TryBackend(BackendScreen, os, viewRect, outputPath, options, result))
                        {
                            return result;
                        }
                    }
                }
            }
#else
            result.notes.Add("Native window capture is Windows-only; falling back to the editor framebuffer.");
#endif

            if (wanted == "auto" || wanted == BackendFramebuffer || wanted == "framebuffer")
            {
                if (TryFramebuffer(window, viewRect, outputPath, options, result))
                {
                    return result;
                }
            }

            if (string.IsNullOrEmpty(result.failureReason))
            {
                result.failureReason = "No capture backend produced pixels. Attempts: " + string.Join(" | ", result.attempts.ToArray());
            }

            return result;
        }

        // ------------------------------------------------------------------ backends

#if UNITY_EDITOR_WIN
        private static bool TryBackend(string backend, OsWindow os, Rect viewRect, string outputPath,
            Options options, Result result)
        {
            try
            {
                Color32[] pixels;
                int width, height;
                bool wholeWindowUniform;
                if (!CaptureNative(backend, os, viewRect, options, out pixels, out width, out height, out wholeWindowUniform, result))
                {
                    return false;
                }

                // "Did the backend work" and "is this window blank" are different questions, and the
                // whole-window bitmap is what answers the first: a driver that ignores
                // PW_RENDERFULLCONTENT returns a flat frame for the entire window, while an empty
                // Inspector is legitimately one flat colour inside an otherwise busy editor window.
                if (wholeWindowUniform && !options.allowUniform)
                {
                    result.attempts.Add(backend +
                        ": the whole window bitmap came back a single flat colour, so this backend produced no real pixels");
                    return false;
                }

                var uniform = IsUniform(pixels);

                WritePng(pixels, width, height, outputPath, result);
                result.backend = backend;
                result.uniformColor = uniform;
                result.success = true;
                result.failureReason = null;
                if (uniform)
                {
                    result.notes.Add("the captured region is a single flat colour - the window is rendering, " +
                                     "but this part of it is empty.");
                }

                return true;
            }
            catch (Exception e)
            {
                result.attempts.Add(backend + ": " + e.Message);
                return false;
            }
        }

        private static bool CaptureNative(string backend, OsWindow os, Rect viewRect, Options options,
            out Color32[] pixels, out int width, out int height, out bool wholeWindowUniform, Result result)
        {
            pixels = null;
            width = 0;
            height = 0;
            wholeWindowUniform = false;

            // The bitmap always covers the whole OS window so both backends share one cropping rule.
            var srcLeft = os.windowRect.left;
            var srcTop = os.windowRect.top;
            var srcWidth = os.windowRect.Width;
            var srcHeight = os.windowRect.Height;
            if (srcWidth <= 0 || srcHeight <= 0)
            {
                result.attempts.Add(backend + ": OS window rect is empty");
                return false;
            }

            byte[] bgra;
            if (!ReadWindowBits(backend, os.handle, srcLeft, srcTop, srcWidth, srcHeight, options, result, out bgra))
            {
                result.attempts.Add(backend + ": the bit blit failed (last error " +
                                    Marshal.GetLastWin32Error().ToString(CultureInfo.InvariantCulture) + ")");
                return false;
            }

            wholeWindowUniform = IsUniformBgra(bgra);

            // Crop: the view rect is in Unity points on the desktop, the bitmap is in physical pixels
            // whose origin is the OS window's top-left corner.
            var cropX = Mathf.RoundToInt(viewRect.x * os.scale) - srcLeft;
            var cropY = Mathf.RoundToInt(viewRect.y * os.scale) - srcTop;
            var cropW = Mathf.RoundToInt(viewRect.width * os.scale);
            var cropH = Mathf.RoundToInt(viewRect.height * os.scale);

            var clampedX = Mathf.Clamp(cropX, 0, Mathf.Max(0, srcWidth - 1));
            var clampedY = Mathf.Clamp(cropY, 0, Mathf.Max(0, srcHeight - 1));
            var clampedW = Mathf.Clamp(cropW, 1, srcWidth - clampedX);
            var clampedH = Mathf.Clamp(cropH, 1, srcHeight - clampedY);

            if (clampedX != cropX || clampedY != cropY || clampedW != cropW || clampedH != cropH)
            {
                result.notes.Add("crop clamped from " + cropX + "," + cropY + " " + cropW + "x" + cropH +
                                 " to " + clampedX + "," + clampedY + " " + clampedW + "x" + clampedH +
                                 " (window bitmap is " + srcWidth + "x" + srcHeight + ")");
            }

            width = clampedW;
            height = clampedH;
            pixels = new Color32[width * height];

            // The DIB is top-down; Texture2D wants bottom-up, so rows are read in reverse.
            for (var row = 0; row < height; row++)
            {
                var srcRow = clampedY + row;
                var srcIndex = (srcRow * srcWidth + clampedX) * 4;
                var dstIndex = (height - 1 - row) * width;
                for (var col = 0; col < width; col++)
                {
                    var b = bgra[srcIndex];
                    var g = bgra[srcIndex + 1];
                    var r = bgra[srcIndex + 2];
                    pixels[dstIndex + col] = new Color32(r, g, b, 255);
                    srcIndex += 4;
                }
            }

            return true;
        }

        private static bool ReadWindowBits(string backend, IntPtr hwnd, int left, int top, int width, int height,
            Options options, Result result, out byte[] bgra)
        {
            bgra = null;

            var screenDc = NativeMethods.GetDC(IntPtr.Zero);
            if (screenDc == IntPtr.Zero)
            {
                return false;
            }

            var memoryDc = IntPtr.Zero;
            var bitmap = IntPtr.Zero;
            var previous = IntPtr.Zero;
            try
            {
                memoryDc = NativeMethods.CreateCompatibleDC(screenDc);
                if (memoryDc == IntPtr.Zero) return false;

                bitmap = NativeMethods.CreateCompatibleBitmap(screenDc, width, height);
                if (bitmap == IntPtr.Zero) return false;

                previous = NativeMethods.SelectObject(memoryDc, bitmap);

                if (backend == BackendPrintWindow)
                {
                    // PW_RENDERFULLCONTENT makes Windows render the window's own composition surface,
                    // which is what makes a hardware-accelerated, partly covered window capturable.
                    if (!NativeMethods.PrintWindow(hwnd, memoryDc, NativeMethods.PW_RENDERFULLCONTENT))
                    {
                        return false;
                    }

                    // That surface is the target alone - a confirmation box or a right-click menu is a
                    // separate window and simply is not in it. Each one is asked for its own pixels the
                    // same way and drawn into this bitmap at its offset. Only for this backend: the
                    // screen backend reads whatever is already on top, so it has them by construction.
                    if (options != null && options.includePopups)
                    {
                        CompositePopups(hwnd, memoryDc, left, top, width, height, result);
                    }
                }
                else
                {
                    if (!NativeMethods.BitBlt(memoryDc, 0, 0, width, height, screenDc, left, top,
                            NativeMethods.SRCCOPY | NativeMethods.CAPTUREBLT))
                    {
                        return false;
                    }
                }

                var header = new NativeMethods.BITMAPINFO
                {
                    bmiHeader = new NativeMethods.BITMAPINFOHEADER
                    {
                        biSize = (uint)Marshal.SizeOf(typeof(NativeMethods.BITMAPINFOHEADER)),
                        biWidth = width,
                        biHeight = -height, // negative: top-down rows
                        biPlanes = 1,
                        biBitCount = 32,
                        biCompression = NativeMethods.BI_RGB,
                    },
                    bmiColors = new uint[4],
                };

                var buffer = new byte[width * height * 4];
                var copied = NativeMethods.GetDIBits(memoryDc, bitmap, 0, (uint)height, buffer, ref header,
                    NativeMethods.DIB_RGB_COLORS);
                if (copied == 0)
                {
                    return false;
                }

                bgra = buffer;
                return true;
            }
            finally
            {
                if (previous != IntPtr.Zero) NativeMethods.SelectObject(memoryDc, previous);
                if (bitmap != IntPtr.Zero) NativeMethods.DeleteObject(bitmap);
                if (memoryDc != IntPtr.Zero) NativeMethods.DeleteDC(memoryDc);
                NativeMethods.ReleaseDC(IntPtr.Zero, screenDc);
            }
        }

        /// <summary>
        /// Draws this process's popups and modal dialogs that sit over the target window into the
        /// target's own bitmap, so one image shows what the screen shows.
        ///
        /// Each popup is asked for its pixels with PrintWindow, exactly like the target was. It is
        /// deliberately not a desktop read: a locked or disconnected screen hands back white, and a
        /// screen read would also pull in whatever unrelated application happens to be sitting there.
        /// Only windows above the target in Z-order are drawn, and they are drawn bottom-most first so
        /// the stacking in the image matches the stacking on screen.
        /// </summary>
        private static void CompositePopups(IntPtr target, IntPtr destinationDc, int left, int top,
            int width, int height, Result result)
        {
            // Claim the counter up front so every exit below reports "the pass ran" rather than
            // looking like the option was never asked for.
            result.popupsComposited = 0;

            var above = new List<Candidate>();
            var found = false;

            // EnumWindows walks front to back, so everything seen before the target is over it.
            foreach (var candidate in EnumerateProcessWindows())
            {
                if (candidate.handle == target)
                {
                    found = true;
                    break;
                }

                above.Add(candidate);
            }

            if (!found)
            {
                // The target was not in the enumeration at all - without knowing where it sits in the
                // stack there is no safe answer about which windows are over it, so nothing is drawn.
                result.notes.Add("includePopups: the target window was not found in the process window " +
                                 "list, so no overlay could be placed with confidence");
                return;
            }

            var drawn = new List<string>();

            // Bottom-most of the overlapping windows first, so a menu over a dialog stays over it.
            for (var i = above.Count - 1; i >= 0; i--)
            {
                var popup = above[i];

                NativeMethods.RECT rect;
                if (!NativeMethods.GetWindowRect(popup.handle, out rect)) continue;

                var popupWidth = rect.Width;
                var popupHeight = rect.Height;
                if (popupWidth <= 0 || popupHeight <= 0) continue;

                // Overlap in the target's bitmap space.
                var x0 = Math.Max(rect.left, left);
                var y0 = Math.Max(rect.top, top);
                var x1 = Math.Min(rect.right, left + width);
                var y1 = Math.Min(rect.bottom, top + height);
                if (x1 <= x0 || y1 <= y0) continue;

                var popupDc = IntPtr.Zero;
                var popupBitmap = IntPtr.Zero;
                var popupPrevious = IntPtr.Zero;
                try
                {
                    popupDc = NativeMethods.CreateCompatibleDC(destinationDc);
                    if (popupDc == IntPtr.Zero) continue;

                    popupBitmap = NativeMethods.CreateCompatibleBitmap(destinationDc, popupWidth, popupHeight);
                    if (popupBitmap == IntPtr.Zero) continue;

                    popupPrevious = NativeMethods.SelectObject(popupDc, popupBitmap);

                    if (!NativeMethods.PrintWindow(popup.handle, popupDc, NativeMethods.PW_RENDERFULLCONTENT))
                    {
                        result.notes.Add("includePopups: '" + popup.title + "' refused PrintWindow and was left out");
                        continue;
                    }

                    if (!NativeMethods.BitBlt(destinationDc, x0 - left, y0 - top, x1 - x0, y1 - y0,
                            popupDc, x0 - rect.left, y0 - rect.top, NativeMethods.SRCCOPY))
                    {
                        result.notes.Add("includePopups: '" + popup.title + "' could not be blitted into the frame");
                        continue;
                    }

                    drawn.Add(string.IsNullOrEmpty(popup.title) ? "(untitled)" : popup.title);
                }
                finally
                {
                    if (popupPrevious != IntPtr.Zero) NativeMethods.SelectObject(popupDc, popupPrevious);
                    if (popupBitmap != IntPtr.Zero) NativeMethods.DeleteObject(popupBitmap);
                    if (popupDc != IntPtr.Zero) NativeMethods.DeleteDC(popupDc);
                }
            }

            result.popupsComposited = drawn.Count;
            result.popupTitles = string.Join(" | ", drawn.ToArray());
        }

        /// <summary>Reports whether something else is drawn over the middle of the target rect.</summary>
        private static bool IsOccluded(IntPtr hwnd, Rect viewRect, float scale)
        {
            try
            {
                var point = new NativeMethods.POINT
                {
                    x = Mathf.RoundToInt((viewRect.x + viewRect.width * 0.5f) * scale),
                    y = Mathf.RoundToInt((viewRect.y + viewRect.height * 0.5f) * scale),
                };

                var atPoint = NativeMethods.WindowFromPoint(point);
                if (atPoint == IntPtr.Zero) return false;

                var root = NativeMethods.GetAncestor(atPoint, NativeMethods.GA_ROOT);
                return root != IntPtr.Zero && root != hwnd;
            }
            catch (Exception)
            {
                return false;
            }
        }

        // ------------------------------------------------------------------ OS window matching

        private struct OsWindow
        {
            public IntPtr handle;
            public string title;
            public NativeMethods.RECT windowRect;
            public float scale;
            public string basis;
            public int dpi;
        }

        /// <summary>
        /// Finds the process's top-level window whose rect matches the container, and measures the
        /// points-to-pixels scale from that match.
        ///
        /// Unity keeps the native handle private, and guessing by title breaks as soon as a tool renames
        /// its tab. Matching by geometry is checkable instead of hopeful: the scale that maps the
        /// container's size onto a candidate's size must also map its position, and the residual is
        /// reported so a wrong match is visible in the response. That measured scale is also what makes
        /// this correct at 100%, 125%, 150% and 200% without reading any DPI setting.
        /// </summary>
        private static bool TryResolveOsWindow(Rect containerRect, Result result, out OsWindow best)
        {
            best = default(OsWindow);

            if (containerRect.width < 1f || containerRect.height < 1f)
            {
                result.attempts.Add("os-window: container rect is empty " + EditorToolBridge.RectJson(containerRect));
                return false;
            }

            var candidates = EnumerateProcessWindows();
            if (candidates.Count == 0)
            {
                result.attempts.Add("os-window: this process has no visible top-level windows");
                return false;
            }

            var bestScore = float.MaxValue;
            var found = false;
            var diagnostics = new List<string>();

            foreach (var candidate in candidates)
            {
                foreach (var basis in new[] { "window", "client", "dwm" })
                {
                    NativeMethods.RECT rect;
                    if (!TryGetBasisRect(candidate.handle, basis, out rect)) continue;

                    var w = rect.Width;
                    var h = rect.Height;
                    if (w <= 0 || h <= 0) continue;

                    var sx = w / containerRect.width;
                    var sy = h / containerRect.height;
                    if (Mathf.Abs(sx - sy) > 0.08f) continue;
                    if (sx < 0.4f || sx > 6f) continue;

                    var scale = (sx + sy) * 0.5f;
                    var score = Mathf.Abs(rect.left - containerRect.x * scale) +
                                Mathf.Abs(rect.top - containerRect.y * scale) +
                                Mathf.Abs(w - containerRect.width * scale) +
                                Mathf.Abs(h - containerRect.height * scale);

                    if (diagnostics.Count < 8)
                    {
                        diagnostics.Add(string.Format(CultureInfo.InvariantCulture,
                            "hwnd=0x{0:X} basis={1} rect={2},{3} {4}x{5} scale={6:F3} score={7:F1} title='{8}'",
                            candidate.handle.ToInt64(), basis, rect.left, rect.top, w, h, scale, score,
                            Truncate(candidate.title, 40)));
                    }

                    if (score >= bestScore) continue;

                    // The bitmap origin must be the full window rect, whichever basis matched.
                    NativeMethods.RECT windowRect;
                    if (!NativeMethods.GetWindowRect(candidate.handle, out windowRect)) continue;

                    bestScore = score;
                    found = true;
                    best = new OsWindow
                    {
                        handle = candidate.handle,
                        title = candidate.title,
                        windowRect = windowRect,
                        scale = scale,
                        basis = basis,
                        dpi = GetDpi(candidate.handle),
                    };
                }
            }

            // A match is only trusted when the residual is small relative to the window: a loose match
            // would capture the wrong container and look plausible.
            var tolerance = Mathf.Max(12f, containerRect.width * 0.02f);
            if (!found || bestScore > tolerance)
            {
                result.attempts.Add("os-window: no top-level window matched container " +
                                    EditorToolBridge.RectJson(containerRect) +
                                    " (best score " + bestScore.ToString("F1", CultureInfo.InvariantCulture) +
                                    ", tolerance " + tolerance.ToString("F1", CultureInfo.InvariantCulture) + "). Candidates: " +
                                    string.Join(" ; ", diagnostics.ToArray()));
                return false;
            }

            result.notes.Add("matched OS window 0x" + best.handle.ToInt64().ToString("X", CultureInfo.InvariantCulture) +
                             " via " + best.basis + " rect, residual " + bestScore.ToString("F1", CultureInfo.InvariantCulture) +
                             " px, scale " + best.scale.ToString("F3", CultureInfo.InvariantCulture) +
                             ", dpi " + best.dpi.ToString(CultureInfo.InvariantCulture));
            return true;
        }

        private static bool TryGetBasisRect(IntPtr hwnd, string basis, out NativeMethods.RECT rect)
        {
            rect = default(NativeMethods.RECT);
            switch (basis)
            {
                case "window":
                    return NativeMethods.GetWindowRect(hwnd, out rect);

                case "client":
                {
                    NativeMethods.RECT client;
                    if (!NativeMethods.GetClientRect(hwnd, out client)) return false;
                    var origin = new NativeMethods.POINT { x = client.left, y = client.top };
                    if (!NativeMethods.ClientToScreen(hwnd, ref origin)) return false;
                    rect = new NativeMethods.RECT
                    {
                        left = origin.x,
                        top = origin.y,
                        right = origin.x + client.Width,
                        bottom = origin.y + client.Height,
                    };
                    return true;
                }

                case "dwm":
                {
                    // The extended frame bounds exclude the invisible resize border Windows 10 keeps
                    // around a window, which is what makes GetWindowRect look a few pixels too large.
                    var size = Marshal.SizeOf(typeof(NativeMethods.RECT));
                    var buffer = Marshal.AllocHGlobal(size);
                    try
                    {
                        var hr = NativeMethods.DwmGetWindowAttribute(hwnd,
                            NativeMethods.DWMWA_EXTENDED_FRAME_BOUNDS, buffer, size);
                        if (hr != 0) return false;
                        rect = (NativeMethods.RECT)Marshal.PtrToStructure(buffer, typeof(NativeMethods.RECT));
                        return true;
                    }
                    catch (Exception)
                    {
                        return false;
                    }
                    finally
                    {
                        Marshal.FreeHGlobal(buffer);
                    }
                }

                default:
                    return false;
            }
        }

        private struct Candidate
        {
            public IntPtr handle;
            public string title;
        }

        private static List<Candidate> EnumerateProcessWindows()
        {
            var found = new List<Candidate>();
            var processId = NativeMethods.GetCurrentProcessId();

            NativeMethods.EnumWindows((hwnd, lParam) =>
            {
                try
                {
                    if (!NativeMethods.IsWindowVisible(hwnd)) return true;

                    uint owner;
                    NativeMethods.GetWindowThreadProcessId(hwnd, out owner);
                    if (owner != processId) return true;

                    NativeMethods.RECT rect;
                    if (!NativeMethods.GetWindowRect(hwnd, out rect)) return true;
                    if (rect.Width <= 1 || rect.Height <= 1) return true;

                    var title = new StringBuilder(256);
                    NativeMethods.GetWindowText(hwnd, title, title.Capacity);
                    found.Add(new Candidate { handle = hwnd, title = title.ToString() });
                }
                catch (Exception)
                {
                    // One bad window must not abort the enumeration.
                }

                return true;
            }, IntPtr.Zero);

            return found;
        }

        private static int GetDpi(IntPtr hwnd)
        {
            try
            {
                return (int)NativeMethods.GetDpiForWindow(hwnd);
            }
            catch (Exception)
            {
                // Pre-1607 Windows has no per-window DPI; the measured scale is used instead.
                return 0;
            }
        }
#endif

        // ------------------------------------------------------------------ legacy framebuffer path

        /// <summary>
        /// The original ReadScreenPixel route, kept as a last resort for docked windows so the command
        /// still returns something on a machine where the native path is unavailable.
        /// </summary>
        private static bool TryFramebuffer(EditorWindow window, Rect viewRect, string outputPath,
            Options options, Result result)
        {
            try
            {
                if (!result.inMainWindow)
                {
                    result.attempts.Add(BackendFramebuffer +
                                        ": the window is in a floating container, which the editor framebuffer does not contain");
                    return false;
                }

                var internalUtility = typeof(EditorWindow).Assembly.GetType("UnityEditorInternal.InternalEditorUtility");
                var readScreenPixel = internalUtility != null
                    ? internalUtility.GetMethod("ReadScreenPixel",
                        System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.Public |
                        System.Reflection.BindingFlags.NonPublic)
                    : null;

                if (readScreenPixel == null)
                {
                    result.attempts.Add(BackendFramebuffer + ": InternalEditorUtility.ReadScreenPixel is not available");
                    return false;
                }

                var mainContainer = EditorToolBridge.MainContainer();
                var mainRectValue = mainContainer != null ? EditorToolBridge.ReadMemberByName(mainContainer, "position") : null;
                if (!(mainRectValue is Rect))
                {
                    result.attempts.Add(BackendFramebuffer + ": the main container window has no readable position");
                    return false;
                }

                var mainRect = (Rect)mainRectValue;
                var scale = result.pixelsPerPoint;
                var localX = viewRect.x - mainRect.x;
                var localY = viewRect.y - mainRect.y;
                var flippedY = mainRect.height - (localY + viewRect.height);

                var origin = new Vector2(localX, flippedY) * scale;
                var width = Mathf.Max(1, Mathf.RoundToInt(viewRect.width * scale));
                var height = Mathf.Max(1, Mathf.RoundToInt(viewRect.height * scale));

                var colors = readScreenPixel.Invoke(null, new object[] { origin, width, height }) as Color[];
                if (colors == null || colors.Length == 0)
                {
                    result.attempts.Add(BackendFramebuffer + ": ReadScreenPixel returned no pixels");
                    return false;
                }

                var pixels = new Color32[colors.Length];
                for (var i = 0; i < colors.Length; i++)
                {
                    pixels[i] = colors[i];
                }

                var uniform = IsUniform(pixels);
                if (uniform && !options.allowUniform)
                {
                    result.attempts.Add(BackendFramebuffer + ": single flat colour, treated as no pixels");
                    return false;
                }

                WritePng(pixels, width, height, outputPath, result);
                result.backend = BackendFramebuffer;
                result.uniformColor = uniform;
                result.scale = scale;
                result.success = true;
                result.failureReason = null;
                return true;
            }
            catch (Exception e)
            {
                result.attempts.Add(BackendFramebuffer + ": " + e.Message);
                return false;
            }
        }

        // ------------------------------------------------------------------ shared helpers

        /// <summary>Whether a 32bpp BGRA buffer holds one single colour. Alpha is ignored: PrintWindow
        /// leaves it unset on some drivers, which would otherwise make every frame look non-uniform.</summary>
        private static bool IsUniformBgra(byte[] bgra)
        {
            if (bgra == null || bgra.Length < 8) return true;

            for (var i = 4; i + 2 < bgra.Length; i += 4)
            {
                if (bgra[i] != bgra[0] || bgra[i + 1] != bgra[1] || bgra[i + 2] != bgra[2])
                {
                    return false;
                }
            }

            return true;
        }

        private static bool IsUniform(Color32[] pixels)
        {
            if (pixels == null || pixels.Length < 2) return true;

            var first = pixels[0];
            for (var i = 1; i < pixels.Length; i++)
            {
                var p = pixels[i];
                if (p.r != first.r || p.g != first.g || p.b != first.b)
                {
                    return false;
                }
            }

            return true;
        }

        private static void WritePng(Color32[] pixels, int width, int height, string outputPath, Result result)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(outputPath) ?? ".");

            var texture = new Texture2D(width, height, TextureFormat.RGBA32, false);
            try
            {
                texture.SetPixels32(pixels);
                texture.Apply(false);
                File.WriteAllBytes(outputPath, texture.EncodeToPNG());
            }
            finally
            {
                Object.DestroyImmediate(texture);
            }

            var info = new FileInfo(outputPath);
            result.imageWidth = width;
            result.imageHeight = height;
            result.pngExists = info.Exists;
            result.pngBytes = info.Exists ? info.Length : 0L;
        }

        private static string Truncate(string text, int limit)
        {
            if (string.IsNullOrEmpty(text) || text.Length <= limit) return text ?? string.Empty;
            return text.Substring(0, limit) + "...";
        }

        /// <summary>Writes the result as the flat key/value pairs the file bridge can carry.</summary>
        internal static void WriteOutputs(Result result, CommandResponse response, string prefix)
        {
            prefix = prefix ?? string.Empty;
            response.AddOutput(prefix + "captureBackend", result.backend);
            response.AddOutput(prefix + "outputPath", result.outputPath ?? string.Empty);
            response.AddOutput(prefix + "pngExists", result.pngExists ? "true" : "false");
            response.AddOutput(prefix + "pngBytes", result.pngBytes.ToString(CultureInfo.InvariantCulture));
            response.AddOutput(prefix + "imageWidth", result.imageWidth.ToString(CultureInfo.InvariantCulture));
            response.AddOutput(prefix + "imageHeight", result.imageHeight.ToString(CultureInfo.InvariantCulture));
            response.AddOutput(prefix + "windowRect", EditorToolBridge.RectJson(result.windowRect));
            response.AddOutput(prefix + "hostRect", EditorToolBridge.RectJson(result.hostRect));
            response.AddOutput(prefix + "containerRect", EditorToolBridge.RectJson(result.containerRect));
            response.AddOutput(prefix + "captureRect", EditorToolBridge.RectJson(result.captureRect));
            response.AddOutput(prefix + "pixelsPerPoint", EditorToolBridge.F(result.pixelsPerPoint));
            response.AddOutput(prefix + "measuredScale", EditorToolBridge.F(result.scale));
            response.AddOutput(prefix + "inMainWindow", result.inMainWindow ? "true" : "false");
            response.AddOutput(prefix + "visibleView", result.visibleView ?? string.Empty);
            response.AddOutput(prefix + "uniformColor", result.uniformColor ? "true" : "false");
            response.AddOutput(prefix + "occluded", result.occluded ? "true" : "false");
            response.AddOutput(prefix + "osWindowHandle", "0x" + result.hwnd.ToString("X", CultureInfo.InvariantCulture));
            response.AddOutput(prefix + "osWindowTitle", result.hwndTitle ?? string.Empty);
            response.AddOutput(prefix + "osWindowRect", EditorToolBridge.RectJson(result.hwndRect));
            response.AddOutput(prefix + "mappingBasis", result.mappingBasis);
            response.AddOutput(prefix + "systemDpi", result.systemDpi.ToString(CultureInfo.InvariantCulture));

            if (result.popupsComposited >= 0)
            {
                // Zero is a real answer, not a missing one: it says the overlay pass ran and found
                // nothing over the window, which is different from never having asked for it.
                response.AddOutput(prefix + "popupsComposited",
                    result.popupsComposited.ToString(CultureInfo.InvariantCulture));
                response.AddOutput(prefix + "popupTitles", result.popupTitles ?? string.Empty);
            }

            if (result.notes.Count > 0)
            {
                response.AddOutput(prefix + "captureNotes", string.Join(" | ", result.notes.ToArray()));
            }

            if (result.attempts.Count > 0)
            {
                response.AddOutput(prefix + "backendAttempts", string.Join(" | ", result.attempts.ToArray()));
            }
        }

#if UNITY_EDITOR_WIN
        private static class NativeMethods
        {
            internal const int PW_RENDERFULLCONTENT = 0x00000002;
            internal const int SRCCOPY = 0x00CC0020;
            internal const int CAPTUREBLT = 0x40000000;
            internal const uint BI_RGB = 0;
            internal const uint DIB_RGB_COLORS = 0;
            internal const uint GA_ROOT = 2;
            internal const int DWMWA_EXTENDED_FRAME_BOUNDS = 9;

            internal delegate bool EnumWindowsProc(IntPtr hwnd, IntPtr lParam);

            [StructLayout(LayoutKind.Sequential)]
            internal struct RECT
            {
                public int left;
                public int top;
                public int right;
                public int bottom;

                public int Width { get { return right - left; } }
                public int Height { get { return bottom - top; } }
            }

            [StructLayout(LayoutKind.Sequential)]
            internal struct POINT
            {
                public int x;
                public int y;
            }

            [StructLayout(LayoutKind.Sequential)]
            internal struct BITMAPINFOHEADER
            {
                public uint biSize;
                public int biWidth;
                public int biHeight;
                public ushort biPlanes;
                public ushort biBitCount;
                public uint biCompression;
                public uint biSizeImage;
                public int biXPelsPerMeter;
                public int biYPelsPerMeter;
                public uint biClrUsed;
                public uint biClrImportant;
            }

            [StructLayout(LayoutKind.Sequential)]
            internal struct BITMAPINFO
            {
                public BITMAPINFOHEADER bmiHeader;
                [MarshalAs(UnmanagedType.ByValArray, SizeConst = 4)]
                public uint[] bmiColors;
            }

            [DllImport("user32.dll", SetLastError = true)]
            internal static extern bool EnumWindows(EnumWindowsProc callback, IntPtr lParam);

            [DllImport("user32.dll", SetLastError = true)]
            internal static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint processId);

            [DllImport("user32.dll", SetLastError = true)]
            internal static extern bool IsWindowVisible(IntPtr hwnd);

            [DllImport("user32.dll", SetLastError = true)]
            internal static extern bool IsIconic(IntPtr hwnd);

            [DllImport("user32.dll", SetLastError = true)]
            internal static extern bool GetWindowRect(IntPtr hwnd, out RECT rect);

            [DllImport("user32.dll", SetLastError = true)]
            internal static extern bool GetClientRect(IntPtr hwnd, out RECT rect);

            [DllImport("user32.dll", SetLastError = true)]
            internal static extern bool ClientToScreen(IntPtr hwnd, ref POINT point);

            [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode, EntryPoint = "GetWindowTextW")]
            internal static extern int GetWindowText(IntPtr hwnd, StringBuilder text, int count);

            [DllImport("user32.dll", SetLastError = true)]
            internal static extern bool PrintWindow(IntPtr hwnd, IntPtr hdc, int flags);

            [DllImport("user32.dll", SetLastError = true)]
            internal static extern IntPtr GetDC(IntPtr hwnd);

            [DllImport("user32.dll", SetLastError = true)]
            internal static extern int ReleaseDC(IntPtr hwnd, IntPtr hdc);

            [DllImport("user32.dll", SetLastError = true)]
            internal static extern IntPtr WindowFromPoint(POINT point);

            [DllImport("user32.dll", SetLastError = true)]
            internal static extern IntPtr GetAncestor(IntPtr hwnd, uint flags);

            [DllImport("user32.dll", SetLastError = true)]
            internal static extern uint GetDpiForWindow(IntPtr hwnd);

            [DllImport("dwmapi.dll", SetLastError = false)]
            internal static extern int DwmGetWindowAttribute(IntPtr hwnd, int attribute, IntPtr value, int size);

            [DllImport("gdi32.dll", SetLastError = true)]
            internal static extern IntPtr CreateCompatibleDC(IntPtr hdc);

            [DllImport("gdi32.dll", SetLastError = true)]
            internal static extern IntPtr CreateCompatibleBitmap(IntPtr hdc, int width, int height);

            [DllImport("gdi32.dll", SetLastError = true)]
            internal static extern IntPtr SelectObject(IntPtr hdc, IntPtr handle);

            [DllImport("gdi32.dll", SetLastError = true)]
            internal static extern bool BitBlt(IntPtr destDc, int destX, int destY, int width, int height,
                IntPtr sourceDc, int sourceX, int sourceY, int rop);

            [DllImport("gdi32.dll", SetLastError = true)]
            internal static extern bool DeleteDC(IntPtr hdc);

            [DllImport("gdi32.dll", SetLastError = true)]
            internal static extern bool DeleteObject(IntPtr handle);

            [DllImport("gdi32.dll", SetLastError = true)]
            internal static extern int GetDIBits(IntPtr hdc, IntPtr bitmap, uint startScan, uint scanLines,
                byte[] bits, ref BITMAPINFO info, uint usage);

            [DllImport("kernel32.dll")]
            internal static extern uint GetCurrentProcessId();
        }
#endif
    }
}
