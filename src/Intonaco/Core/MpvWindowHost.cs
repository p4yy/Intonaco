using System;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows.Interop;

namespace Intonaco.Core
{
    /// <summary>
    /// Dedicated child HWND for mpv --wid. Unlike overlaying a separate
    /// top-level window, this follows Intonaco's WorkerW desktop host and
    /// inherits its layout, clipping and monitor sizing.
    /// </summary>
    internal sealed class MpvWindowHost : HwndHost
    {
        private const uint WS_CHILD = 0x40000000;
        private const uint WS_VISIBLE = 0x10000000;
        private const uint WS_CLIPCHILDREN = 0x02000000;
        private const uint WS_CLIPSIBLINGS = 0x04000000;

        [DllImport("user32.dll", EntryPoint = "CreateWindowExW",
            CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr CreateWindowEx(
            uint exStyle, string className, string? windowName, uint style,
            int x, int y, int width, int height, IntPtr parent,
            IntPtr menu, IntPtr instance, IntPtr parameter);

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool DestroyWindow(IntPtr hwnd);

        // This is the hosted child HWND for mpv, not HwndHost's own handle.
        internal new IntPtr Handle { get; private set; }

        protected override HandleRef BuildWindowCore(HandleRef parent)
        {
            Handle = CreateWindowEx(0, "Static", null,
                WS_CHILD | WS_VISIBLE | WS_CLIPCHILDREN | WS_CLIPSIBLINGS,
                0, 0, 1, 1, parent.Handle,
                IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
            if (Handle == IntPtr.Zero)
                throw new Win32Exception(Marshal.GetLastWin32Error(),
                    "Could not create the MPV wallpaper surface.");
            return new HandleRef(this, Handle);
        }

        protected override void DestroyWindowCore(HandleRef hwnd)
        {
            if (hwnd.Handle != IntPtr.Zero)
                DestroyWindow(hwnd.Handle);
            Handle = IntPtr.Zero;
        }
    }
}
