// Intonaco Video — original GPU-first Windows wallpaper renderer.
// Licensed under the Intonaco repository license. No Lively/mpv/VLC code.
// Uses Windows Media Foundation + D3D11 GPU surfaces without CPU frame copies.
#include <windows.h>
#include <d3d11.h>
#include <d3d10.h>
#include <dxgi1_3.h>
#include <mfapi.h>
#include <mferror.h>
#include <mfidl.h>
#include <mfmediaengine.h>
#include <wrl/client.h>
#include <atomic>
#include <chrono>
#include <memory>
#include <new>
#include <string>
#include <thread>
#include <algorithm>
#include <cmath>
#include <cstddef>

using Microsoft::WRL::ComPtr;

namespace
{
    // Stable across the C ABI; C# declares this exact layout.
    struct VideoStats
    {
        int phase;       // 0=start, 1=ready, 2=playing, 3=paused, -1=error
        int errorHr;
        int gpuDecode;   // 1=real D3D11 hardware device + DXGI manager
        int width;
        int height;
        int decoderSleeping; // 1=Media Foundation decoder released; frozen DXGI frame retained
        double duration;
        double position;
        long long frames;
    };

    // C++ and .NET must agree on this fixed native telemetry layout.
    static_assert(sizeof(VideoStats) == 48);
    static_assert(offsetof(VideoStats, duration) == 24);

    struct SharedState
    {
        std::atomic<bool> stop{false};
        std::atomic<bool> finished{false};
        std::atomic<bool> pause{false};
        std::atomic<bool> mute{true};
        std::atomic<bool> loop{true};
        std::atomic<bool> fill{true};
        std::atomic<bool> seekRequested{false};
        std::atomic<bool> loaded{false};
        std::atomic<bool> canPlay{false};
        std::atomic<bool> ended{false};
        std::atomic<int> phase{0};
        std::atomic<int> error{0};
        std::atomic<int> generation{0};
        std::atomic<int> decoderSleeping{0};
        std::atomic<bool> seeked{false};
        std::atomic<int> gpu{0};
        std::atomic<int> width{0}, height{0};
        std::atomic<double> volume{0}, speed{1}, duration{0}, position{0}, seekTo{0};
        std::atomic<long long> frames{0};
    };

    // Owns a ref-counted, shared state. No native media calls are made from
    // Media Foundation's callback thread, preventing COM-thread deadlocks.
    class Notify final : public IMFMediaEngineNotify
    {
        std::atomic<ULONG> refs_{1};
        std::shared_ptr<SharedState> s_;
        int generation_;
    public:
        Notify(std::shared_ptr<SharedState> s, int generation)
            : s_(std::move(s)), generation_(generation) {}

        STDMETHODIMP QueryInterface(REFIID id, void** output) override
        {
            if (!output) return E_POINTER;
            *output = nullptr;
            if (id == IID_IUnknown || id == __uuidof(IMFMediaEngineNotify))
            {
                *output = static_cast<IMFMediaEngineNotify*>(this);
                AddRef();
                return S_OK;
            }
            return E_NOINTERFACE;
        }

        STDMETHODIMP_(ULONG) AddRef() override { return ++refs_; }
        STDMETHODIMP_(ULONG) Release() override
        {
            ULONG count = --refs_;
            if (count == 0) delete this;
            return count;
        }

        STDMETHODIMP EventNotify(DWORD evt, DWORD_PTR p1, DWORD) override
        {
            // Ignore asynchronous events from a decoder that has already
            // been shut down for the paused still-frame state.
            if (s_->stop.load() ||
                generation_ != s_->generation.load(std::memory_order_acquire))
                return S_OK;
            switch (evt)
            {
                case MF_MEDIA_ENGINE_EVENT_LOADEDMETADATA:
                    s_->loaded.store(true);
                    break;
                case MF_MEDIA_ENGINE_EVENT_CANPLAY:
                    s_->canPlay.store(true);
                    break;
                case MF_MEDIA_ENGINE_EVENT_SEEKED:
                    s_->seeked.store(true);
                    break;
                case MF_MEDIA_ENGINE_EVENT_ENDED:
                    s_->ended.store(true);
                    break;
                case MF_MEDIA_ENGINE_EVENT_ERROR:
                    s_->error.store(static_cast<int>(p1 == 0 ? E_FAIL : p1));
                    s_->phase.store(-1);
                    break;
                case MF_MEDIA_ENGINE_EVENT_RESOURCELOST:
                    s_->error.store(DXGI_ERROR_DEVICE_REMOVED);
                    s_->phase.store(-1);
                    break;
                default:
                    break;
            }
            return S_OK;
        }
    };

