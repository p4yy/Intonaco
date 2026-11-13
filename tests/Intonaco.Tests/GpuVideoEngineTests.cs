using System;
using System.IO;
using System.Runtime.InteropServices;
using Intonaco.Core;
using Xunit;

namespace Intonaco.Tests
{
    /// <summary>
    /// The GPU engine is authored and bundled with Intonaco; no external
    /// video player or repository dependency is required by the installer.
    /// CI virtual machines may not provide a video-decode GPU, so playback
    /// and RAM on target hardware remain separate acceptance tests.
    /// </summary>
    public sealed class GpuVideoEngineTests
    {
        [WpfFact]
        public void GpuPlayer_UsesNativeHwndInsteadOfMediaElement()
        {
            using var player = new GpuVideoWallpaperPlayer();
            var host = player.CreateHost();
            Assert.IsType<NativeVideoWindowHost>(host);
            Assert.Null(player.CurrentFilePath);
            Assert.Equal(WallpaperPlayer.PlaybackState.Stopped, player.CurrentState);
            player.SetMute(true);
            player.TargetFps = 30;
            player.Stop();
        }

        [Fact]
        public void GpuBackend_RemainsSelectableWithoutChangingLegacySettings()
        {
            Assert.Equal("GPU", AppSettings.NormalizeBackend("GPU"));
            Assert.Equal("GPU", AppSettings.NormalizeBackend("gpu"));
            Assert.Equal("Native", AppSettings.NormalizeBackend("Native"));
            Assert.Equal("Windows", AppSettings.NormalizeBackend("Windows"));
            Assert.Equal("MPV", AppSettings.NormalizeBackend("MPV"));
        }

        [Fact]
        public void OriginalVideoModule_HasAllExpectedNativeExports()
        {
            if (!OperatingSystem.IsWindows())
                return;

            // Locate compiled output in the current solution tree. The CI
            // pipeline builds the x64 native DLL before dotnet restore.
            string? path = null;
            for (var dir = new DirectoryInfo(AppContext.BaseDirectory);
                 dir != null; dir = dir.Parent)
            {
                var test = Path.Combine(dir.FullName,
                    "src", "Intonaco", "native", "Intonaco.Video.dll");
                if (File.Exists(test))
                {
                    path = test;
                    break;
                }
            }

            Assert.False(string.IsNullOrEmpty(path),
                "CMake must build and bundle Intonaco.Video.dll before .NET tests.");
            IntPtr handle = NativeLibrary.Load(path!);
            try
            {
                foreach (string symbol in new[]
                {
                    "IntonacoVideo_Create", "IntonacoVideo_Configure",
                    "IntonacoVideo_Seek", "IntonacoVideo_GetStats",
                    "IntonacoVideo_Destroy"
                })
                    Assert.NotEqual(IntPtr.Zero, NativeLibrary.GetExport(handle, symbol));
            }
            finally
            {
                NativeLibrary.Free(handle);
            }
        }
    }
}
