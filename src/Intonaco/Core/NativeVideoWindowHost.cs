using System;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows.Interop;

namespace Intonaco.Core
{
    /// <summary>
    /// Lightweight child HWND used by the Windows in-process media renderer.
    /// EVR presents into this native window directly, without creating a
    /// second WPF MediaElement or full-resolution WPF bitmap.
    /// </summary>
    internal sealed class NativeVideoWindowHost : HwndHost
    {
        private const uint Child = 0x40000000;
        private const uint Visible = 0x10000000;
        private const uint ClipChildren = 0x02000000;
        private const uint ClipSiblings = 0x04000000;

        [DllImport("user32.dll", EntryPoint = "CreateWindowExW",
            SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern IntPtr CreateWindowEx(
            uint exStyle, string className, string? title, uint style,
            int x, int y, int width, int height, IntPtr parent,
            IntPtr menu, IntPtr instance, IntPtr parameter);

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool DestroyWindow(IntPtr hwnd);

        internal new IntPtr Handle { get; private set; }

        protected override HandleRef BuildWindowCore(HandleRef parent)
        {
            Handle = CreateWindowEx(0, "Static", null,
                Child | Visible | ClipChildren | ClipSiblings,
                0, 0, 1, 1, parent.Handle,
                IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
            if (Handle == IntPtr.Zero)
                throw new Win32Exception(Marshal.GetLastWin32Error(),
                    "Windows could not create the built-in wallpaper video window.");
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