    HRESULT CreateDevice(ComPtr<ID3D11Device>& device,
        ComPtr<ID3D11DeviceContext>& context)
    {
        static constexpr D3D_FEATURE_LEVEL levels[] = {
            D3D_FEATURE_LEVEL_11_1, D3D_FEATURE_LEVEL_11_0,
            D3D_FEATURE_LEVEL_10_1, D3D_FEATURE_LEVEL_10_0
        };
        D3D_FEATURE_LEVEL actual{};
        const UINT flags = D3D11_CREATE_DEVICE_BGRA_SUPPORT |
                           D3D11_CREATE_DEVICE_VIDEO_SUPPORT;
        HRESULT hr = D3D11CreateDevice(nullptr, D3D_DRIVER_TYPE_HARDWARE,
            nullptr, flags, levels, _countof(levels), D3D11_SDK_VERSION,
            device.GetAddressOf(), &actual, context.GetAddressOf());
        if (hr == E_INVALIDARG)
        {
            // Older drivers may not accept the 11.1 feature-level entry.
            hr = D3D11CreateDevice(nullptr, D3D_DRIVER_TYPE_HARDWARE,
                nullptr, flags, levels + 1, _countof(levels) - 1,
                D3D11_SDK_VERSION, device.GetAddressOf(), &actual,
                context.GetAddressOf());
        }
        if (SUCCEEDED(hr))
        {
            ComPtr<ID3D10Multithread> mt;
            if (SUCCEEDED(device.As(&mt)))
                mt->SetMultithreadProtected(TRUE);
        }
        return hr;
    }

    struct Engine
    {
        HWND hwnd{};
        std::wstring url;
        std::shared_ptr<SharedState> state = std::make_shared<SharedState>();
        std::thread worker;

