#if INTONACO_STORE
using System;
using System.Threading.Tasks;
using Windows.ApplicationModel;
using Windows.ApplicationModel.Activation;

namespace Intonaco.Core
{
    /// <summary>
    /// Manages the manifest-registered Store startup task. Unlike raw HKCU Run
    /// entries, this survives MSIX deployment and respects Windows Startup Apps.
    /// All calls are asynchronous to avoid blocking the WPF dispatcher.
    /// </summary>
    internal static class StoreStartupManager
    {
        internal const string TaskId = "IntonacoWallpaperStartup";

        internal static bool IsStartupActivation()
        {
            try
            {
                return AppInstance.GetActivatedEventArgs()?.Kind ==
                    ActivationKind.StartupTask;
            }
            catch
            {
                return false;
            }
        }

        internal static async Task<bool> IsEnabledAsync()
        {
            var task = await StartupTask.GetAsync(TaskId);
            return task.State == StartupTaskState.Enabled;
        }

        internal static async Task<bool> SetEnabledAsync(bool enabled)
        {
            var task = await StartupTask.GetAsync(TaskId);
            if (enabled && task.State == StartupTaskState.Disabled)
                await task.RequestEnableAsync();
            else if (!enabled && task.State == StartupTaskState.Enabled)
                task.Disable();

            return task.State == StartupTaskState.Enabled;
        }
    }
}
#endif
