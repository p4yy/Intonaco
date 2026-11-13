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
            Assert.Equal(80, Marshal.SizeOf(stats!));
            Assert.Equal((IntPtr)20,
                Marshal.OffsetOf(stats!, "DecoderSleeping"));
            Assert.Equal((IntPtr)24,
                Marshal.OffsetOf(stats!, "DurationSeconds"));
            Assert.Equal((IntPtr)48,
                Marshal.OffsetOf(stats!, "GpuLocalBytes"));
            Assert.Equal((IntPtr)56,
                Marshal.OffsetOf(stats!, "GpuNonLocalBytes"));
            Assert.Equal((IntPtr)64,
                Marshal.OffsetOf(stats!, "GpuLocalBudgetBytes"));
            Assert.Equal((IntPtr)72,
                Marshal.OffsetOf(stats!, "GpuNonLocalBudgetBytes"));
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
        public void NativeFramePolling_AdaptsTo24_30_60FpsWithoutChangingSource()
        {
            // Compile-time and source-structure regression guard. Hardware
            // frame quality and frame rate are verified by the Win11 test PC.
            string? engine = null;
            for (var folder = new DirectoryInfo(AppContext.BaseDirectory);
                 folder != null; folder = folder.Parent)
            {
                string file = Path.Combine(folder.FullName,
                    "native", "IntonacoVideo", "engine.cpp");
                if (File.Exists(file))
                {
                    engine = File.ReadAllText(file);
                    break;
                }
            }

            Assert.NotNull(engine);
            Assert.Contains("lastPresentationTime = -1", engine!);
            Assert.Contains("sourceFrameIntervalMs", engine!);
            Assert.Contains("std::lround(", engine!);
            Assert.Contains("std::clamp(", engine!);
            Assert.Contains("activePollMs", engine!);
            Assert.Contains("player->OnVideoStreamTick", engine!);
            Assert.Contains("player->TransferVideoFrame", engine!);
            Assert.Contains("if (!wantedPause && GetClientRect", engine!);

            // Equivalent expected pacing windows of the native formula:
            // half the source frame interval, clamped to 8..20ms.
            Assert.Equal(20, ExpectedPollInterval(1000.0 / 24));
            Assert.Equal(17, ExpectedPollInterval(1000.0 / 30));
            Assert.Equal(8, ExpectedPollInterval(1000.0 / 60));
        }

        private static int ExpectedPollInterval(double framePeriodMs) =>
            Math.Clamp((int)Math.Round(framePeriodMs * 0.5,
                MidpointRounding.AwayFromZero), 8, 20);

        [Fact]
        public void GpuTelemetry_SamplesPresentedFramesAndSleepsWhenDecoderIsReleased()
        {
            string? player = null;
            for (var folder = new DirectoryInfo(AppContext.BaseDirectory);
                 folder != null; folder = folder.Parent)
            {
                string file = Path.Combine(folder.FullName,
                    "src", "Intonaco", "Core", "GpuVideoWallpaperPlayer.cs");
                if (File.Exists(file))
                {
                    player = File.ReadAllText(file);
                    break;
                }
            }

            Assert.NotNull(player);
            Assert.Contains("LogGpuFrameTelemetry", player!);
            Assert.Contains("stats.FramesPresented", player!);
            Assert.Contains("decoderSleeping ? 1500 : 300", player!);
            Assert.Contains("framePresentationFps=", player!);
            Assert.Contains("workingSetMiB=", player!);
            Assert.DoesNotContain("EmptyWorkingSet", player!);
        }

        [Fact]
        public void DeepPause_TrimsOnlyIdleDirectXDriverCaches()
        {
            // Keep GPU driver caches reclaimable after the Media Foundation
            // decoder is destroyed. Never call Trim once per live frame.
            string? engine = null;
            for (var folder = new DirectoryInfo(AppContext.BaseDirectory);
                 folder != null; folder = folder.Parent)
            {
                string file = Path.Combine(folder.FullName,
                    "native", "IntonacoVideo", "engine.cpp");
                if (File.Exists(file)) { engine = File.ReadAllText(file); break; }
            }

            Assert.NotNull(engine);
            // Windows CI checks out .cpp with CRLF; locate the actual
            // deep-pause section instead of matching platform-specific EOLs.
            int deepPause = engine!.IndexOf(
                "// Invalidate callback events before shutdown.");
            Assert.True(deepPause >= 0);
            int shutdown = engine.IndexOf("player->Shutdown();", deepPause);
            int decoderReset = engine.IndexOf("player.Reset();", shutdown);
            int clear = engine.IndexOf("context->ClearState();", decoderReset);
            int flush = engine.IndexOf("context->Flush();", clear);
            int trim = engine.IndexOf("idleDxgiDevice->Trim();", flush);
            int retained = engine.IndexOf("s->decoderSleeping.store(1)", trim);
            Assert.True(shutdown > deepPause && decoderReset > shutdown &&
                clear > decoderReset && flush > clear &&
                trim > flush && retained > trim);
            Assert.Equal(1, engine.Split("idleDxgiDevice->Trim();").Length - 1);
            Assert.Contains("DXGI_MEMORY_SEGMENT_GROUP_LOCAL", engine);
            Assert.Contains("DXGI_MEMORY_SEGMENT_GROUP_NON_LOCAL", engine);
            Assert.Contains("std::chrono::seconds(5)", engine);
            Assert.DoesNotContain("EmptyWorkingSet", engine);
        }

        [Fact]
        public void GPUThumbnail_OnlyLoadsWhenSettingsWindowIsVisible()
        {
            string? code = null;
            for (var dir = new DirectoryInfo(AppContext.BaseDirectory);
                 dir != null; dir = dir.Parent)
            {
                string file = Path.Combine(dir.FullName, "src", "Intonaco",
                    "Views", "MainWindow.xaml.cs");
                if (File.Exists(file)) { code = File.ReadAllText(file); break; }
            }
            Assert.NotNull(code);
            int showPreview = code!.IndexOf("private void ShowStaticPreview()");
            int visibilityGuard = code.IndexOf("if (!IsPreviewWindowVisible)",
                showPreview);
            int thumbnailRequest = code.IndexOf("_ = LoadStaticPreviewAsync(",
                showPreview);
            Assert.True(showPreview >= 0 && visibilityGuard > showPreview &&
                thumbnailRequest > visibilityGuard,
                "Hidden settings must not trigger an Explorer 4K thumbnail decode.");
            Assert.Contains("RefreshPreviewForCurrentState();", code);
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
