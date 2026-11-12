// Intonaco Video: native Windows 10+ hardware-accelerated wallpaper renderer.
// Own implementation: Media Foundation MediaEngine -> Direct3D 11 swapchain.
// No FFmpeg/mpv/browser service, software frame copies, or per-frame WPF callbacks.
// ABI is C, to keep the existing UI independent of the C++ implementation.
#define WIN32_LEAN_AND_MEAN
#define NOMINMAX
#include <windows.h>
#include <mfapi.h>
#include <mfmediaengine.h>
#include <mfidl.h>
#include <mferror.h>
#include <d3d11.h>
#include <dxgi1_2.h>
#include <wrl/client.h>
#include <atomic>
#include <algorithm>
#include <cmath>
#include <cstdint>
#include <memory>
#include <mutex>
#include <string>
#include <thread>
#include <utility>
using Microsoft::WRL::ComPtr;

namespace {
constexpr int kStarting = 0, kReady = 1, kPlaying = 2, kPaused = 3,
              kStopped = 4, kFailed = -1;
struct SharedState {
    std::atomic<int> state{kStarting};
    std::atomic<bool> ended{false};
    std::atomic<bool> ready{false};
    std::atomic<long> error{0};
    std::atomic<int> videoWidth{0};
    std::atomic<int> videoHeight{0};
    std::atomic<std::int64_t> positionMs{0};
    std::atomic<std::int64_t> durationMs{0};
};
class MediaEvents final : public IMFMediaEngineNotify {
    std::atomic<ULONG> refs_{1};
    std::shared_ptr<SharedState> shared_;
public:
    explicit MediaEvents(std::shared_ptr<SharedState> state)
        : shared_(std::move(state)) {}
    HRESULT STDMETHODCALLTYPE QueryInterface(REFIID id, void** result) override {
        if (!result) return E_POINTER;
        *result = nullptr;
        if (id == __uuidof(IMFMediaEngineNotify) || id == IID_IUnknown) {
            *result = static_cast<IMFMediaEngineNotify*>(this);
            AddRef(); return S_OK;
        }
        return E_NOINTERFACE;
    }
    ULONG STDMETHODCALLTYPE AddRef() override { return ++refs_; }
    ULONG STDMETHODCALLTYPE Release() override {
        ULONG n = --refs_; if (!n) delete this; return n;
    }
    HRESULT STDMETHODCALLTYPE EventNotify(DWORD event, DWORD_PTR p1, DWORD) override {
        switch (event) {
        case MF_MEDIA_ENGINE_EVENT_LOADEDMETADATA:
        case MF_MEDIA_ENGINE_EVENT_CANPLAY:
            shared_->ready = true;
            break;
        case MF_MEDIA_ENGINE_EVENT_ENDED:
            shared_->ended = true;
            break;
        case MF_MEDIA_ENGINE_EVENT_ERROR:
            shared_->error = static_cast<long>(p1);
            shared_->state = kFailed;
            break;
        }
        return S_OK;
    }
};
struct Cropping {
    MFVideoNormalizedRect source{0.f,0.f,1.f,1.f};
    RECT destination{};
};
Cropping calculateCrop(int sourceW, int sourceH, int destW, int destH, bool cover) {
    Cropping r;
    r.destination = RECT{0, 0, std::max(1, destW), std::max(1, destH)};
    if (sourceW <= 0 || sourceH <= 0 || destW <= 0 || destH <= 0) return r;
    double videoAspect = static_cast<double>(sourceW) / sourceH;
    double outputAspect = static_cast<double>(destW) / destH;
    if (cover) {
        if (videoAspect > outputAspect) {
            double normalizedWidth = outputAspect / videoAspect;
            r.source.left = static_cast<float>((1 - normalizedWidth) / 2);
            r.source.right = static_cast<float>(1 - (1 - normalizedWidth) / 2);
        } else if (videoAspect < outputAspect) {
            double normalizedHeight = videoAspect / outputAspect;
            r.source.top = static_cast<float>((1 - normalizedHeight) / 2);
            r.source.bottom = static_cast<float>(1 - (1 - normalizedHeight) / 2);
        }
    } else {
        if (videoAspect > outputAspect) {
            int height = std::max(1, static_cast<int>(std::round(destW / videoAspect)));
            int top = (destH - height) / 2;
            r.destination = RECT{0, top, destW, top + height};
        } else if (videoAspect < outputAspect) {
            int width = std::max(1, static_cast<int>(std::round(destH * videoAspect)));
            int left = (destW - width) / 2;
            r.destination = RECT{left, 0, left + width, destH};
        }
    }
    return r;
}

class VideoEngine {
    HWND hwnd_;
    HANDLE wake_ = nullptr;
    std::thread worker_;
    std::mutex commandMutex_;
    std::wstring pendingFile_;
    bool requestedOpen_ = false;
    bool requestedPause_ = false;
    bool requestedCover_ = true;
    bool requestedLoop_ = true;
    double requestedSpeed_ = 1.0;
    double requestedVolume_ = 0.0;
    double pendingSeek_ = -1.0;
    bool pendingStop_ = false;
    std::atomic<bool> quitting_{false};
    std::shared_ptr<SharedState> state_ = std::make_shared<SharedState>();

