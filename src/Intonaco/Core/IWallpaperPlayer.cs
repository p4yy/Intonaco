using System;
using System.Windows;
using System.Windows.Media;

namespace Intonaco.Core
{
    /// <summary>
    /// Common playback contract so the existing Media Foundation backend and
    /// the optional MPV renderer share the same desktop, tray and pause logic.
    /// No decoder libraries are loaded when MPV is not selected.
    /// </summary>
    public interface IWallpaperPlayer : IDisposable
    {
        event Action<string>? ErrorOccurred;
        event Action<WallpaperPlayer.PlaybackState>? StateChanged;
        event Action<TimeSpan>? MediaOpened;

        WallpaperPlayer.PlaybackState CurrentState { get; }
        bool IsMuted { get; }
        double Volume { get; set; }
        int TargetFps { get; set; }
        Stretch StretchMode { get; set; }
        double PlaybackSpeed { get; set; }
        bool LoopPlayback { get; set; }
        TimeSpan Duration { get; }
        TimeSpan Position { get; }
        int NaturalVideoWidth { get; }
        int NaturalVideoHeight { get; }
        string? CurrentFilePath { get; }

        FrameworkElement CreateHost();
        bool Play(string path);
        void Pause();
        void PauseAt(TimeSpan position);
        void Resume();
        void Stop();
        void Seek(TimeSpan position);
        void SetMute(bool value);
        void ToggleMute();
    }
}