        // All inputs are owned by the worker, not by the Engine wrapper.
        // This permits a bounded shutdown when a GPU driver stalls in Present:
        // the UI can release the wrapper without any use-after-free.
        static void Run(std::shared_ptr<SharedState> s,
                        HWND hwnd, std::wstring url) noexcept
        {
            struct Completion
            {
                std::shared_ptr<SharedState> s;
                ~Completion() { s->finished.store(true, std::memory_order_release); }
            } completion{s};
            const HRESULT co = CoInitializeEx(nullptr, COINIT_MULTITHREADED);
            const bool uninitializeCo = SUCCEEDED(co);
            bool startedMf = false;
            auto fail = [&](HRESULT hr)
            {
                s->error.store(static_cast<int>(hr));
                s->phase.store(-1);
            };
            try
            {
                if (FAILED(co) && co != RPC_E_CHANGED_MODE)
                {
                    fail(co);
                    return;
                }
                HRESULT hr = MFStartup(MF_VERSION);
                if (FAILED(hr)) { fail(hr); goto done; }
                startedMf = true;

                {
                    ComPtr<ID3D11Device> device;
                    ComPtr<ID3D11DeviceContext> context;
                    hr = CreateDevice(device, context);
                    // Do not silently switch to WARP/software and recreate the
                    // previous 800 MB CPU/video-memory problem.
                    if (FAILED(hr)) { fail(hr); goto done; }

                    UINT token = 0;
                    ComPtr<IMFDXGIDeviceManager> dxgiManager;
                    hr = MFCreateDXGIDeviceManager(&token, &dxgiManager);
                    if (FAILED(hr)) { fail(hr); goto done; }
                    hr = dxgiManager->ResetDevice(device.Get(), token);
                    if (FAILED(hr)) { fail(hr); goto done; }

                    ComPtr<IDXGIFactory2> factory;
                    hr = CreateDXGIFactory2(0, IID_PPV_ARGS(&factory));
                    if (FAILED(hr)) { fail(hr); goto done; }

                    RECT client{};
                    if (!GetClientRect(hwnd, &client)) { fail(E_HANDLE); goto done; }
                    UINT width = static_cast<UINT>(std::max<LONG>(2, client.right - client.left));
                    UINT height = static_cast<UINT>(std::max<LONG>(2, client.bottom - client.top));

                    DXGI_SWAP_CHAIN_DESC1 desc{};
                    desc.Width = width;
                    desc.Height = height;
                    desc.Format = DXGI_FORMAT_B8G8R8A8_UNORM;
                    desc.Stereo = FALSE;
                    desc.SampleDesc.Count = 1;
                    desc.BufferUsage = DXGI_USAGE_RENDER_TARGET_OUTPUT;
                    desc.BufferCount = 2;  // Explicit bounded compositor queue.
                    desc.Scaling = DXGI_SCALING_STRETCH;
                    desc.SwapEffect = DXGI_SWAP_EFFECT_FLIP_SEQUENTIAL;
                    desc.AlphaMode = DXGI_ALPHA_MODE_IGNORE;
                    ComPtr<IDXGISwapChain1> swap;
                    hr = factory->CreateSwapChainForHwnd(device.Get(), hwnd,
                        &desc, nullptr, nullptr, &swap);
                    if (FAILED(hr)) { fail(hr); goto done; }

                    ComPtr<IMFAttributes> attrs;
                    hr = MFCreateAttributes(&attrs, 3);
                    if (FAILED(hr)) { fail(hr); goto done; }
                    // Mandatory in IMFMediaEngine FRAME-SERVER mode.
                    // Without this attribute the engine can initialize in
                    // audio-only mode or fail, even when D3D11 is available.
                    hr = attrs->SetUINT32(MF_MEDIA_ENGINE_VIDEO_OUTPUT_FORMAT,
                        static_cast<UINT32>(DXGI_FORMAT_B8G8R8A8_UNORM));
                    if (FAILED(hr)) { fail(hr); goto done; }
                    hr = attrs->SetUnknown(MF_MEDIA_ENGINE_DXGI_MANAGER,
                        dxgiManager.Get());
                    if (FAILED(hr)) { fail(hr); goto done; }

                    // The frame-server session is created separately from the
                    // D3D11 device and swap chain. A long pause can release the
                    // decoder, while DWM retains the last presented GPU frame.
                    ComPtr<IMFMediaEngineClassFactory> classFactory;
                    hr = CoCreateInstance(CLSID_MFMediaEngineClassFactory,
                        nullptr, CLSCTX_INPROC_SERVER,
                        IID_PPV_ARGS(&classFactory));
                    if (FAILED(hr)) { fail(hr); goto done; }

                    ComPtr<IMFMediaEngine> player;
                    const auto openMediaSession = [&]() -> HRESULT
                    {
                        // Reset events before registering a new callback;
                        // generations make stale asynchronous events harmless.
                        s->loaded.store(false);
                        s->canPlay.store(false);
                        s->seeked.store(false);
                        s->ended.store(false);
                        int session = ++s->generation;
                        ComPtr<IMFMediaEngineNotify> notify;
                        notify.Attach(new (std::nothrow) Notify(s, session));
                        if (!notify) return E_OUTOFMEMORY;
                        HRESULT result = attrs->SetUnknown(
                            MF_MEDIA_ENGINE_CALLBACK, notify.Get());
                        if (FAILED(result)) return result;
                        result = classFactory->CreateInstance(
                            MF_MEDIA_ENGINE_REAL_TIME_MODE,
                            attrs.Get(), &player);
                        if (FAILED(result)) return result;
                        BSTR source = SysAllocString(url.c_str());
                        if (!source)
                        {
                            player->Shutdown();
                            player.Reset();
                            return E_OUTOFMEMORY;
                        }
                        result = player->SetSource(source);
                        SysFreeString(source);
                        if (FAILED(result))
                        {
                            player->Shutdown();
                            player.Reset();
                            return result;
                        }
                        s->decoderSleeping.store(0);
                        return S_OK;
                    };

                    hr = openMediaSession();
                    if (FAILED(hr)) { fail(hr); goto done; }
                    // 1 means a real hardware D3D11 device is bound to the
                    // Media Engine. Driver-level Video Decode (not just 3D)
                    // must still be checked in Task Manager.
                    s->gpu.store(1);

                    constexpr auto DeepPauseDelay = std::chrono::milliseconds(1800);
                    std::chrono::steady_clock::time_point pausedAt{};
                    std::chrono::steady_clock::time_point seekStarted{};
                    double frozenPosition = 0.0;
                    bool resumeNeedsSeek = false;
                    bool resumeSeekIssued = false;
                    bool begun = false;
                    bool lastPause = false;
                    bool lastMute = false;
                    bool lastLoop = false;
                    bool lastFill = true;
                    double lastVolume = -1;
                    double lastSpeed = -1;
                    LONG srcW = 0, srcH = 0;
                    // Metadata is static for local wallpaper files. Calling
                    // these Media Foundation COM getters on every 16ms
                    // frame-server tick needlessly contends with decoding.
                    bool metadataReady = false;
                    auto nextMetadataQuery = std::chrono::steady_clock::now();
                    auto nextPositionQuery = std::chrono::steady_clock::now();
                    // Derive polling from actual media presentation timestamps,
                    // not the app's preferred FPS. This reduces wakeups for
                    // 24 FPS wallpapers and improves sampling of 60 FPS clips
                    // without altering their decoded frames or colors.
                    LONGLONG lastPresentationTime = -1;
                    double sourceFrameIntervalMs = 0.0;
                    int activePollMs = 16;

                    while (!s->stop.load(std::memory_order_acquire))
                    {
                        if (s->phase.load() < 0) break;

                        const bool wantedMute = s->mute.load();
                        const bool wantedLoop = s->loop.load();
                        const bool wantedPause = s->pause.load();
                        const bool wantedFill = s->fill.load();
                        const double wantedVolume = s->volume.load();
                        const double wantedSpeed = s->speed.load();

                        // A paused video is a still image. DWM retains the
                        // last presented DXGI frame; after a short grace period
                        // release the source, decoder and its reference frames.
                        // The flip swap chain stays alive so the artwork does
                        // not disappear or change colors when frozen.
                        if (!player)
                        {
                            if (s->seekRequested.exchange(false))
                                frozenPosition = s->seekTo.load();

                            if (wantedPause)
                            {
                                s->phase.store(3);
                                std::this_thread::sleep_for(
                                    std::chrono::milliseconds(350));
                                continue;
                            }

                            hr = openMediaSession();
                            if (FAILED(hr)) { fail(hr); break; }

                            // Resume the *same frame position*, not the first
                            // frame of the loop. Seek completion is async;
                            // avoid presenting frame 0 before seeking.
                            resumeNeedsSeek = frozenPosition > 0.001;
                            resumeSeekIssued = false;
                            metadataReady = false;
                            nextMetadataQuery = std::chrono::steady_clock::now();
                            nextPositionQuery = nextMetadataQuery;
                            lastPresentationTime = -1;
                            sourceFrameIntervalMs = 0.0;
                            activePollMs = 16;
                            begun = false;
                            lastPause = false;
                            lastMute = !wantedMute;
                            lastLoop = !wantedLoop;
                            lastVolume = -1;
                            lastSpeed = -1;
                            s->phase.store(1);
                        }

                        if (s->loaded.load() && !metadataReady &&
                            std::chrono::steady_clock::now() >= nextMetadataQuery)
                        {
                            // A source may report metadata before its video
                            // dimensions settle. Retry at 500ms intervals,
                            // never 60 times a second during 4K decoding.
                            nextMetadataQuery =
                                std::chrono::steady_clock::now() +
                                std::chrono::milliseconds(500);
                            DWORD nativeW = 0, nativeH = 0;
                            if (SUCCEEDED(player->GetNativeVideoSize(
                                    &nativeW, &nativeH)) &&
                                nativeW > 0 && nativeH > 0)
                            {
                                srcW = static_cast<LONG>(nativeW);
                                srcH = static_cast<LONG>(nativeH);
                                s->width.store(srcW);
                                s->height.store(srcH);
                            }
                            const double duration = player->GetDuration();
                            if (std::isfinite(duration) && duration > 0)
                                s->duration.store(duration);
                            metadataReady = srcW > 0 && srcH > 0;
                        }

                        if (wantedMute != lastMute) player->SetMuted(wantedMute);
                        lastMute = wantedMute;
                        if (wantedVolume != lastVolume) player->SetVolume(wantedVolume);
                        lastVolume = wantedVolume;
                        if (wantedLoop != lastLoop) player->SetLoop(wantedLoop);
                        lastLoop = wantedLoop;
                        if (wantedSpeed != lastSpeed) player->SetPlaybackRate(wantedSpeed);
                        lastSpeed = wantedSpeed;

                        if (s->seekRequested.load() && resumeNeedsSeek)
                        {
                            frozenPosition = s->seekTo.load();
                            s->seekRequested.store(false);
                            resumeSeekIssued = false;
                            s->seeked.store(false);
                        }

                        if (s->canPlay.load() && !begun)
                        {
                            bool readyToPlay = true;
                            if (resumeNeedsSeek)
                            {
                                if (!resumeSeekIssued)
                                {
                                    hr = player->SetCurrentTime(frozenPosition);
                                    resumeSeekIssued = SUCCEEDED(hr);
                                    seekStarted = std::chrono::steady_clock::now();
                                }
                                readyToPlay = !resumeSeekIssued ||
                                    s->seeked.load() ||
                                    (std::chrono::steady_clock::now() -
                                        seekStarted > std::chrono::seconds(2));
                            }
                            if (readyToPlay)
                            {
                                resumeNeedsSeek = false;
                                begun = true;
                                player->Play();
                            }
                        }

                        if (begun && wantedPause != lastPause)
                        {
                            if (wantedPause)
                            {
                                player->Pause();
                                pausedAt = std::chrono::steady_clock::now();
                            }
                            else
                            {
                                player->Play();
                            }
                            lastPause = wantedPause;
                        }

                        if (s->seekRequested.load() && s->canPlay.load() &&
                            !resumeNeedsSeek && begun)
                        {
                            double position = s->seekTo.load();
                            s->seekRequested.store(false);
                            player->SetCurrentTime(position);
                        }

                        // IMFMediaEngine::SetLoop already loops the media.
                        // ENDED is not sent when loop is enabled; trying to
                        // seek and Play again is redundant and can thrash a
                        // five-second 4K wallpaper on every repetition.
                        s->ended.store(false);

                        if (begun && wantedPause && lastPause &&
                            s->frames.load() > 0 &&
                            std::chrono::steady_clock::now() - pausedAt >=
                                DeepPauseDelay)
                        {
                            double position = player->GetCurrentTime();
                            if (std::isfinite(position) && position >= 0)
                            {
                                frozenPosition = position;
                                s->position.store(position);
                            }
                            // Invalidate callback events before shutdown.
                            ++s->generation;
                            player->Shutdown();
                            player.Reset();
                            s->decoderSleeping.store(1);
                            s->phase.store(3);
                            begun = false;
                            // Keep swapchain contents, defer any resize until
                            // playback resumes so the frozen frame stays put.
                            std::this_thread::sleep_for(
                                std::chrono::milliseconds(350));
                            continue;
                        }

                        // Preserve the last full-quality GPU frame while
                        // paused. Resizing DXGI buffers during deep pause
                        // would discard it and show a black wallpaper.
                        RECT nowRect{};
                        if (!wantedPause && GetClientRect(hwnd, &nowRect))
                        {
                            UINT w = static_cast<UINT>(
                                std::max<LONG>(2, nowRect.right - nowRect.left));
                            UINT h = static_cast<UINT>(
                                std::max<LONG>(2, nowRect.bottom - nowRect.top));
                            if ((w != width || h != height) && IsWindow(hwnd))
                            {
                                hr = swap->ResizeBuffers(2, w, h,
                                    DXGI_FORMAT_B8G8R8A8_UNORM, 0);
                                if (SUCCEEDED(hr))
                                {
                                    width = w;
                                    height = h;
                                }
                                else if (hr == DXGI_ERROR_DEVICE_REMOVED ||
                                         hr == DXGI_ERROR_DEVICE_RESET)
                                {
                                    fail(hr);
                                    break;
                                }
                            }
                        }

                        // No CompositionTarget.Rendering, WPF bitmaps or
                        // expensive CPU readback. Present only decoded frames.
                        if (begun && s->loaded.load() && !wantedPause)
                        {
                            LONGLONG presentationTime = 0;
                            HRESULT tick = player->OnVideoStreamTick(&presentationTime);
                            if (tick == S_OK)
                            {
                                if (lastPresentationTime >= 0 &&
                                    presentationTime > lastPresentationTime)
                                {
                                    const double ms = static_cast<double>(
                                        presentationTime - lastPresentationTime) / 10000.0;
                                    // Ignore unusual seeks and tiny/huge PTS
                                    // deltas rather than letting them disturb
                                    // steady-state frame pacing.
                                    if (ms >= 10.0 && ms <= 100.0)
                                    {
                                        sourceFrameIntervalMs =
                                            sourceFrameIntervalMs <= 0.0 ? ms :
                                            sourceFrameIntervalMs * 0.75 + ms * 0.25;
                                        activePollMs = std::clamp(
                                            static_cast<int>(std::lround(
                                                sourceFrameIntervalMs * 0.5)),
                                            8, 20);
                                    }
                                }
                                lastPresentationTime = presentationTime;
                                ComPtr<ID3D11Texture2D> surface;
                                hr = swap->GetBuffer(0, IID_PPV_ARGS(&surface));
                                if (SUCCEEDED(hr))
                                {
                                    MFVideoNormalizedRect crop{0,0,1,1};
                                    RECT dst{0,0,static_cast<LONG>(width),
                                        static_cast<LONG>(height)};

                                    const bool fill = wantedFill;
                                    if (srcW > 0 && srcH > 0 && width > 0 && height > 0)
                                    {
                                        double aspect = static_cast<double>(srcW) / srcH;
                                        double target = static_cast<double>(width) / height;
                                        if (fill)
                                        {
                                            if (aspect > target)
                                            {
                                                float pad = static_cast<float>(
                                                    (1 - target / aspect) / 2);
                                                crop.left = pad;
                                                crop.right = 1 - pad;
                                            }
                                            else if (aspect < target)
                                            {
                                                float pad = static_cast<float>(
                                                    (1 - aspect / target) / 2);
                                                crop.top = pad;
                                                crop.bottom = 1 - pad;
                                            }
                                        }
                                        else
                                        {
                                            if (aspect > target)
                                            {
                                                LONG newHeight = static_cast<LONG>(width / aspect);
                                                dst.top = static_cast<LONG>((height - newHeight) / 2);
                                                dst.bottom = dst.top + newHeight;
                                            }
                                            else if (aspect < target)
                                            {
                                                LONG newWidth = static_cast<LONG>(height * aspect);
                                                dst.left = static_cast<LONG>((width - newWidth) / 2);
                                                dst.right = dst.left + newWidth;
                                            }
                                        }
                                    }
                                    MFARGB black{};
                                    hr = player->TransferVideoFrame(
                                        surface.Get(), &crop, &dst, &black);
                                    surface.Reset(); // Must release before ResizeBuffers.
                                    if (SUCCEEDED(hr))
                                    {
                                        HRESULT shown = swap->Present(1, 0);
                                        if (SUCCEEDED(shown))
                                            s->frames.fetch_add(1);
                                        else if (shown == DXGI_ERROR_DEVICE_REMOVED ||
                                                 shown == DXGI_ERROR_DEVICE_RESET)
                                        {
                                            fail(shown);
                                            break;
                                        }
                                    }
                                }
                            }
                        }

                        lastFill = wantedFill;
                        (void)lastFill;
                        if (begun)
                        {
                            // The UI reads playback position every 300ms.
                            // Avoid an expensive 60Hz COM query that doesn't
                            // help video presentation or image quality.
                            if (std::chrono::steady_clock::now() >= nextPositionQuery)
                            {
                                nextPositionQuery =
                                    std::chrono::steady_clock::now() +
                                    std::chrono::milliseconds(200);
                                double position = player->GetCurrentTime();
                                if (std::isfinite(position) && position >= 0)
                                    s->position.store(position);
                            }
                            s->phase.store(wantedPause ? 3 : 2);
                        }
                        else if (s->loaded.load())
                        {
                            s->phase.store(1);
                        }

                        // For 24 FPS clips sample near 20 ms, 30 FPS near
                        // 16 ms, and 60 FPS near 8 ms. Do not spin or change
                        // the media presentation clock. Pause is slower still.
                        std::this_thread::sleep_for(
                            std::chrono::milliseconds(
                                wantedPause ? 120 : activePollMs));
                    }

                    if (player)
                    {
                        player->Pause();
                        player->Shutdown();
                        player.Reset();
                    }
                }
            }
            catch (...)
            {
                fail(E_FAIL);
            }
        done:
            if (startedMf) MFShutdown();
            if (uninitializeCo) CoUninitialize();
        }
    };
}

