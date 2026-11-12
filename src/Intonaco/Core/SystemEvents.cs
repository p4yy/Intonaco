using System;
using Microsoft.Win32;

namespace Intonaco.Core
{
    /// <summary>
    /// Monitors system power state, session lock/unlock, and display changes
    /// to pause/resume wallpaper playback appropriately.
    /// </summary>
    public sealed class SystemEventMonitor : IDisposable
    {
        private bool _disposed;
        private bool _isLocked;
        private bool _isSuspended;

        public event Action? ShouldPause;
        public event Action? ShouldResume;
        public event Action? DisplayChanged;

        public bool IsPausedBySystem => _isLocked || _isSuspended;

        public void Start()
        {
            SystemEvents.PowerModeChanged += OnPowerModeChanged;
            SystemEvents.SessionSwitch += OnSessionSwitch;
            SystemEvents.DisplaySettingsChanged += OnDisplayChanged;
        }

        public void Stop()
        {
            SystemEvents.PowerModeChanged -= OnPowerModeChanged;
            SystemEvents.SessionSwitch -= OnSessionSwitch;
            SystemEvents.DisplaySettingsChanged -= OnDisplayChanged;
        }

        private void OnPowerModeChanged(object sender, PowerModeChangedEventArgs e)
        {
            switch (e.Mode)
            {
                case PowerModes.Suspend:
                    _isSuspended = true;
                    ShouldPause?.Invoke();
                    break;
                case PowerModes.Resume:
                    _isSuspended = false;
                    if (!IsPausedBySystem)
                        ShouldResume?.Invoke();
                    break;
            }
        }

        private void OnSessionSwitch(object sender, SessionSwitchEventArgs e)
        {
            switch (e.Reason)
            {
                case SessionSwitchReason.SessionLock:
                case SessionSwitchReason.ConsoleDisconnect:
                    _isLocked = true;
                    ShouldPause?.Invoke();
                    break;
                case SessionSwitchReason.SessionUnlock:
                case SessionSwitchReason.ConsoleConnect:
                    _isLocked = false;
                    if (!IsPausedBySystem)
                        ShouldResume?.Invoke();
                    break;
            }
        }

        private void OnDisplayChanged(object? sender, EventArgs e)
        {
            DisplayChanged?.Invoke();
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            Stop();
        }
    }
}
