using System;
using System.Runtime.InteropServices;

namespace Intonaco.Core
{
    /// <summary>
    /// Minimal x64 COM declarations for Windows' in-process MFPlay renderer.
    /// Method order follows the Windows SDK mfplay.h interface vtables.
    /// MFPlay is an OS component, not an additional app or helper process.
    /// </summary>
    internal static class NativeMfPlay
    {
        internal static readonly Guid Position100Ns = Guid.Empty;

        [DllImport("mfplay.dll", CharSet = CharSet.Unicode,
            EntryPoint = "MFPCreateMediaPlayer", PreserveSig = false)]
        internal static extern void CreatePlayer(
            [MarshalAs(UnmanagedType.LPWStr)] string url,
            [MarshalAs(UnmanagedType.Bool)] bool startPlayback,
            int options,
            [MarshalAs(UnmanagedType.Interface)] IMfpMediaPlayerCallback callback,
            IntPtr videoWindow,
            [MarshalAs(UnmanagedType.Interface)] out IMfpMediaPlayer player);

        [DllImport("ole32.dll")]
        internal static extern int PropVariantClear(ref NativePropVariant variant);

        [StructLayout(LayoutKind.Explicit, Size = 24)]
        internal struct NativePropVariant
        {
            [FieldOffset(0)] internal ushort VariantType;
            [FieldOffset(8)] internal long Value;

            internal static NativePropVariant FromTime(TimeSpan position) =>
                new() { VariantType = 20, Value = position.Ticks }; // VT_I8, 100 ns

            internal TimeSpan AsTime()
            {
                if (VariantType is not (20 or 21) || Value < 0)
                    return TimeSpan.Zero;
                return TimeSpan.FromTicks(Value);
            }
        }

        [StructLayout(LayoutKind.Sequential)]
        internal struct VideoSize
        {
            internal int Width;
            internal int Height;
        }

        [StructLayout(LayoutKind.Sequential)]
        internal struct VideoRect
        {
            internal float Left;
            internal float Top;
            internal float Right;
            internal float Bottom;

            internal static VideoRect Full => new()
            {
                Left = 0, Top = 0, Right = 1, Bottom = 1
            };
        }

        [StructLayout(LayoutKind.Sequential)]
        internal struct EventHeader
        {
            internal int EventType;
            internal int HResult;
            internal IntPtr Player;
            internal int State;
            internal IntPtr PropertyStore;
        }

        // MFP_EVENT_TYPE; value alignment is defined in mfplay.h.
        internal const int MediaItemCreated = 5;
        internal const int MediaItemSet = 6;
        internal const int Playing = 0;
        internal const int PlaybackEnded = 11;

        [ComVisible(true)]
        [Guid("766C8FFB-5FDB-4fea-A28D-B912996F51BD")]
        [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        internal interface IMfpMediaPlayerCallback
        {
            void OnMediaPlayerEvent(IntPtr eventHeader);
        }

        [ComImport]
        [Guid("A714590A-58AF-430A-85BF-44F5EC838D85")]
        [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        internal interface IMfpMediaPlayer
        {
            void Play();
            void Pause();
            void Stop();
            void FrameStep();
            void SetPosition(ref Guid positionType, ref NativePropVariant position);
            void GetPosition(ref Guid positionType, out NativePropVariant position);
            void GetDuration(ref Guid positionType, out NativePropVariant duration);
            void SetRate(float rate);
            void GetRate(out float rate);
            void GetSupportedRates([MarshalAs(UnmanagedType.Bool)] bool forward,
                out float slowest, out float fastest);
            void GetState(out int state);
            void CreateMediaItemFromURL([MarshalAs(UnmanagedType.LPWStr)] string url,
                [MarshalAs(UnmanagedType.Bool)] bool synchronous,
                UIntPtr data, out IntPtr mediaItem);
            void CreateMediaItemFromObject(IntPtr obj,
                [MarshalAs(UnmanagedType.Bool)] bool synchronous,
                UIntPtr data, out IntPtr mediaItem);
            void SetMediaItem(IMfpMediaItem item);
            void ClearMediaItem();
            void GetMediaItem(out IntPtr item);
            void GetVolume(out float volume);
            void SetVolume(float volume);
            void GetBalance(out float balance);
            void SetBalance(float balance);
            void GetMute([MarshalAs(UnmanagedType.Bool)] out bool muted);
            void SetMute([MarshalAs(UnmanagedType.Bool)] bool mute);
            void GetNativeVideoSize(out VideoSize source, out VideoSize aspect);
            void GetIdealVideoSize(out VideoSize minimum, out VideoSize maximum);
            void SetVideoSourceRect(ref VideoRect rect);
            void GetVideoSourceRect(out VideoRect rect);
            void SetAspectRatioMode(uint mode);
            void GetAspectRatioMode(out uint mode);
            void GetVideoWindow(out IntPtr hwnd);
            void UpdateVideo();
            void SetBorderColor(uint color);
            void GetBorderColor(out uint color);
            void InsertEffect(IntPtr effect, [MarshalAs(UnmanagedType.Bool)] bool optional);
            void RemoveEffect(IntPtr effect);
            void RemoveAllEffects();
            void Shutdown();
        }

        [ComImport]
        [Guid("90EB3E6B-ECBF-45CC-B1DA-C6FE3EA70D57")]
        [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        internal interface IMfpMediaItem
        {
            void GetMediaPlayer(out IntPtr player);
            void GetURL(out IntPtr url);
            void GetObject(out IntPtr media);
            void GetUserData(out UIntPtr data);
            void SetUserData(UIntPtr data);
            void GetStartStopPosition(IntPtr startType, IntPtr start,
                IntPtr stopType, IntPtr stop);
            void SetStartStopPosition(IntPtr startType, IntPtr start,
                IntPtr stopType, IntPtr stop);
            void HasVideo([MarshalAs(UnmanagedType.Bool)] out bool hasVideo,
                [MarshalAs(UnmanagedType.Bool)] out bool selected);
            void HasAudio([MarshalAs(UnmanagedType.Bool)] out bool hasAudio,
                [MarshalAs(UnmanagedType.Bool)] out bool selected);
            void IsProtected([MarshalAs(UnmanagedType.Bool)] out bool protectedMedia);
            void GetDuration(ref Guid positionType, out NativePropVariant duration);
        }
    }
}