    void fail(HRESULT hr) noexcept {
        state_->error = static_cast<long>(hr);
        state_->state = kFailed;
    }
    static bool ok(HRESULT hr) { return SUCCEEDED(hr); }
public:
    explicit VideoEngine(HWND hwnd) : hwnd_(hwnd) {
        wake_ = CreateEventW(nullptr, FALSE, FALSE, nullptr);
        if (wake_) worker_ = std::thread([this] { run(); });
        else fail(HRESULT_FROM_WIN32(GetLastError()));
    }
    ~VideoEngine() {
        quitting_ = true;
        if (wake_) SetEvent(wake_);
        if (worker_.joinable()) worker_.join();
        if (wake_) CloseHandle(wake_);
    }
    void open(const wchar_t* path) {
        std::lock_guard<std::mutex> lock(commandMutex_);
        pendingFile_ = path ? path : L"";
        requestedPause_ = false;
        requestedOpen_ = true;
        pendingStop_ = false;
        state_->ready = false;
        state_->state = kStarting;
        if (wake_) SetEvent(wake_);
    }
    void pause(bool paused) {
        std::lock_guard<std::mutex> lock(commandMutex_);
        requestedPause_ = paused;
        if (wake_) SetEvent(wake_);
    }
    void stop() {
        std::lock_guard<std::mutex> lock(commandMutex_);
        pendingStop_ = true;
        if (wake_) SetEvent(wake_);
    }
    void setOptions(bool cover, bool loop, double speed, double volume) {
        std::lock_guard<std::mutex> lock(commandMutex_);
        requestedCover_ = cover; requestedLoop_ = loop;
        requestedSpeed_ = std::clamp(speed, 0.25, 2.0);
        requestedVolume_ = std::clamp(volume, 0.0, 1.0);
        if (wake_) SetEvent(wake_);
    }
    void seek(double seconds) {
        std::lock_guard<std::mutex> lock(commandMutex_);
        pendingSeek_ = std::max(0.0, seconds);
        if (wake_) SetEvent(wake_);
    }
    SharedState& state() noexcept { return *state_; }
private:
    void run() {
        HRESULT hr = CoInitializeEx(nullptr, COINIT_MULTITHREADED);
        if (FAILED(hr)) { fail(hr); return; }
        hr = MFStartup(MF_VERSION);
        if (FAILED(hr)) { fail(hr); CoUninitialize(); return; }

        ComPtr<ID3D11Device> device;
        ComPtr<ID3D11DeviceContext> context;
        ComPtr<IDXGISwapChain1> swap;
        ComPtr<IMFDXGIDeviceManager> manager;
        ComPtr<IMFMediaEngine> engine;
        UINT resetToken = 0;
        D3D_FEATURE_LEVEL level{};
        hr = D3D11CreateDevice(nullptr, D3D_DRIVER_TYPE_HARDWARE, nullptr,
            D3D11_CREATE_DEVICE_BGRA_SUPPORT | D3D11_CREATE_DEVICE_VIDEO_SUPPORT,
            nullptr, 0, D3D11_SDK_VERSION, device.GetAddressOf(), &level,
            context.GetAddressOf());
        if (SUCCEEDED(hr)) hr = MFCreateDXGIDeviceManager(&resetToken, manager.GetAddressOf());
        if (SUCCEEDED(hr)) hr = manager->ResetDevice(device.Get(), resetToken);

        ComPtr<IDXGIDevice> dxgiDevice;
        ComPtr<IDXGIAdapter> adapter;
        ComPtr<IDXGIFactory2> factory;
        if (SUCCEEDED(hr)) hr = device.As(&dxgiDevice);
        if (SUCCEEDED(hr)) hr = dxgiDevice->GetAdapter(adapter.GetAddressOf());
        if (SUCCEEDED(hr)) hr = adapter->GetParent(IID_PPV_ARGS(factory.GetAddressOf()));

        RECT bounds{};
        if (SUCCEEDED(hr) && !GetClientRect(hwnd_, &bounds)) hr = E_HANDLE;
        int width = std::max(1L, bounds.right - bounds.left);
        int height = std::max(1L, bounds.bottom - bounds.top);
        DXGI_SWAP_CHAIN_DESC1 desc{};
        desc.Width = static_cast<UINT>(width);
        desc.Height = static_cast<UINT>(height);
        desc.Format = DXGI_FORMAT_B8G8R8A8_UNORM;
        desc.Stereo = FALSE;
        desc.SampleDesc.Count = 1;
        desc.BufferUsage = DXGI_USAGE_RENDER_TARGET_OUTPUT;
        desc.BufferCount = 2;
        desc.Scaling = DXGI_SCALING_STRETCH;
        desc.SwapEffect = DXGI_SWAP_EFFECT_FLIP_DISCARD;
        if (SUCCEEDED(hr)) hr = factory->CreateSwapChainForHwnd(
            device.Get(), hwnd_, &desc, nullptr, nullptr, swap.GetAddressOf());

        ComPtr<IMFAttributes> attributes;
        ComPtr<IMFMediaEngineClassFactory> mediaFactory;
        ComPtr<IMFMediaEngineNotify> notify;
        notify.Attach(new MediaEvents(state_));
        if (SUCCEEDED(hr)) hr = MFCreateAttributes(attributes.GetAddressOf(), 4);
        if (SUCCEEDED(hr)) hr = attributes->SetUnknown(MF_MEDIA_ENGINE_DXGI_MANAGER, manager.Get());
        if (SUCCEEDED(hr)) hr = attributes->SetUnknown(MF_MEDIA_ENGINE_CALLBACK, notify.Get());
        if (SUCCEEDED(hr)) hr = attributes->SetUINT32(
            MF_MEDIA_ENGINE_VIDEO_OUTPUT_FORMAT, DXGI_FORMAT_B8G8R8A8_UNORM);
        if (SUCCEEDED(hr)) hr = CoCreateInstance(CLSID_MFMediaEngineClassFactory,
            nullptr, CLSCTX_INPROC_SERVER, IID_PPV_ARGS(mediaFactory.GetAddressOf()));
        if (SUCCEEDED(hr)) hr = mediaFactory->CreateInstance(
            0, attributes.Get(), engine.GetAddressOf());
        if (FAILED(hr)) {
            fail(hr);
            swap.Reset(); context.Reset(); device.Reset();
            MFShutdown(); CoUninitialize(); return;
        }
        engine->SetMuted(TRUE);
        engine->SetVolume(0);

        bool hasSource = false, playing = false, lastPause = false;
        bool cover = true, loop = true;
        double speed = 1.0, volume = 0.0;
        bool settingsDirty = true;
        for (;;) {
            if (quitting_) break;
            bool open = false, stop = false, pause = false;
            double seekTime = -1.0;
            std::wstring file;
            {
                std::lock_guard<std::mutex> lock(commandMutex_);
                open = std::exchange(requestedOpen_, false);
                if (open) file = pendingFile_;
                stop = std::exchange(pendingStop_, false);
                pause = requestedPause_;
                if (cover != requestedCover_ || loop != requestedLoop_ ||
                    speed != requestedSpeed_ || volume != requestedVolume_) settingsDirty = true;
                cover = requestedCover_; loop = requestedLoop_;
                speed = requestedSpeed_; volume = requestedVolume_;
                seekTime = std::exchange(pendingSeek_, -1.0);
            }
            if (stop || open) {
                engine->Pause();
                engine->SetSource(nullptr);
                hasSource = false; playing = false;
                state_->ready = false;
                state_->ended = false;
                state_->error = 0;
                state_->durationMs = 0; state_->positionMs = 0;
                state_->videoWidth = 0; state_->videoHeight = 0;
                state_->state = kStopped;
            }
            if (open && !file.empty()) {
                BSTR src = SysAllocString(file.c_str());
                HRESULT openHr = src ? engine->SetSource(src) : E_OUTOFMEMORY;
                if (src) SysFreeString(src);
                if (SUCCEEDED(openHr)) {
                    hasSource = true;
                    state_->state = kStarting;
                } else fail(openHr);
            }
            if (hasSource && state_->error.load() != 0) {
                engine->Pause();
                engine->SetSource(nullptr);
                hasSource = false;
                playing = false;
                state_->state = kFailed;
            }
            if (hasSource) {
                if (settingsDirty) {
                    engine->SetPlaybackRate(speed);
                    engine->SetMuted(volume <= 0.0 ? TRUE : FALSE);
                    engine->SetVolume(volume);
                    settingsDirty = false;
                }
                if (state_->ready) {
                    DWORD w=0,h=0;
                    if (SUCCEEDED(engine->GetNativeVideoSize(&w,&h))) {
                        state_->videoWidth = static_cast<int>(w);
                        state_->videoHeight = static_cast<int>(h);
                    }
                    double duration = engine->GetDuration();
                    if (std::isfinite(duration))
                        state_->durationMs = static_cast<std::int64_t>(duration*1000.0);
                    if (seekTime >= 0) engine->SetCurrentTime(seekTime);
                    if (state_->ended.exchange(false)) {
                        if (loop) {
                            engine->SetCurrentTime(0);
                            if (!pause) engine->Play();
                        } else {
                            pause = true;
                        }
                    }
                    if (pause != lastPause || !playing) {
                        HRESULT playbackHr = pause ? engine->Pause() : engine->Play();
                        if (SUCCEEDED(playbackHr)) {
                            state_->state = pause ? kPaused : kPlaying;
                            playing = true;
                        } else fail(playbackHr);
                        lastPause = pause;
                    }
                    if (!pause && swap && SUCCEEDED(hr)) {
                        LONGLONG presentationTime = 0;
                        if (engine->OnVideoStreamTick(&presentationTime) == S_OK) {
                            RECT now{};
                            if (GetClientRect(hwnd_, &now)) {
                                int newWidth = std::max(1L, now.right-now.left);
                                int newHeight = std::max(1L, now.bottom-now.top);
                                if (newWidth != width || newHeight != height) {
                                    context->ClearState();
                                    HRESULT resizeHr = swap->ResizeBuffers(
                                        0, newWidth, newHeight, DXGI_FORMAT_UNKNOWN, 0);
                                    if (SUCCEEDED(resizeHr)) {
                                        width = newWidth; height = newHeight;
                                    }
                                }
                            }
                            ComPtr<ID3D11Texture2D> target;
                            HRESULT drawHr = swap->GetBuffer(0, IID_PPV_ARGS(target.GetAddressOf()));
                            if (SUCCEEDED(drawHr)) {
                                const auto crop = calculateCrop(state_->videoWidth,
                                    state_->videoHeight, width, height, cover);
                                MFARGB black{0,0,0,255};
                                drawHr = engine->TransferVideoFrame(
                                    target.Get(), &crop.source, &crop.destination, &black);
                                if (SUCCEEDED(drawHr)) swap->Present(1,0);
                            }
                            if (FAILED(drawHr)) fail(drawHr);
                        }
                    }
                    double pos = engine->GetCurrentTime();
                    if (std::isfinite(pos) && pos >= 0)
                        state_->positionMs = static_cast<std::int64_t>(pos*1000.0);
                }
            }
            DWORD sleepMs = hasSource && !pause ? 15 : 150;
            WaitForSingleObject(wake_, sleepMs);
        }
        engine->Pause();
        engine->SetSource(nullptr);
        engine->Shutdown();
        engine.Reset();
        swap.Reset(); context.Reset(); manager.Reset(); device.Reset();
        MFShutdown();
        CoUninitialize();
    }
};
} // namespace

