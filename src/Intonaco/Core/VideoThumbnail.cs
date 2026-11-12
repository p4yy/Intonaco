using System;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media.Imaging;

namespace Intonaco.Core
{
    /// <summary>
    /// Extracts a single Explorer-style thumbnail off the UI thread.
    /// Unlike a WPF MediaElement, this does not keep another video decoder
    /// active while the desktop wallpaper is playing.
    /// </summary>
    internal static class VideoThumbnail
    {
        private static readonly SemaphoreSlim Gate = new(1, 1);
        private const uint ThumbnailOnly = 0x8;

        [StructLayout(LayoutKind.Sequential)]
        private struct ShellSize
        {
            internal int Width;
            internal int Height;
            internal ShellSize(int width, int height)
            {
                Width = width;
                Height = height;
            }
        }

        [ComImport]
        [Guid("bcc18b79-ba16-442f-80c4-8a59c30c463b")]
        [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IShellItemImageFactory
        {
            [PreserveSig]
            int GetImage(ShellSize size, uint flags, out IntPtr bitmap);
        }

        [DllImport("shell32.dll", CharSet = CharSet.Unicode, PreserveSig = false)]
        private static extern void SHCreateItemFromParsingName(
            [MarshalAs(UnmanagedType.LPWStr)] string path,
            IntPtr bindContext,
            [In] ref Guid iid,
            [MarshalAs(UnmanagedType.Interface)] out IShellItemImageFactory imageFactory);

        [DllImport("gdi32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool DeleteObject(IntPtr handle);

        internal static async Task<BitmapSource?> LoadAsync(
            string filePath, CancellationToken cancellationToken = default)
        {
            await Gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                var tcs = new TaskCompletionSource<BitmapSource?>(
                    TaskCreationOptions.RunContinuationsAsynchronously);
                var thread = new Thread(() =>
                {
                    try { tcs.TrySetResult(Extract(filePath)); }
                    catch (Exception) { tcs.TrySetResult(null); }
                })
                {
                    IsBackground = true,
                    Name = "Intonaco thumbnail"
                };
                thread.SetApartmentState(ApartmentState.STA);
                thread.Start();
                return await tcs.Task.ConfigureAwait(false);
            }
            finally
            {
                Gate.Release();
            }
        }

        private static BitmapSource? Extract(string path)
        {
            IShellItemImageFactory? factory = null;
            IntPtr hbitmap = IntPtr.Zero;
            try
            {
                Guid iid = typeof(IShellItemImageFactory).GUID;
                SHCreateItemFromParsingName(path, IntPtr.Zero,
                    ref iid, out factory);
                if (factory == null ||
                    factory.GetImage(new ShellSize(480, 270),
                        ThumbnailOnly, out hbitmap) != 0 ||
                    hbitmap == IntPtr.Zero)
                    return null;

                BitmapSource image = Imaging.CreateBitmapSourceFromHBitmap(
                    hbitmap, IntPtr.Zero, Int32Rect.Empty,
                    BitmapSizeOptions.FromEmptyOptions());
                image.Freeze(); // Safe to pass from the STA thread to WPF.
                return image;
            }
            finally
            {
                if (hbitmap != IntPtr.Zero)
                    DeleteObject(hbitmap);
                if (factory != null)
                    Marshal.ReleaseComObject(factory);
            }
        }
    }
}
