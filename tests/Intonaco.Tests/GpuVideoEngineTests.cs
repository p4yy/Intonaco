using System;
using System.IO;
using System.Reflection;
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
        public void DeepPauseNativeTelemetry_KeepsCorrect64BitAbi()
        {
            var stats = typeof(GpuVideoWallpaperPlayer).GetNestedType(
                "Stats", BindingFlags.NonPublic);
            Assert.NotNull(stats);
            Assert.Equal(48, Marshal.SizeOf(stats!));
            Assert.Equal((IntPtr)20,
                Marshal.OffsetOf(stats!, "DecoderSleeping"));
            Assert.Equal((IntPtr)24,
                Marshal.OffsetOf(stats!, "DurationSeconds"));
        }

        [Fact]
        public void DeepPause_ReleasesVideoDecoderButKeepsDxgiStillFrame()
        {
            string? source = null;
            for (var folder = new DirectoryInfo(AppContext.BaseDirectory);
                 folder != null; folder = folder.Parent)
            {
                string path = Path.Combine(folder.FullName,
                    "native", "IntonacoVideo", "engine.cpp");
                if (File.Exists(path))
                {
                    source = File.ReadAllText(path);
                    break;
                }
            }

            Assert.NotNull(source);
            Assert.Contains("DeepPauseDelay", source!);
            Assert.Contains("player->Shutdown()", source!);
            Assert.Contains("player.Reset()", source!);
            Assert.Contains("s->decoderSleeping.store(1)", source!);
            Assert.Contains("resumeNeedsSeek", source!);
            Assert.Contains("generation_ != s_->generation.load", source!);
            Assert.DoesNotContain("EmptyWorkingSet", source!);
        }

        [Fact]
        public void GpuLoop_CachesStaticMetadataAndThrottlesClockQueries()
        {
            // This test guards against reintroducing unnecessary Windows
            // Media Foundation COM traffic on every 16ms display tick.
            string? source = null;
            for (var folder = new DirectoryInfo(AppContext.BaseDirectory);
                 folder != null; folder = folder.Parent)
            {
                string file = Path.Combine(folder.FullName,
                    "native", "IntonacoVideo", "engine.cpp");
                if (File.Exists(file))
                {
                    source = File.ReadAllText(file);
                    break;
                }
            }

            Assert.NotNull(source);
            Assert.Contains("bool metadataReady = false", source!);
            Assert.Contains("!metadataReady", source!);
            Assert.Contains("std::chrono::milliseconds(500)", source!);
            Assert.Contains("std::chrono::milliseconds(200)", source!);
            Assert.Contains("metadataReady = false;", source!);
            Assert.Contains("player->OnVideoStreamTick", source!);
            Assert.Contains("player->TransferVideoFrame", source!);
            Assert.DoesNotContain("EmptyWorkingSet", source!);
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
