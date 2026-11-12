using System;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows.Interop;

namespace Intonaco.Core
{
    /// <summary>
    /// Own, isolated child HWND for the native D3D11 swapchain.
    /// The WPF settings panel never paints or copies decoded video frames.
    /// </summary>
    internal sealed class NativeVideoWindowHost : HwndHost
    {
        private const uint WsChild = 0x40000000;
        private const uint WsVisible = 0x10000000;
        private const uint WsClipChildren = 0x02000000;
        private const uint WsClipSiblings = 0x04000000;

        [DllImport("user32.dll", EntryPoint = "CreateWindowExW",
            CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr CreateWindowEx(
            uint exStyle, string className, string? windowName, uint style,
            int x, int y, int width, int height, IntPtr parent,
            IntPtr menu, IntPtr instance, IntPtr parameter);

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool DestroyWindow(IntPtr hwnd);

        internal new IntPtr Handle { get; private set; }

        protected override HandleRef BuildWindowCore(HandleRef parent)
        {
            Handle = CreateWindowEx(0, "Static", null,
                WsChild | WsVisible | WsClipChildren | WsClipSiblings,
                0, 0, 1, 1, parent.Handle,
                IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
            if (Handle == IntPtr.Zero)
                throw new Win32Exception(Marshal.GetLastWin32Error(),
                    "Could not create Intonaco's native video surface.");
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
