using Microsoft.Win32;
using System;
using System.Diagnostics;
using System.Linq;

namespace Intonaco.Core
{
    public static class StartupManager
    {
        private const string RegistryKey = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Run";
        private const string AppName = "Intonaco";

        public static bool IsRegistered
        {
            get
            {
                try
                {
                    using var key = Registry.CurrentUser.OpenSubKey(RegistryKey, false);
                    return key?.GetValue(AppName) != null;
                }
                catch
                {
                    return false;
                }
            }
        }

        public static string? GetRegisteredValue()
        {
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(RegistryKey, false);
                return key?.GetValue(AppName) as string;
            }
            catch { return null; }
        }

        // A startup launch may be silent, but a deliberate Start menu or
        // desktop shortcut launch must always open the UI normally.
        internal static string BuildStartupCommand(string exePath, bool startMinimized) =>
            $"\"{exePath}\"" + (startMinimized ? " --minimized" : "");

        public static void Register(bool startMinimized = false)
        {
            try
            {
                string exePath = Process.GetCurrentProcess().MainModule?.FileName
                    ?? Environment.ProcessPath
                    ?? throw new InvalidOperationException("Cannot determine executable path");

                using var key = Registry.CurrentUser.OpenSubKey(RegistryKey, true);
                string command = BuildStartupCommand(exePath, startMinimized);
                key?.SetValue(AppName, command);
                DebugLog.Write($"StartupManager: registered '{command}'");
            }
            catch (Exception ex)
            {
                DebugLog.Write($"StartupManager register failed: {ex.Message}");
            }
        }

        public static void Unregister()
        {
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(RegistryKey, true);
                key?.DeleteValue(AppName, false);
                DebugLog.Write("StartupManager: unregistered Intonaco from Run key");
            }
            catch (Exception ex)
            {
                DebugLog.Write($"StartupManager unregister failed: {ex.Message}");
            }
        }

        /// <summary>
        /// True if the current process was launched with a minimised flag
        /// (--minimized, -m, --start-minimized, /minimized).
        /// </summary>
        public static bool ShouldStartMinimized(string[]? args = null)
        {
            args ??= Environment.GetCommandLineArgs();
            return args.Any(arg =>
                arg.Equals("--minimized", StringComparison.OrdinalIgnoreCase) ||
                arg.Equals("-m", StringComparison.OrdinalIgnoreCase) ||
                arg.Equals("--start-minimized", StringComparison.OrdinalIgnoreCase) ||
                arg.Equals("/minimized", StringComparison.OrdinalIgnoreCase));
        }
    }
}
