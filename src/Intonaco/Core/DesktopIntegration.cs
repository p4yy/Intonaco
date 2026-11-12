using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Interop;

namespace Intonaco.Core
{
    /// <summary>
    /// Integrates a WPF window into the Windows desktop wallpaper layer so it
    /// renders behind the desktop icons (SHELLDLL_DefView) but above the
    /// static wallpaper. Explorer builds that layer in two different ways:
    ///
    /// Classic (Windows 10 / Windows 11 up to 23H2): Progman is asked (via the
    /// undocumented 0x052C message) to split the desktop; the icons move into
    /// a top-level WorkerW and a second top-level WorkerW behind it becomes
    /// the wallpaper layer. The player is parented into that WorkerW.
    ///
    /// Raised (Windows 11 24H2+, incl. 26H2 Insider builds): no extra
    /// top-level windows are created. Progman carries
    /// WS_EX_NOREDIRECTIONBITMAP (it has no redirection surface of its own),
    /// the icon view is a WS_EX_LAYERED child of Progman, and Explorer's
    /// wallpaper is drawn by a WorkerW child of Progman below it. A plain
    /// child window parented into Progman/WorkerW is never composited by
    /// DWM there — it exists and "plays" but is invisible. Per Microsoft's
    /// guidance to wallpaper apps (via the Lively project), the player must
    /// be its own WS_EX_LAYERED child of Progman with constant alpha 0xFF,
    /// z-ordered directly under SHELLDLL_DefView and above the system
    /// WorkerW: [SHELLDLL_DefView, player, WorkerW].
    ///
    /// Diagnostics from a 26H2 machine exposed two extra requirements: a
    /// raised Progman/DefView pair is not by itself a completed split (an
    /// actual WorkerW layer must be requested), and WPF's HwndTarget
    /// actively clears WS_EX_LAYERED unless the style change is guarded.
    ///
    /// The layout is detected from the actual window tree and Progman's
    /// extended style — not from the OS version — and every attach attempt
    /// is logged to %LocalAppData%\Intonaco\debug.log.
    /// </summary>
    public sealed class DesktopIntegration : IDisposable
    {
        #region Native Methods

        private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