extern "C" {
__declspec(dllexport) void* __cdecl iv_create(HWND hwnd) {
    try { return IsWindow(hwnd) ? new VideoEngine(hwnd) : nullptr; }
    catch (...) { return nullptr; }
}
__declspec(dllexport) void __cdecl iv_destroy(void* p) {
    delete static_cast<VideoEngine*>(p);
}
__declspec(dllexport) void __cdecl iv_open(void* p, const wchar_t* path) {
    if (p) static_cast<VideoEngine*>(p)->open(path);
}
__declspec(dllexport) void __cdecl iv_pause(void* p, int paused) {
    if (p) static_cast<VideoEngine*>(p)->pause(paused != 0);
}
__declspec(dllexport) void __cdecl iv_stop(void* p) {
    if (p) static_cast<VideoEngine*>(p)->stop();
}
__declspec(dllexport) void __cdecl iv_options(
    void* p, int cover, int loop, double speed, double volume) {
    if (p) static_cast<VideoEngine*>(p)->setOptions(
        cover != 0, loop != 0, speed, volume);
}
__declspec(dllexport) void __cdecl iv_seek(void* p, double seconds) {
    if (p) static_cast<VideoEngine*>(p)->seek(seconds);
}
__declspec(dllexport) int __cdecl iv_state(void* p) {
    return p ? static_cast<VideoEngine*>(p)->state().state.load() : kFailed;
}
__declspec(dllexport) long __cdecl iv_error(void* p) {
    return p ? static_cast<VideoEngine*>(p)->state().error.load() : E_POINTER;
}
__declspec(dllexport) int __cdecl iv_width(void* p) {
    return p ? static_cast<VideoEngine*>(p)->state().videoWidth.load() : 0;
}
__declspec(dllexport) int __cdecl iv_height(void* p) {
    return p ? static_cast<VideoEngine*>(p)->state().videoHeight.load() : 0;
}
__declspec(dllexport) std::int64_t __cdecl iv_position_ms(void* p) {
    return p ? static_cast<VideoEngine*>(p)->state().positionMs.load() : 0;
}
__declspec(dllexport) std::int64_t __cdecl iv_duration_ms(void* p) {
    return p ? static_cast<VideoEngine*>(p)->state().durationMs.load() : 0;
}
}