extern "C"
{
    __declspec(dllexport) int __cdecl IntonacoVideo_Create(
        HWND window, const wchar_t* url, void** handle)
    {
        if (!handle || !url || !*url || !IsWindow(window)) return E_INVALIDARG;
        *handle = nullptr;
        try
        {
            auto engine = std::make_unique<Engine>();
            engine->hwnd = window;
            engine->url = url;
            // Copy the state, URL and target HWND into the thread. Never
            // capture the Engine object itself, which may be destroyed while
            // a faulty video driver is still processing its final Present.
            engine->worker = std::thread(
                [state = engine->state, hwnd = engine->hwnd, url = engine->url]()
                {
                    Engine::Run(state, hwnd, url);
                });
            *handle = engine.release();
            return S_OK;
        }
        catch (...) { return E_FAIL; }
    }

    __declspec(dllexport) void __cdecl IntonacoVideo_Configure(
        void* handle, int paused, int muted, double volume, double speed,
        int looping, int fill)
    {
        if (!handle) return;
        auto& s = *static_cast<Engine*>(handle)->state;
        s.pause.store(paused != 0);
        s.mute.store(muted != 0);
        s.volume.store(std::clamp(volume, 0.0, 1.0));
        s.speed.store(std::clamp(speed, 0.25, 2.0));
        s.loop.store(looping != 0);
        s.fill.store(fill != 0);
    }

    __declspec(dllexport) void __cdecl IntonacoVideo_Seek(
        void* handle, double timeSeconds)
    {
        if (!handle || !std::isfinite(timeSeconds)) return;
        auto& s = *static_cast<Engine*>(handle)->state;
        s.seekTo.store(std::max(0.0, timeSeconds));
        s.seekRequested.store(true);
    }

    __declspec(dllexport) int __cdecl IntonacoVideo_GetStats(
        void* handle, VideoStats* output)
    {
        if (!handle || !output) return E_POINTER;
        const auto& s = *static_cast<Engine*>(handle)->state;
        *output = {s.phase.load(), s.error.load(), s.gpu.load(),
            s.width.load(), s.height.load(), s.decoderSleeping.load(),
            s.duration.load(), s.position.load(), s.frames.load()};
        return S_OK;
    }

    __declspec(dllexport) void __cdecl IntonacoVideo_Destroy(void* handle)
    {
        if (!handle) return;
        std::unique_ptr<Engine> engine(static_cast<Engine*>(handle));
        engine->state->stop.store(true, std::memory_order_release);
        if (engine->worker.joinable())
        {
            // A hung graphics driver must not lock the Intonaco UI forever.
            // The worker owns all its state and can finish cleanup later.
            HANDLE threadHandle = static_cast<HANDLE>(engine->worker.native_handle());
            DWORD result = WaitForSingleObject(threadHandle, 1500);
            if (result == WAIT_OBJECT_0)
                engine->worker.join();
            else
                engine->worker.detach();
        }
    }
}