        [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern IntPtr FindWindow(string? lpClassName, string? lpWindowName);

        [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern IntPtr FindWindowEx(IntPtr hwndParent, IntPtr hwndChildAfter,
            string? lpszClass, string? lpszWindow);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern IntPtr GetWindow(IntPtr hWnd, uint uCmd);

        [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern int GetClassName(IntPtr hWnd, StringBuilder lpClassName, int nMaxCount);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern IntPtr SendMessageTimeout(IntPtr hWnd, uint msg,
            IntPtr wParam, IntPtr lParam, uint fuFlags, uint uTimeout, out IntPtr lpdwResult);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern IntPtr SetParent(IntPtr hWndChild, IntPtr hWndNewParent);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern IntPtr GetParent(IntPtr hWnd);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter,
            int x, int y, int cx, int cy, uint uFlags);

        [DllImport("user32.dll", SetLastError = true, EntryPoint = "SetWindowLongPtrW")]
        private static extern IntPtr SetWindowLongPtr(IntPtr hWnd, int nIndex, IntPtr dwNewLong);

        [DllImport("user32.dll", SetLastError = true, EntryPoint = "GetWindowLongPtrW")]
        private static extern IntPtr GetWindowLongPtr(IntPtr hWnd, int nIndex);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool SetLayeredWindowAttributes(IntPtr hWnd, uint crKey, byte bAlpha, uint dwFlags);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool GetLayeredWindowAttributes(IntPtr hWnd, out uint pcrKey,
            out byte pbAlpha, out uint pdwFlags);

        [DllImport("user32.dll")]
        private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

        [DllImport("user32.dll")]
        private static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);

        [DllImport("user32.dll")]
        private static extern bool IsWindow(IntPtr hWnd);

        [DllImport("user32.dll")]
        private static extern bool IsWindowVisible(IntPtr hWnd);

        [DllImport("user32.dll")]
        private static extern int GetSystemMetrics(int nIndex);

        [StructLayout(LayoutKind.Sequential)]
        private struct RECT
        {
            public int Left, Top, Right, Bottom;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct STYLESTRUCT
        {
            public uint styleOld;
            public uint styleNew;
        }

        private const uint SMTO_NORMAL = 0x0000;
        private const int WM_STYLECHANGING = 0x007C;
        private const int GWL_STYLE = -16;
        private const int GWL_EXSTYLE = -20;
        private const long WS_POPUP = unchecked((long)0x80000000);
        private const long WS_CHILD = 0x40000000;
        private const long WS_VISIBLE = 0x10000000;
        private const long WS_CAPTION = 0x00C00000;
        private const long WS_SYSMENU = 0x00080000;
        private const long WS_THICKFRAME = 0x00040000;
        private const long WS_EX_NOACTIVATE = 0x08000000;
        private const long WS_EX_TOOLWINDOW = 0x00000080;
        private const long WS_EX_LAYERED = 0x00080000;
        private const long WS_EX_NOREDIRECTIONBITMAP = 0x00200000;
        private const uint LWA_ALPHA = 0x00000002;
        private const uint SWP_NOSIZE = 0x0001;
        private const uint SWP_NOMOVE = 0x0002;
        private const uint SWP_NOZORDER = 0x0004;
        private const uint SWP_NOACTIVATE = 0x0010;
        private const uint SWP_FRAMECHANGED = 0x0020;
        private const uint SWP_SHOWWINDOW = 0x0040;
        private const int SW_SHOW = 5;
        private const uint GW_HWNDNEXT = 2;
        private const uint GW_CHILD = 5;
        private const int SM_CXSCREEN = 0;
        private const int SM_CYSCREEN = 1;
        private static readonly IntPtr HWND_BOTTOM = new IntPtr(1);

        #endregion

        #region Layout classification & candidate ordering (pure logic, unit-tested)

        internal enum DesktopLayout
        {
            Unknown,
            ClassicWorkerW,
            RaisedProgman
        }

        /// <summary>
        /// Classifies Explorer's desktop layout from observable window-tree
        /// facts. Raised wins over classic: on a raised desktop a stale
        /// top-level WorkerW can still exist, but parenting there is invisible.
        /// </summary>
        internal static DesktopLayout ClassifyLayout(
            bool progmanHasNoRedirectionBitmap,
            bool defViewIsProgmanChild,
            bool progmanHasFullScreenWorkerWChild,
            bool classicWorkerWFound)
        {
            if (progmanHasNoRedirectionBitmap)
                return DesktopLayout.RaisedProgman;
            if (defViewIsProgmanChild && progmanHasFullScreenWorkerWChild)
                return DesktopLayout.RaisedProgman;
            if (classicWorkerWFound)
                return DesktopLayout.ClassicWorkerW;
            return DesktopLayout.Unknown;
        }

        /// <summary>
        /// A raised Progman with SHELLDLL_DefView is only *capable* of the
        /// raised layout; Explorer has not split the desktop until an actual
        /// full-screen WorkerW wallpaper layer exists. Treating the raised
        /// styles alone as proof of a split was the v1.0.2 failure on build
        /// 26300.9711: no 0x052C request was sent, no WorkerW was created,
        /// and DefView continued painting the static wallpaper over us.
        /// </summary>
        internal static bool HasWallpaperLayer(
            bool classicWorkerWFound,
            bool progmanHasFullScreenWorkerWChild)
            => classicWorkerWFound || progmanHasFullScreenWorkerWChild;

        /// <summary>
        /// WPF's HwndTarget deliberately clears WS_EX_LAYERED from every
        /// WM_STYLECHANGING notification while UsesPerPixelOpacity is false.
        /// The desktop style guard reapplies this bit before the change is
        /// committed, while keeping WPF's normal (non-per-pixel) renderer.
        /// </summary>
        internal static uint ForceLayeredExStyle(uint styleNew, bool keepLayered)
            => keepLayered ? styleNew | (uint)WS_EX_LAYERED : styleNew;

        internal enum WorkerWStrategy
        {
            ShellHostNextSibling = 0,
            ProgmanChild = 1,
            ProgmanNextSibling = 2,
            AnyWorkerW = 3,
            ProgmanFallback = 4
        }

        internal readonly struct WorkerWCandidate
        {
            public WorkerWCandidate(IntPtr handle, WorkerWStrategy strategy, long area, bool isVisible)
            {
                Handle = handle;
                Strategy = strategy;
                Area = area;
                IsVisible = isVisible;
            }

            public IntPtr Handle { get; }
            public WorkerWStrategy Strategy { get; }
            public long Area { get; }
            public bool IsVisible { get; }
        }

        /// <summary>
        /// De-duplicates fallback candidates (keeping each handle's best
        /// strategy) and orders them by strategy priority, then visibility,
        /// then size.
        /// </summary>
        internal static IReadOnlyList<IntPtr> OrderCandidates(IEnumerable<WorkerWCandidate> candidates)
        {
            return candidates
                .Where(c => c.Handle != IntPtr.Zero)
                .GroupBy(c => c.Handle)
                .Select(g => g.OrderBy(c => (int)c.Strategy).First())
                .OrderBy(c => (int)c.Strategy)
                .ThenByDescending(c => c.IsVisible)
                .ThenByDescending(c => c.Area)
                .Select(c => c.Handle)
                .ToList();
        }

        #endregion

        /// <summary>Snapshot of the Explorer window facts attach decisions use.</summary>
        private sealed class DesktopSnapshot
        {
            public IntPtr Progman;
            public IntPtr DefView;
            public IntPtr ProgmanDefView;
            public IntPtr ClassicWorkerW;
            public IntPtr ProgmanWorkerChild;
            public IntPtr AnyProgmanWorkerChild;
            public bool IsRaisedByExStyle;
            public List<(IntPtr Handle, string ClassName)> TopLevels = new List<(IntPtr, string)>();

            public DesktopLayout Layout => ClassifyLayout(
                IsRaisedByExStyle,
                ProgmanDefView != IntPtr.Zero,
                ProgmanWorkerChild != IntPtr.Zero,
                ClassicWorkerW != IntPtr.Zero);

            public bool HasDesktopSplit => HasWallpaperLayer(
                ClassicWorkerW != IntPtr.Zero,
                ProgmanWorkerChild != IntPtr.Zero);
        }

        // _parentHandle is the window the player is actually parented to
        // (a WorkerW in classic mode, Progman itself in raised mode) and the
        // rect the player is sized to fill.
        private IntPtr _parentHandle = IntPtr.Zero;
        private IntPtr _playerHandle = IntPtr.Zero;
        private IntPtr _shellDefView = IntPtr.Zero;
        private IntPtr _systemWorkerW = IntPtr.Zero;
        private Window? _playerWindow;
        private HwndSource? _playerHwndSource;
        private HwndSourceHook? _layeredStyleHook;
        private bool _keepLayeredStyle;
        private bool _disposed;
        private bool _isAttached;
        private bool _isRaisedDesktop;

        public bool IsAttached => _isAttached && _parentHandle != IntPtr.Zero;

        /// <summary>Human-readable reason for the last failed Attach, if any.</summary>
        public string? LastError { get; private set; }

        private bool IsProgmanParent =>
            _parentHandle != IntPtr.Zero && GetClassNameString(_parentHandle) == "Progman";

        #region Window-tree inspection

        private static string GetClassNameString(IntPtr hWnd)
        {
            if (hWnd == IntPtr.Zero) return "<null>";
            var sb = new StringBuilder(256);
            GetClassName(hWnd, sb, sb.Capacity);
            return sb.ToString();
        }

        private static long GetWindowArea(IntPtr hWnd)
        {
            return hWnd != IntPtr.Zero && GetWindowRect(hWnd, out RECT rect)
                ? (long)(rect.Right - rect.Left) * (rect.Bottom - rect.Top)
                : 0;
        }

        private static List<IntPtr> GetDirectChildren(IntPtr parent)
        {
            var children = new List<IntPtr>();
            if (parent == IntPtr.Zero) return children;
            IntPtr child = GetWindow(parent, GW_CHILD);
            while (child != IntPtr.Zero && children.Count < 64)
            {
                children.Add(child);
                child = GetWindow(child, GW_HWNDNEXT);
            }
            return children;
        }

        private DesktopSnapshot CaptureDesktopState()
        {
            var snapshot = new DesktopSnapshot();
            snapshot.Progman = FindWindow("Progman", null);
            if (snapshot.Progman == IntPtr.Zero)
                return snapshot;

            snapshot.IsRaisedByExStyle =
                (GetWindowLongPtr(snapshot.Progman, GWL_EXSTYLE).ToInt64() & WS_EX_NOREDIRECTIONBITMAP) != 0;

            var topLevels = new List<(IntPtr, string)>();
            EnumWindowsProc callback = (hWnd, _) =>
            {
                topLevels.Add((hWnd, GetClassNameString(hWnd)));
                return true;
            };
            EnumWindows(callback, IntPtr.Zero);
            GC.KeepAlive(callback);
            snapshot.TopLevels = topLevels;

            long primaryArea = (long)GetSystemMetrics(SM_CXSCREEN) * GetSystemMetrics(SM_CYSCREEN);

            // Classic arrangement: a top-level host contains SHELLDLL_DefView
            // and the wallpaper WorkerW is the next top-level WorkerW after it.
            // Count it as a wallpaper layer only when it actually covers the
            // display; Explorer owns many tiny WorkerW helper windows.
            foreach (var (handle, _) in topLevels)
            {
                IntPtr defView = FindWindowEx(handle, IntPtr.Zero, "SHELLDLL_DefView", null);
                if (defView != IntPtr.Zero)
                {
                    snapshot.DefView = defView;
                    IntPtr classicWorker = FindWindowEx(IntPtr.Zero, handle, "WorkerW", null);
                    if (classicWorker != IntPtr.Zero && primaryArea > 0 &&
                        GetWindowArea(classicWorker) >= primaryArea * 9 / 10)
                    {
                        snapshot.ClassicWorkerW = classicWorker;
                    }
                }
            }

            snapshot.ProgmanDefView = FindWindowEx(snapshot.Progman, IntPtr.Zero, "SHELLDLL_DefView", null);
            if (snapshot.ProgmanDefView != IntPtr.Zero)
                snapshot.DefView = snapshot.ProgmanDefView;

            // Direct WorkerW children of Progman. Explorer keeps several tiny
            // ones (tray/input helpers); the wallpaper layer is the one that
            // covers the primary display.
            IntPtr child = IntPtr.Zero;
            IntPtr largestChild = IntPtr.Zero;
            long largestArea = -1;
            while ((child = FindWindowEx(snapshot.Progman, child, "WorkerW", null)) != IntPtr.Zero)
            {
                long area = GetWindowArea(child);
                if (area > largestArea)
                {
                    largestArea = area;
                    largestChild = child;
                }
            }
            snapshot.AnyProgmanWorkerChild = largestChild;
            if (primaryArea > 0 && largestArea >= primaryArea * 9 / 10)
                snapshot.ProgmanWorkerChild = largestChild;

            return snapshot;
        }

        /// <summary>
        /// Asks Explorer to create the wallpaper layer when the window tree
        /// shows no actual WorkerW layer yet. This includes a raised desktop
        /// whose DefView is already under Progman: that arrangement alone is
        /// not a completed split. The current request is a single
        /// 0x052C (0xD, 1); variants with lParam 0 can release/undo the layer,
        /// so a legacy (0, 0) request is used only on non-raised layouts and
        /// only after (0xD, 1) demonstrably made nothing.
        /// </summary>
        private DesktopSnapshot EnsureDesktopSplit(DesktopSnapshot snapshot)
        {
            if (snapshot.Progman == IntPtr.Zero || snapshot.HasDesktopSplit)
                return snapshot;

            DebugLog.Write(
                "No actual WorkerW wallpaper layer in window tree " +
                $"(Layout={snapshot.Layout}); sending 0x052C (0xD, 1) to Progman.");
            SendDesktopSplitRequest(snapshot.Progman, new IntPtr(0xD), new IntPtr(1), "(0xD, 1)");
            snapshot = WaitForWallpaperLayer(2000);
            if (snapshot.HasDesktopSplit)
                return snapshot;

            if (snapshot.Layout == DesktopLayout.RaisedProgman)
            {
                DebugLog.Write(
                    "Raised desktop still has no full-screen WorkerW after 0x052C (0xD, 1); " +
                    "proceeding with the verified layered-Progman placement and " +
                    "not sending an lParam=0 variant that can release the split.");
                return snapshot;
            }

            DebugLog.Write("Still no wallpaper layer; trying legacy 0x052C (0, 0) once.");
            SendDesktopSplitRequest(snapshot.Progman, IntPtr.Zero, IntPtr.Zero, "(0, 0)");
            return WaitForWallpaperLayer(1000);
        }

        private DesktopSnapshot WaitForWallpaperLayer(int timeoutMs)
        {
            const int pollMs = 50;
            int elapsedMs = 0;
            DesktopSnapshot snapshot = CaptureDesktopState();

            while (!snapshot.HasDesktopSplit && elapsedMs < timeoutMs)
            {
                Thread.Sleep(pollMs);
                elapsedMs += pollMs;
                snapshot = CaptureDesktopState();
            }

            if (snapshot.HasDesktopSplit)
            {
                DebugLog.Write(
                    $"WorkerW wallpaper layer appeared after ~{elapsedMs} ms: " +
                    $"ClassicWorkerW=0x{snapshot.ClassicWorkerW.ToInt64():X} " +
                    $"ProgmanWorkerChild=0x{snapshot.ProgmanWorkerChild.ToInt64():X}.");
            }

            return snapshot;
        }

        private static void SendDesktopSplitRequest(
            IntPtr progman, IntPtr wParam, IntPtr lParam, string label)
        {
            IntPtr callResult = SendMessageTimeout(
                progman, 0x052C, wParam, lParam, SMTO_NORMAL, 1000, out IntPtr messageResult);
            int win32Error = callResult == IntPtr.Zero ? Marshal.GetLastWin32Error() : 0;
            DebugLog.Write(
                $"0x052C {label}: SendMessageTimeout returned 0x{callResult.ToInt64():X}, " +
                $"message result 0x{messageResult.ToInt64():X}, win32 error = {win32Error}.");
        }

        private static void LogSnapshot(DesktopSnapshot snapshot)
        {
            DebugLog.Write(
                $"Desktop state: OS={Environment.OSVersion.Version} Layout={snapshot.Layout} " +
                $"HasWallpaperLayer={snapshot.HasDesktopSplit} " +
                $"Progman=0x{snapshot.Progman.ToInt64():X} NoRedirectionBitmap={snapshot.IsRaisedByExStyle} " +
                $"DefView=0x{snapshot.DefView.ToInt64():X} ProgmanDefView=0x{snapshot.ProgmanDefView.ToInt64():X} " +
                $"ClassicWorkerW=0x{snapshot.ClassicWorkerW.ToInt64():X} " +
                $"ProgmanWorkerChild=0x{snapshot.ProgmanWorkerChild.ToInt64():X} " +
                $"AnyProgmanWorkerChild=0x{snapshot.AnyProgmanWorkerChild.ToInt64():X}");

            foreach (var (handle, className) in snapshot.TopLevels)
            {
                if (className is not ("Progman" or "WorkerW"))
                    continue;
                IntPtr shellView = FindWindowEx(handle, IntPtr.Zero, "SHELLDLL_DefView", null);
                GetWindowRect(handle, out RECT rect);
                DebugLog.Write(
                    $"  top-level 0x{handle.ToInt64():X} {className} " +
                    $"shellView={(shellView != IntPtr.Zero)} visible={IsWindowVisible(handle)} " +
                    $"rect=({rect.Left},{rect.Top})-({rect.Right},{rect.Bottom})");
            }

            LogChildren(snapshot.Progman, "Progman children (top to bottom)");
        }

        private static void LogChildren(IntPtr parent, string label)
        {
            if (parent == IntPtr.Zero) return;
            var children = GetDirectChildren(parent);
            DebugLog.Write($"  {label} of 0x{parent.ToInt64():X}:");
            for (int i = 0; i < children.Count; i++)
            {
                GetWindowRect(children[i], out RECT rect);
                long exStyle = GetWindowLongPtr(children[i], GWL_EXSTYLE).ToInt64();
                DebugLog.Write(
                    $"    [{i}] 0x{children[i].ToInt64():X} {GetClassNameString(children[i])} " +
                    $"visible={IsWindowVisible(children[i])} exStyle=0x{exStyle:X} " +
                    $"rect=({rect.Left},{rect.Top})-({rect.Right},{rect.Bottom})");
            }
        }

        private static WorkerWCandidate MakeCandidate(IntPtr handle, WorkerWStrategy strategy)
        {
            return new WorkerWCandidate(handle, strategy, GetWindowArea(handle),
                handle != IntPtr.Zero && IsWindowVisible(handle));
        }

        /// <summary>Ordered WorkerW-parent fallbacks (classic mode / last resort).</summary>
        private IReadOnlyList<IntPtr> FindWorkerWCandidates(DesktopSnapshot snapshot)
        {
            var candidates = new List<WorkerWCandidate>();

            foreach (var (handle, _) in snapshot.TopLevels)
            {
                if (FindWindowEx(handle, IntPtr.Zero, "SHELLDLL_DefView", null) != IntPtr.Zero)
                {
                    IntPtr next = FindWindowEx(IntPtr.Zero, handle, "WorkerW", null);
                    if (next != IntPtr.Zero)
                        candidates.Add(MakeCandidate(next, WorkerWStrategy.ShellHostNextSibling));
                }
            }

            if (snapshot.AnyProgmanWorkerChild != IntPtr.Zero)
                candidates.Add(MakeCandidate(snapshot.AnyProgmanWorkerChild, WorkerWStrategy.ProgmanChild));

            IntPtr afterProgman = FindWindowEx(IntPtr.Zero, snapshot.Progman, "WorkerW", null);
            if (afterProgman != IntPtr.Zero)
                candidates.Add(MakeCandidate(afterProgman, WorkerWStrategy.ProgmanNextSibling));

            foreach (var (handle, className) in snapshot.TopLevels)
            {
                if (className == "WorkerW" &&
                    FindWindowEx(handle, IntPtr.Zero, "SHELLDLL_DefView", null) == IntPtr.Zero)
                {
                    candidates.Add(MakeCandidate(handle, WorkerWStrategy.AnyWorkerW));
                }
            }

            candidates.Add(MakeCandidate(snapshot.Progman, WorkerWStrategy.ProgmanFallback));

            DebugLog.Write(
                "WorkerW-parent candidates: " +
                $"[{string.Join(", ", candidates.Select(c => $"{c.Strategy}=0x{c.Handle.ToInt64():X}"))}]");
            return OrderCandidates(candidates);
        }

        #endregion

        /// <summary>
        /// Explorer's 0x052C split can take several seconds after login.
        /// Run pure shell-window discovery and WorkerW preparation on a
        /// background thread, so the WPF control window remains responsive.
        /// Only Attach(Window) touches the actual WPF wallpaper surface.
        /// </summary>
        // Only one caller may request Explorer's undocumented desktop split at
        // a time. Overlapping startup/selection/recovery requests can race and
        // repeatedly rearrange Explorer windows.
        private static readonly SemaphoreSlim SplitPreparationGate = new(1, 1);

        public Task PrepareDesktopLayerAsync(CancellationToken cancellationToken)
        {
            return Task.Run(async () =>
            {
                await SplitPreparationGate.WaitAsync(cancellationToken)
                    .ConfigureAwait(false);
                try
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (_disposed)
                        return;

                    DesktopSnapshot snapshot = CaptureDesktopState();
                    if (snapshot.Progman != IntPtr.Zero)
                        EnsureDesktopSplit(snapshot);
                    cancellationToken.ThrowIfCancellationRequested();
                }
                finally
                {
                    SplitPreparationGate.Release();
                }
            }, cancellationToken);
        }

        #region Attach

        /// <summary>
        /// Attaches the given WPF window to the desktop wallpaper layer,
        /// using the layout-appropriate placement.
        /// </summary>
        public bool Attach(Window playerWindow)
        {
            if (_disposed) return false;

            try
            {
                return AttachCore(playerWindow);
            }
            catch (Exception ex)
            {
                // e.g. the WPF window's handle was destroyed together with the
                // old desktop windows after an Explorer restart. Callers
                // re-apply the wallpaper with a fresh player window.
                LastError = $"Unexpected error attaching to desktop: {ex.Message}";
                DebugLog.Write($"Attach threw: {ex}");
                Detach();
                return false;
            }
        }

        private bool AttachCore(Window playerWindow)
        {
            LastError = null;
            Detach();

            // No shell split requests or multi-second WorkerW polling on the
            // WPF dispatcher. The caller prepares Explorer asynchronously.
            // A failed preparation must fail gracefully rather than hang UI.
            var snapshot = CaptureDesktopState();
            if (snapshot.Progman == IntPtr.Zero)
            {
                LastError = "Desktop window (Progman) not found. Is Explorer running?";
                DebugLog.Write("Attach: Progman not found.");
                return false;
            }

            if (!snapshot.HasDesktopSplit)
            {
                LastError = "Windows has not prepared its wallpaper layer. Try again after Explorer is ready.";
                DebugLog.Write("Attach skipped: no WorkerW wallpaper layer; no blocking shell setup on the UI thread.");
                return false;
            }
            LogSnapshot(snapshot);
            bool raised = snapshot.Layout == DesktopLayout.RaisedProgman;

            _playerWindow = playerWindow;
            _shellDefView = snapshot.DefView;
            _systemWorkerW = snapshot.ProgmanWorkerChild != IntPtr.Zero
                ? snapshot.ProgmanWorkerChild
                : snapshot.AnyProgmanWorkerChild;
            _isRaisedDesktop = raised;

            var helper = new WindowInteropHelper(playerWindow);
            helper.EnsureHandle();
            _playerHandle = helper.Handle;
            if (_playerHandle == IntPtr.Zero)
            {
                LastError = "Could not create the player window.";
                DebugLog.Write("Attach: player HWND is null.");
                return false;
            }

            // Convert the overlapped/popup WPF window into a child window.
            // WS_POPUP and WS_CHILD are mutually exclusive — WS_POPUP must go.
            long style = GetWindowLongPtr(_playerHandle, GWL_STYLE).ToInt64();
            style = (style & ~WS_POPUP & ~WS_CAPTION & ~WS_SYSMENU & ~WS_THICKFRAME)
                    | WS_CHILD | WS_VISIBLE;
            SetWindowLongPtr(_playerHandle, GWL_STYLE, new IntPtr(style));

            long exStyle = GetWindowLongPtr(_playerHandle, GWL_EXSTYLE).ToInt64();
            exStyle |= WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW;
            SetWindowLongPtr(_playerHandle, GWL_EXSTYLE, new IntPtr(exStyle));
            ApplyFrameChanged();

            if (raised)
            {
                // WS_EX_LAYERED must be in place BEFORE SetParent (some
                // renderers ignore it afterwards), with constant alpha so
                // DirectX/WPF presents stay hardware accelerated. WPF would
                // otherwise remove the bit in its WM_STYLECHANGING handler;
                // SetLayeredOpaque installs a guard before changing it.
                if (!SetLayeredOpaque(true))
                {
                    DebugLog.Write(
                        "Could not verify WS_EX_LAYERED + alpha 255 before SetParent; " +
                        "the raised placement will retry after parenting and must " +
                        "verify it before it can report success.");
                }
                if (TryAttachRaised(snapshot))
                    return true;

                DebugLog.Write("Raised-Progman attach failed; falling back to WorkerW parenting.");
                SetLayeredOpaque(false);
                snapshot = CaptureDesktopState();
                _shellDefView = snapshot.DefView;
                _systemWorkerW = snapshot.ProgmanWorkerChild != IntPtr.Zero
                    ? snapshot.ProgmanWorkerChild
                    : snapshot.AnyProgmanWorkerChild;
            }

            return TryAttachToCandidates(snapshot, raised);
        }

        /// <summary>
        /// Raised-desktop placement: layered child of Progman, z-ordered
        /// directly under SHELLDLL_DefView and above the system WorkerW.
        /// </summary>
        private bool TryAttachRaised(DesktopSnapshot snapshot)
        {
            SetParent(_playerHandle, snapshot.Progman);
            int win32Error = Marshal.GetLastWin32Error();
            IntPtr actualParent = GetParent(_playerHandle);
            DebugLog.Write(
                $"Raised attach: SetParent(player -> Progman 0x{snapshot.Progman.ToInt64():X}) " +
                $"actual parent = 0x{actualParent.ToInt64():X}, win32 error = {win32Error}");

            if (actualParent != snapshot.Progman)
            {
                LastError = $"Windows refused the desktop layer (Win32 error {win32Error}).";
                ResetAttachmentState(keepPlayerHandle: true);
                return false;
            }

            _parentHandle = snapshot.Progman;
            EnsureSystemWorkerWBelowPlayer();
            ResizeToDesktop();
            ShowWindow(_playerHandle, SW_SHOW);
            // A final no-op when the pre-parent call stuck; otherwise this
            // restores the required layered state after SetParent/SetWindowPos.
            SetLayeredOpaque(true);

            if (!VerifyPlayerPlacement())
            {
                DebugLog.Write("Raised attach: placement check failed; repairing once.");
                EnsureSystemWorkerWBelowPlayer();
                ResizeToDesktop();
                ShowWindow(_playerHandle, SW_SHOW);
                SetLayeredOpaque(true);
            }

            bool placed = VerifyPlayerPlacement();
            LogPlayerState("Raised attach final state");
            LogChildren(snapshot.Progman, "Progman children after raised attach (top to bottom)");

            if (!placed)
            {
                LastError = "The wallpaper window could not be placed visibly on the desktop layer.";
                SetParent(_playerHandle, IntPtr.Zero);
                ResetAttachmentState(keepPlayerHandle: true);
                return false;
            }

            _isAttached = true;
            DebugLog.Write("Raised attach succeeded.");
            return true;
        }

        private bool TryAttachToCandidates(DesktopSnapshot snapshot, bool skipProgman)
        {
            var candidates = FindWorkerWCandidates(snapshot);
            int lastWin32Error = 0;

            for (int i = 0; i < candidates.Count; i++)
            {
                IntPtr candidate = candidates[i];
                if (!IsWindow(candidate))
                    continue;
                if (skipProgman && candidate == snapshot.Progman)
                {
                    // On a raised desktop a non-layered Progman child at the
                    // bottom is exactly the invisible v1.0.1 placement; the
                    // layered sandwich above is the only Progman attempt.
                    continue;
                }

                SetParent(_playerHandle, candidate);
                lastWin32Error = Marshal.GetLastWin32Error();
                IntPtr actualParent = GetParent(_playerHandle);
                DebugLog.Write(
                    $"Attach candidate #{i + 1} 0x{candidate.ToInt64():X} ({GetClassNameString(candidate)}): " +
                    $"parent after SetParent = 0x{actualParent.ToInt64():X}, win32 error = {lastWin32Error}");

                if (actualParent != candidate)
                    continue;

                _parentHandle = candidate;
                _isRaisedDesktop = false;
                ResizeToDesktop();
                ShowWindow(_playerHandle, SW_SHOW);

                if (!VerifyPlayerPlacement())
                {
                    DebugLog.Write($"Attach candidate #{i + 1}: placement check failed, trying next.");
                    SetParent(_playerHandle, IntPtr.Zero);
                    ResetAttachmentState(keepPlayerHandle: true);
                    continue;
                }

                LogPlayerState($"Attach succeeded with candidate #{i + 1}");
                _isAttached = true;
                return true;
            }

            LastError =
                $"Windows refused to place the wallpaper on the desktop layer (Win32 error {lastWin32Error}).";
            DebugLog.Write("Attach failed: no placement verified.");
            return false;
        }

        /// <summary>
        /// Parented-and-playing is not enough: the player HWND must be
        /// visible and actually cover its parent's rect. On a raised desktop
        /// it must additionally still be an opaque WS_EX_LAYERED window,
        /// sit directly under SHELLDLL_DefView, and not have Explorer's
        /// full-screen WorkerW above it. (v1.0.2 reported success with the
        /// layered bit missing, so its correct parent/rect were invisible.)
        /// </summary>
        private bool VerifyPlayerPlacement()
        {
            if (_playerHandle == IntPtr.Zero || _parentHandle == IntPtr.Zero)
                return false;
            if (!IsWindow(_playerHandle) || !IsWindowVisible(_playerHandle))
                return false;
            if (GetParent(_playerHandle) != _parentHandle)
                return false;
            if (!GetWindowRect(_parentHandle, out RECT parentRect) ||
                !GetWindowRect(_playerHandle, out RECT playerRect))
                return false;

            long parentArea = (long)(parentRect.Right - parentRect.Left) * (parentRect.Bottom - parentRect.Top);
            long playerArea = (long)(playerRect.Right - playerRect.Left) * (playerRect.Bottom - playerRect.Top);
            if (parentArea <= 0 || playerArea < parentArea * 9 / 10)
                return false;

            if (_isRaisedDesktop && IsProgmanParent)
            {
                if (!HasOpaqueLayeredStyle())
                    return false;

                var children = GetDirectChildren(_parentHandle);
                int playerIndex = children.IndexOf(_playerHandle);
                if (playerIndex < 0)
                    return false;

                if (_shellDefView != IntPtr.Zero && IsWindow(_shellDefView))
                {
                    int defViewIndex = children.IndexOf(_shellDefView);
                    if (defViewIndex < 0 || playerIndex != defViewIndex + 1)
                        return false;
                }

                if (_systemWorkerW != IntPtr.Zero && IsWindow(_systemWorkerW))
                {
                    int workerIndex = children.IndexOf(_systemWorkerW);
                    if (workerIndex >= 0 && workerIndex < playerIndex &&
                        GetWindowArea(_systemWorkerW) >= parentArea * 9 / 10)
                    {
                        return false;
                    }
                }
            }

            return true;
        }

        private void LogPlayerState(string label)
        {
            if (_playerHandle == IntPtr.Zero) return;
            GetWindowRect(_playerHandle, out RECT rect);
            long style = GetWindowLongPtr(_playerHandle, GWL_STYLE).ToInt64();
            long exStyle = GetWindowLongPtr(_playerHandle, GWL_EXSTYLE).ToInt64();
            string layeredAttributes = TryGetLayeredAttributes(out byte alpha, out uint flags)
                ? $"alpha={alpha} flags=0x{flags:X}"
                : "alpha=<unreadable>";
            DebugLog.Write(
                $"{label}: player=0x{_playerHandle.ToInt64():X} parent=0x{GetParent(_playerHandle).ToInt64():X} " +
                $"({GetClassNameString(GetParent(_playerHandle))}) visible={IsWindowVisible(_playerHandle)} " +
                $"rect=({rect.Left},{rect.Top})-({rect.Right},{rect.Bottom}) style=0x{style:X} exStyle=0x{exStyle:X} " +
                $"layered={(exStyle & WS_EX_LAYERED) != 0} {layeredAttributes} " +
                $"layeredGuard={_keepLayeredStyle} raised={_isRaisedDesktop}");
        }

        private void ApplyFrameChanged()
        {
            SetWindowPos(_playerHandle, IntPtr.Zero, 0, 0, 0, 0,
                SWP_NOMOVE | SWP_NOSIZE | SWP_NOZORDER | SWP_NOACTIVATE | SWP_FRAMECHANGED);
        }

        /// <summary>
        /// Installs the hook that keeps WPF from undoing the raised-desktop
        /// layered style. HwndSource public hooks run before WPF's
        /// HwndTarget hook. For GWL_EXSTYLE changes we put WS_EX_LAYERED
        /// back into STYLESTRUCT.styleNew and mark the notification handled,
        /// so HwndTarget does not clear it again. This deliberately keeps
        /// AllowsTransparency/UsesPerPixelOpacity false: the window uses
        /// Win32 constant-alpha layering, not WPF's UpdateLayeredWindow path.
        /// </summary>
        private void InstallLayeredStyleGuard()
        {
            if (_playerHandle == IntPtr.Zero ||
                (_playerHwndSource != null && _layeredStyleHook != null))
            {
                return;
            }

            try
            {
                _playerHwndSource = HwndSource.FromHwnd(_playerHandle);
                if (_playerHwndSource == null)
                {
                    DebugLog.Write("Layered style guard: no HwndSource for the player HWND.");
                    return;
                }

                _layeredStyleHook = LayeredStyleHook;
                _playerHwndSource.AddHook(_layeredStyleHook);
                DebugLog.Write("Layered style guard installed for the player HWND.");
            }
            catch (Exception ex)
            {
                _playerHwndSource = null;
                _layeredStyleHook = null;
                DebugLog.Write($"Layered style guard could not be installed: {ex.Message}");
            }
        }

        private void RemoveLayeredStyleGuard()
        {
            _keepLayeredStyle = false;

            HwndSource? source = _playerHwndSource;
            HwndSourceHook? hook = _layeredStyleHook;
            _playerHwndSource = null;
            _layeredStyleHook = null;

            if (source == null || hook == null)
                return;

            try
            {
                source.RemoveHook(hook);
            }
            catch
            {
                // The HWND/source may already have been destroyed with the
                // old Explorer desktop windows. There is nothing to undo.
            }
        }

        private IntPtr LayeredStyleHook(
            IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            if (!_keepLayeredStyle || hwnd != _playerHandle ||
                msg != WM_STYLECHANGING || wParam.ToInt64() != GWL_EXSTYLE ||
                lParam == IntPtr.Zero)
            {
                return IntPtr.Zero;
            }

            STYLESTRUCT style = Marshal.PtrToStructure<STYLESTRUCT>(lParam);
            style.styleNew = ForceLayeredExStyle(style.styleNew, keepLayered: true);
            Marshal.StructureToPtr(style, lParam, fDeleteOld: false);

            // Returning without handled=true would let HwndTarget process the
            // same WM_STYLECHANGING next and clear the bit we just restored.
            handled = true;
            return IntPtr.Zero;
        }

        private bool TryGetLayeredAttributes(out byte alpha, out uint flags)
        {
            alpha = 0;
            flags = 0;
            return _playerHandle != IntPtr.Zero && IsWindow(_playerHandle) &&
                   GetLayeredWindowAttributes(_playerHandle, out _, out alpha, out flags);
        }

        private bool HasOpaqueLayeredStyle()
        {
            if (_playerHandle == IntPtr.Zero || !IsWindow(_playerHandle))
                return false;

            long exStyle = GetWindowLongPtr(_playerHandle, GWL_EXSTYLE).ToInt64();
            return (exStyle & WS_EX_LAYERED) != 0 &&
                   TryGetLayeredAttributes(out byte alpha, out uint flags) &&
                   alpha == 0xFF && (flags & LWA_ALPHA) != 0;
        }

        /// <summary>
        /// Adds/removes WS_EX_LAYERED with constant alpha 255 and verifies
        /// the result by reading it back. Constant-alpha layered windows keep
        /// hardware-accelerated presents (unlike per-pixel-alpha
        /// UpdateLayeredWindow windows). No SWP_FRAMECHANGED is sent here:
        /// the style guard above is what makes the bit survive WPF.
        /// </summary>
        private bool SetLayeredOpaque(bool enabled)
        {
            if (_playerHandle == IntPtr.Zero) return false;

            if (enabled)
            {
                InstallLayeredStyleGuard();
                _keepLayeredStyle = true;
                if (HasOpaqueLayeredStyle())
                    return true;
            }
            else
            {
                // Let the clearing SetWindowLongPtr below pass through the
                // still-installed hook as a no-op, then remove the hook.
                _keepLayeredStyle = false;
            }

            long exStyle = GetWindowLongPtr(_playerHandle, GWL_EXSTYLE).ToInt64();
            long newExStyle = enabled ? exStyle | WS_EX_LAYERED : exStyle & ~WS_EX_LAYERED;
            if (newExStyle != exStyle)
            {
                IntPtr previous = SetWindowLongPtr(
                    _playerHandle, GWL_EXSTYLE, new IntPtr(newExStyle));
                int setError = previous == IntPtr.Zero ? Marshal.GetLastWin32Error() : 0;
                long after = GetWindowLongPtr(_playerHandle, GWL_EXSTYLE).ToInt64();
                DebugLog.Write(
                    $"SetLayeredOpaque({enabled}): exStyle 0x{exStyle:X} -> desired 0x{newExStyle:X}; " +
                    $"readback 0x{after:X}; SetWindowLongPtr previous=0x{previous.ToInt64():X}, " +
                    $"win32 error = {setError}.");
            }

            if (!enabled)
            {
                bool cleared =
                    (GetWindowLongPtr(_playerHandle, GWL_EXSTYLE).ToInt64() & WS_EX_LAYERED) == 0;
                RemoveLayeredStyleGuard();
                return cleared;
            }

            bool ok = SetLayeredWindowAttributes(_playerHandle, 0, 0xFF, LWA_ALPHA);
            int attributesError = ok ? 0 : Marshal.GetLastWin32Error();
            bool readBack = TryGetLayeredAttributes(out byte alpha, out uint flags);
            long finalExStyle = GetWindowLongPtr(_playerHandle, GWL_EXSTYLE).ToInt64();
            bool verified = ok && (finalExStyle & WS_EX_LAYERED) != 0 && readBack &&
                            alpha == 0xFF && (flags & LWA_ALPHA) != 0;
            DebugLog.Write(
                $"SetLayeredOpaque(true): SetLayeredWindowAttributes -> {ok}, " +
                $"win32 error = {attributesError}, exStyle now 0x{finalExStyle:X}, " +
                $"GetLayeredWindowAttributes -> {readBack}, alpha={alpha}, flags=0x{flags:X}, " +
                $"verified={verified}.");
            return verified;
        }

        /// <summary>
        /// Explorer can reshuffle its Progman children (e.g. recreating the
        /// system WorkerW after a wallpaper change), covering the player.
        /// Keeps the system WorkerW below the player.
        /// </summary>
        private void EnsureSystemWorkerWBelowPlayer()
        {
            if (!_isRaisedDesktop || _parentHandle == IntPtr.Zero ||
                _systemWorkerW == IntPtr.Zero || !IsWindow(_systemWorkerW) ||
                _playerHandle == IntPtr.Zero)
                return;

            var children = GetDirectChildren(_parentHandle);
            int playerIndex = children.IndexOf(_playerHandle);
            int workerIndex = children.IndexOf(_systemWorkerW);
            if (playerIndex >= 0 && workerIndex >= 0 && workerIndex < playerIndex)
            {
                DebugLog.Write("System WorkerW is above the player; pushing it to the bottom.");
                SetWindowPos(_systemWorkerW, HWND_BOTTOM, 0, 0, 0, 0,
                    SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);
            }
        }

        private void ResetAttachmentState(bool keepPlayerHandle)
        {
            _isAttached = false;
            _parentHandle = IntPtr.Zero;
            if (!keepPlayerHandle)
                _playerHandle = IntPtr.Zero;
        }

        #endregion

        #region Placement maintenance

        /// <summary>
        /// Resizes the player to fill its parent and re-asserts the mode's
        /// z-order: directly under SHELLDLL_DefView on a raised desktop
        /// (the SetWindowPos insert-after handle), top of the WorkerW in
        /// classic mode, bottom only for the legacy Progman last resort.
        /// </summary>
        public void ResizeToDesktop()
        {
            if (_playerHandle == IntPtr.Zero || _parentHandle == IntPtr.Zero)
                return;

            if (!GetWindowRect(_parentHandle, out RECT rect))
                return;

            int width = rect.Right - rect.Left;
            int height = rect.Bottom - rect.Top;
            IntPtr insertAfter;
            if (_isRaisedDesktop && IsProgmanParent)
            {
                insertAfter = _shellDefView != IntPtr.Zero && IsWindow(_shellDefView)
                    ? _shellDefView
                    : IntPtr.Zero;
            }
            else if (IsProgmanParent)
            {
                insertAfter = HWND_BOTTOM;
            }
            else
            {
                insertAfter = IntPtr.Zero;
            }

            SetWindowPos(_playerHandle, insertAfter, 0, 0, width, height,
                SWP_NOACTIVATE | SWP_SHOWWINDOW);
            EnsureSystemWorkerWBelowPlayer();
        }

        /// <summary>
        /// Cheap periodic self-check (called by the UI every few seconds):
        /// repairs size/z-order if Explorer reshuffled the desktop windows.
        /// Logs only when it actually repairs something.
        /// </summary>
        public void EnsurePlacement()
        {
            if (_disposed || !_isAttached ||
                _playerHandle == IntPtr.Zero || _parentHandle == IntPtr.Zero ||
                !IsWindow(_playerHandle) || !IsWindow(_parentHandle) ||
                GetParent(_playerHandle) != _parentHandle)
                return;

            bool needsRepair = !VerifyPlayerPlacement();
            if (!needsRepair && _isRaisedDesktop && IsProgmanParent &&
                _shellDefView != IntPtr.Zero && IsWindow(_shellDefView))
            {
                var children = GetDirectChildren(_parentHandle);
                int defViewIndex = children.IndexOf(_shellDefView);
                int playerIndex = children.IndexOf(_playerHandle);
                int workerIndex = _systemWorkerW != IntPtr.Zero ? children.IndexOf(_systemWorkerW) : -1;
                needsRepair = defViewIndex < 0 || playerIndex != defViewIndex + 1 ||
                              (workerIndex >= 0 && workerIndex < playerIndex);
            }

            if (needsRepair)
            {
                DebugLog.Write(
                    "EnsurePlacement: desktop placement or layered state drifted; " +
                    "re-asserting size, z-order and raised-desktop layering.");
                ResizeToDesktop();
                ShowWindow(_playerHandle, SW_SHOW);
                if (_isRaisedDesktop && IsProgmanParent)
                    SetLayeredOpaque(true);
            }
        }

        /// <summary>
        /// Reattaches to desktop after Explorer restart or display change.
        /// </summary>
        public bool Reattach()
        {
            if (_disposed || _playerWindow == null)
                return false;

            if (_parentHandle != IntPtr.Zero && IsWindow(_parentHandle) &&
                _playerHandle != IntPtr.Zero && IsWindow(_playerHandle) &&
                GetParent(_playerHandle) == _parentHandle)
            {
                ResizeToDesktop();
                ShowWindow(_playerHandle, SW_SHOW);
                if (_isRaisedDesktop && IsProgmanParent)
                    SetLayeredOpaque(true);
                return VerifyPlayerPlacement();
            }

            return Attach(_playerWindow);
        }

        #endregion

        /// <summary>
        /// Detaches the player window from the desktop.
        /// </summary>
        public void Detach()
        {
            RemoveLayeredStyleGuard();

            if (_playerHandle != IntPtr.Zero && IsWindow(_playerHandle))
            {
                SetParent(_playerHandle, IntPtr.Zero);
            }

            ResetAttachmentState(keepPlayerHandle: false);
            _isRaisedDesktop = false;
            _shellDefView = IntPtr.Zero;
            _systemWorkerW = IntPtr.Zero;
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            Detach();
        }
    }
}
