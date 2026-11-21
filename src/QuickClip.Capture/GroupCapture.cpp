#include "GroupCapture.h"
#include "DiscordDwmHost.h"

#include <d3d11_4.h>
#include <d3dcompiler.h>
#include <dxgi1_4.h>
#include <wrl/client.h>
#include <windows.graphics.capture.interop.h>
#include <windows.graphics.directx.direct3d11.interop.h>
#include <winrt/Windows.Foundation.h>
#include <winrt/Windows.Graphics.Capture.h>
#include <winrt/Windows.Graphics.DirectX.Direct3D11.h>
extern "C" {
#include <libavutil/hwcontext.h>
#include <libavutil/hwcontext_d3d11va.h>
#include <libavutil/mem.h>
}
#include <algorithm>
#include <array>
#include <atomic>
#include <cmath>
#include <memory>
#include <stdexcept>
#include <string>

using Microsoft::WRL::ComPtr;
namespace wg = winrt::Windows::Graphics;
namespace wc = winrt::Windows::Graphics::Capture;
namespace wd = winrt::Windows::Graphics::DirectX::Direct3D11;
using winrt::check_hresult;

namespace qc {
namespace {
constexpr int MaxWindows = 4;
constexpr int MaxDimension = 4096;
constexpr uint64_t MaxTotalPixels = 16000000;
using Flags = std::shared_ptr<std::atomic_bool>;

bool OwnerValid(const QcGameWindow& owner, HANDLE process)
{
    if (!owner.handle || !IsWindow(owner.handle) || !process ||
        WaitForSingleObject(process, 0) != WAIT_TIMEOUT) return false;
    DWORD pid = 0;
    if (GetWindowThreadProcessId(owner.handle, &pid) != owner.thread || pid != owner.process) return false;
    FILETIME created{}, exited{}, kernel{}, user{};
    if (!GetProcessTimes(process, &created, &exited, &kernel, &user)) return false;
    return ((static_cast<uint64_t>(created.dwHighDateTime) << 32) | created.dwLowDateTime) ==
        static_cast<uint64_t>(owner.started);
}

struct Pool {
    ComPtr<ID3D11Device> device;
    int width = 0, height = 0, allocated = 0;
};
AVBufferRef* Surface(void* opaque, size_t)
{
    auto& p = *static_cast<Pool*>(opaque);
    if (p.allocated >= 16) return nullptr;
    auto* descriptor = static_cast<AVD3D11FrameDescriptor*>(av_mallocz(sizeof(AVD3D11FrameDescriptor)));
    if (!descriptor) return nullptr;
    D3D11_TEXTURE2D_DESC d{};
    d.Width = p.width; d.Height = p.height;
    d.MipLevels = d.ArraySize = d.SampleDesc.Count = 1;
    d.Format = DXGI_FORMAT_NV12;
    d.Usage = D3D11_USAGE_DEFAULT;
    d.BindFlags = D3D11_BIND_RENDER_TARGET;
    if (FAILED(p.device->CreateTexture2D(&d, nullptr, &descriptor->texture))) {
        av_free(descriptor); return nullptr;
    }
    auto* ref = av_buffer_create(reinterpret_cast<uint8_t*>(descriptor), sizeof(*descriptor),
        [](void*, uint8_t* bytes) {
            auto* ptr = reinterpret_cast<AVD3D11FrameDescriptor*>(bytes);
            ptr->texture->Release();
            av_free(ptr);
        }, nullptr, 0);
    if (!ref) { descriptor->texture->Release(); av_free(descriptor); return nullptr; }
    ++p.allocated;
    return ref;
}

struct Source {
    QcGameWindow identity{};
    HANDLE process = nullptr;
    std::unique_ptr<DiscordDwmHost> mirror;
    wc::GraphicsCaptureItem item{nullptr};
    wc::Direct3D11CaptureFramePool pool{nullptr};
    wc::GraphicsCaptureSession session{nullptr};
    wg::SizeInt32 size{};
    ComPtr<ID3D11Texture2D> owned;
    ComPtr<ID3D11ShaderResourceView> view;
    Flags closed = std::make_shared<std::atomic_bool>(false);
    winrt::event_token closedToken{};
    bool subscribed = false, have = false, wasMinimized = false;
    int64_t freshAfter = 0;
    ~Source()
    {
        if (subscribed && item) item.Closed(closedToken);
        if (session) session.Close();
        if (pool) pool.Close();
        mirror.reset(); // WGC must close before the cloaked HWND is destroyed.
        if (process) CloseHandle(process);
    }
    bool Valid() const { return OwnerValid(identity, process) && !closed->load(); }
};

int64_t Qpc100ns()
{
    LARGE_INTEGER q{}, frequency{};
    QueryPerformanceCounter(&q); QueryPerformanceFrequency(&frequency);
    return static_cast<int64_t>(static_cast<long double>(q.QuadPart) * 10000000 / frequency.QuadPart);
}

void Verify(bool condition, const char* message)
{
    if (!condition) throw std::runtime_error(message);
}
} // namespace

struct GroupCapture::Impl {
    ComPtr<ID3D11Device> device;
    ComPtr<ID3D11DeviceContext> context;
    ComPtr<ID3D11Multithread> multithread;
    ComPtr<ID3D11VideoDevice> videoDevice;
    ComPtr<ID3D11VideoContext1> videoContext;
    ComPtr<ID3D11VideoProcessorEnumerator> videoEnumerator;
    ComPtr<ID3D11VideoProcessor> processor;
    ComPtr<ID3D11VideoProcessorInputView> canvasInput;
    ComPtr<ID3D11Texture2D> canvas;
    ComPtr<ID3D11RenderTargetView> canvasView;
    ComPtr<ID3D11VertexShader> vertex;
    ComPtr<ID3D11PixelShader> pixel;
    ComPtr<ID3D11SamplerState> sampler;
    wd::IDirect3DDevice winrtDevice{nullptr};
    winrt::com_ptr<IGraphicsCaptureItemInterop> interop;
    AVBufferRef* frames = nullptr;
    int width = 0, height = 0;
    bool cloakedSources = false;
    std::vector<std::unique_ptr<Source>> sources;

    ~Impl() { sources.clear(); av_buffer_unref(&frames); }

    void Open(AVBufferRef* hwDevice, int w, int h)
    {
        Verify(hwDevice && w >= 64 && h >= 64 && w <= MaxDimension && h <= MaxDimension &&
            !(w & 7) && !(h & 1), "Invalid fixed companion canvas");
        Verify(wc::GraphicsCaptureSession::IsSupported(), "Windows Graphics Capture unsupported");
        auto* data = reinterpret_cast<AVHWDeviceContext*>(hwDevice->data);
        auto* d3d = reinterpret_cast<AVD3D11VADeviceContext*>(data->hwctx);
        device = d3d->device; context = d3d->device_context;
        Verify(device && context, "Companion GPU device unavailable");
        if (SUCCEEDED(context->QueryInterface(IID_PPV_ARGS(&multithread))))
            multithread->SetMultithreadProtected(TRUE);
        ComPtr<IDXGIDevice> dxgi;
        check_hresult(device.As(&dxgi));
        winrt::com_ptr<IInspectable> inspect;
        check_hresult(CreateDirect3D11DeviceFromDXGIDevice(dxgi.Get(), inspect.put()));
        winrtDevice = inspect.as<wd::IDirect3DDevice>();
        interop = winrt::get_activation_factory<wc::GraphicsCaptureItem, IGraphicsCaptureItemInterop>();
        width=w; height=h;
        static const char shader[] = R"(
Texture2D<float4> img : register(t0);
SamplerState linearSampler : register(s0);
struct V { float4 pos:SV_Position; float2 uv:TEXCOORD0; };
V VS(uint id:SV_VertexID) {
 V v; v.uv=float2((id<<1)&2,id&2); v.pos=float4(v.uv*float2(2,-2)+float2(-1,1),0,1); return v;
}
float4 PS(V v):SV_Target { return float4(img.Sample(linearSampler,v.uv).rgb,1); }
)";
        ComPtr<ID3DBlob> vs,ps;
        check_hresult(D3DCompile(shader,sizeof shader-1,nullptr,nullptr,nullptr,"VS","vs_4_0",0,0,&vs,nullptr));
        check_hresult(D3DCompile(shader,sizeof shader-1,nullptr,nullptr,nullptr,"PS","ps_4_0",0,0,&ps,nullptr));
        check_hresult(device->CreateVertexShader(vs->GetBufferPointer(),vs->GetBufferSize(),nullptr,&vertex));
        check_hresult(device->CreatePixelShader(ps->GetBufferPointer(),ps->GetBufferSize(),nullptr,&pixel));
        D3D11_SAMPLER_DESC samplerDesc{};
        samplerDesc.Filter=D3D11_FILTER_MIN_MAG_MIP_LINEAR;
        samplerDesc.AddressU=samplerDesc.AddressV=samplerDesc.AddressW=D3D11_TEXTURE_ADDRESS_CLAMP;
        samplerDesc.MaxLOD=D3D11_FLOAT32_MAX;
        check_hresult(device->CreateSamplerState(&samplerDesc,&sampler));
        D3D11_TEXTURE2D_DESC tex{};
        tex.Width=w; tex.Height=h; tex.MipLevels=tex.ArraySize=tex.SampleDesc.Count=1;
        tex.Format=DXGI_FORMAT_B8G8R8A8_UNORM; tex.BindFlags=D3D11_BIND_RENDER_TARGET;
        check_hresult(device->CreateTexture2D(&tex,nullptr,&canvas));
        check_hresult(device->CreateRenderTargetView(canvas.Get(),nullptr,&canvasView));
        check_hresult(device.As(&videoDevice));
        check_hresult(context.As(&videoContext));
        D3D11_VIDEO_PROCESSOR_CONTENT_DESC desc{};
        desc.InputFrameFormat=D3D11_VIDEO_FRAME_FORMAT_PROGRESSIVE;
        desc.InputWidth=w; desc.InputHeight=h; desc.OutputWidth=w; desc.OutputHeight=h;
        desc.InputFrameRate=desc.OutputFrameRate={60,1};desc.Usage=D3D11_VIDEO_USAGE_PLAYBACK_NORMAL;
        check_hresult(videoDevice->CreateVideoProcessorEnumerator(&desc,&videoEnumerator));
        check_hresult(videoDevice->CreateVideoProcessor(videoEnumerator.Get(),0,&processor));
        videoContext->VideoProcessorSetStreamColorSpace1(processor.Get(),0,DXGI_COLOR_SPACE_RGB_FULL_G22_NONE_P709);
        videoContext->VideoProcessorSetOutputColorSpace1(processor.Get(),DXGI_COLOR_SPACE_YCBCR_STUDIO_G22_LEFT_P709);
        videoContext->VideoProcessorSetStreamAutoProcessingMode(processor.Get(),0,FALSE);
        RECT rect{0,0,w,h};
        videoContext->VideoProcessorSetOutputTargetRect(processor.Get(),TRUE,&rect);
        videoContext->VideoProcessorSetStreamSourceRect(processor.Get(),0,TRUE,&rect);
        videoContext->VideoProcessorSetStreamDestRect(processor.Get(),0,TRUE,&rect);
        D3D11_VIDEO_PROCESSOR_INPUT_VIEW_DESC iv{};
        iv.ViewDimension=D3D11_VPIV_DIMENSION_TEXTURE2D;
        check_hresult(videoDevice->CreateVideoProcessorInputView(canvas.Get(),videoEnumerator.Get(),&iv,&canvasInput));
        frames = av_hwframe_ctx_alloc(hwDevice);
        Verify(frames!=nullptr,"Companion hardware frame pool allocation failed");
        auto* f=reinterpret_cast<AVHWFramesContext*>(frames->data);
        f->format=AV_PIX_FMT_D3D11;f->sw_format=AV_PIX_FMT_NV12;f->width=w;f->height=h;
        auto* pool=new Pool();pool->device=device;pool->width=w;pool->height=h;
        f->pool=av_buffer_pool_init2(sizeof(AVD3D11FrameDescriptor),pool,Surface,
            [](void* ptr){ delete static_cast<Pool*>(ptr); });
        Verify(f->pool!=nullptr,"Companion hardware frame buffer pool allocation failed");
        static_cast<AVD3D11VAFramesContext*>(f->hwctx)->BindFlags=D3D11_BIND_RENDER_TARGET;
        Verify(av_hwframe_ctx_init(frames)>=0,"Companion hardware frames unavailable");
    }

    std::unique_ptr<Source> Add(const QcGameWindow& target)
    {
        auto source=std::make_unique<Source>();
        source->identity=target;
        source->process=OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION|SYNCHRONIZE,FALSE,target.process);
        Verify(OwnerValid(target,source->process),"Companion source changed before capture");
        HWND captureWindow = target.handle;
        if (cloakedSources) {
            RECT sourceBounds{};
            Verify(GetWindowRect(target.handle,&sourceBounds)!=FALSE,
                "Companion mirror source bounds unavailable");
            const int sourceWidth=sourceBounds.right-sourceBounds.left;
            const int sourceHeight=sourceBounds.bottom-sourceBounds.top;
            Verify(sourceWidth >= 64 && sourceHeight >= 64 &&
                sourceWidth <= MaxDimension && sourceHeight <= MaxDimension,
                "Companion mirror source dimensions outside capture budget");
            source->mirror=std::make_unique<DiscordDwmHost>();
            std::string error;
            Verify(source->mirror->Open(target,sourceWidth,sourceHeight,error),error.c_str());
            captureWindow=source->mirror->CaptureTarget().handle;
        }
        check_hresult(interop->CreateForWindow(captureWindow,winrt::guid_of<wc::GraphicsCaptureItem>(),winrt::put_abi(source->item)));
        source->size=source->item.Size();
        Verify(source->size.Width>0 && source->size.Height>0 && source->size.Width<=MaxDimension &&
            source->size.Height<=MaxDimension,"Companion window dimensions unavailable or excessive");
        auto closed=source->closed;
        source->closedToken=source->item.Closed([closed](auto const&,auto const&){closed->store(true);});
        source->subscribed=true;
        source->pool=wc::Direct3D11CaptureFramePool::CreateFreeThreaded(winrtDevice,
            wg::DirectX::DirectXPixelFormat::B8G8R8A8UIntNormalized,2,source->size);
        source->session=source->pool.CreateCaptureSession(source->item);
        source->session.IsCursorCaptureEnabled(false);
        source->freshAfter=Qpc100ns();
        source->session.StartCapture();
        Verify(source->Valid(),"Companion source closed during capture initialization");
        return source;
    }

    void Update(const std::vector<QcGameWindow>& targets)
    {
        Verify(!targets.empty() && targets.size()<=MaxWindows,"Companion window count invalid");
        uint64_t pixels=0, boundsPixels=0;
        // Estimate sizes before WGC pool allocation; checked again against
        // the actual frame sizes after acquisition.
        for (const auto& target:targets) {
            RECT bounds{};
            Verify(GetWindowRect(target.handle,&bounds)!=FALSE,"Companion window bounds unavailable");
            int64_t w=static_cast<int64_t>(bounds.right)-bounds.left;
            int64_t h=static_cast<int64_t>(bounds.bottom)-bounds.top;
            Verify(w>0 && h>0 && w<=MaxDimension && h<=MaxDimension,
                "Companion window geometry exceeds capture budget");
            boundsPixels+=static_cast<uint64_t>(w*h);
            Verify(boundsPixels<=MaxTotalPixels,"Companion window group exceeds capture budget");
        }
        for(const auto& target:targets) {
            Verify(target.handle && target.thread && target.process && target.started>0,
                "Companion source identity is incomplete");
            Verify(std::count_if(targets.begin(),targets.end(),[&](const QcGameWindow& other){
                return other.handle==target.handle;})==1,"Duplicate companion window");
        }
        // Allocate newly discovered sources first; failure leaves the prior set untouched.
        std::vector<std::unique_ptr<Source>> pending;
        for (const auto& t:targets) {
            auto old=std::find_if(sources.begin(),sources.end(),[&](const auto& s){
                return s->identity.handle==t.handle;
            });
            if(old!=sources.end()) {
                Verify((*old)->identity.thread==t.thread && (*old)->identity.process==t.process &&
                    (*old)->identity.started==t.started && (*old)->Valid(),"Companion source ownership changed");
            } else pending.push_back(Add(t));
        }
        // Validate the complete requested source budget before moving ownership.
        // Rejected updates must not partly erase the existing capture group.
        for (const auto& t:targets) {
            auto old=std::find_if(sources.begin(),sources.end(),[&](const auto& src){
                return src && src->identity.handle==t.handle;
            });
            auto newSource=std::find_if(pending.begin(),pending.end(),[&](const auto& src){
                return src && src->identity.handle==t.handle;
            });
            auto* src=old!=sources.end()?old->get():newSource!=pending.end()?newSource->get():nullptr;
            Verify(src!=nullptr,"Companion source missing during update");
            pixels+=static_cast<uint64_t>(src->size.Width)*src->size.Height;
            Verify(pixels<=MaxTotalPixels,"Companion windows exceed GPU capture budget");
        }
        std::vector<std::unique_ptr<Source>> next;
        for (const auto& t:targets) {
            auto old=std::find_if(sources.begin(),sources.end(),[&](const auto& s){
                return s && s->identity.handle==t.handle;
            });
            if(old!=sources.end()) next.push_back(std::move(*old));
            else {
                auto added=std::find_if(pending.begin(),pending.end(),[&](const auto& s){return s && s->identity.handle==t.handle;});
                Verify(added!=pending.end(),"Companion selection missing");
                next.push_back(std::move(*added));
            }
        }
        sources=std::move(next);
    }

    void Render(AVFrame* frame)
    {
        Verify(!sources.empty(),"No authorized companion windows");
        const auto now=Qpc100ns();
        float black[4]={0,0,0,1};
        context->ClearRenderTargetView(canvasView.Get(),black);
        context->OMSetRenderTargets(1,canvasView.GetAddressOf(),nullptr);
        context->IASetPrimitiveTopology(D3D11_PRIMITIVE_TOPOLOGY_TRIANGLELIST);
        context->VSSetShader(vertex.Get(),nullptr,0);
        context->PSSetShader(pixel.Get(),nullptr,0);
        context->PSSetSamplers(0,1,sampler.GetAddressOf());
        unsigned live=0;
        for (size_t i=0;i<sources.size();i++) {
            auto& s=*sources[i];
            if(!s.Valid()) { s.have=false;continue; }
            ++live;
            bool minimized=IsIconic(s.identity.handle)!=FALSE ||
                IsWindowVisible(s.identity.handle)==FALSE;
            // A minimized pop-out must not revoke the rest of the group if
            // DWM temporarily stops reporting its source thumbnail geometry.
            if(!minimized && s.mirror) {
                std::string error;
                Verify(s.mirror->Refresh(error), error.c_str());
            }
            if(minimized!=s.wasMinimized) { s.have=false; s.wasMinimized=minimized; s.freshAfter=now; }
            for(int drain=0;drain<4;drain++) {
                auto incoming=s.pool.TryGetNextFrame();
                if(!incoming) break;
                auto sz=incoming.ContentSize();
                if(minimized||sz.Width<1||sz.Height<1||
                    sz.Width>MaxDimension||sz.Height>MaxDimension||
                    incoming.SystemRelativeTime().count()<s.freshAfter) continue;
                if(sz.Width!=s.size.Width||sz.Height!=s.size.Height) {
                    incoming.Close();s.size=sz;
                    s.pool.Recreate(winrtDevice,wg::DirectX::DirectXPixelFormat::B8G8R8A8UIntNormalized,2,sz);
                    s.owned.Reset();s.view.Reset();s.have=false;
                    break;
                }
                auto surface=incoming.Surface().as<::Windows::Graphics::DirectX::Direct3D11::IDirect3DDxgiInterfaceAccess>();
                ComPtr<ID3D11Texture2D> tex;
                check_hresult(surface->GetInterface(IID_PPV_ARGS(&tex)));
                D3D11_TEXTURE2D_DESC td{};tex->GetDesc(&td);
                if(td.Width<static_cast<UINT>(sz.Width)||td.Height<static_cast<UINT>(sz.Height)) continue;
                if(!s.owned) {
                    D3D11_TEXTURE2D_DESC desc{};
                    desc.Width=sz.Width;desc.Height=sz.Height;
                    desc.MipLevels=desc.ArraySize=desc.SampleDesc.Count=1;
                    desc.Format=DXGI_FORMAT_B8G8R8A8_UNORM;desc.BindFlags=D3D11_BIND_SHADER_RESOURCE;
                    check_hresult(device->CreateTexture2D(&desc,nullptr,&s.owned));
                    check_hresult(device->CreateShaderResourceView(s.owned.Get(),nullptr,&s.view));
                }
                D3D11_BOX box{0,0,0,static_cast<UINT>(sz.Width),static_cast<UINT>(sz.Height),1};
                context->CopySubresourceRegion(s.owned.Get(),0,0,0,0,tex.Get(),0,&box);
                s.have=true;
            }
            if(!s.have||minimized||!s.Valid()) continue;
            const int n=static_cast<int>(sources.size());
            RECT tile=i==0?(n==1?RECT{0,0,width,height}:RECT{0,0,width*7/10,height}):
                RECT{width*7/10,height*static_cast<int>(i-1)/(n-1),
                     width,height*static_cast<int>(i)/(n-1)};
            double scale=std::min(static_cast<double>(tile.right-tile.left)/s.size.Width,
                                  static_cast<double>(tile.bottom-tile.top)/s.size.Height);
            int w=std::max(2,(static_cast<int>(s.size.Width*scale)&~1));
            int h=std::max(2,(static_cast<int>(s.size.Height*scale)&~1));
            const int x=tile.left+(tile.right-tile.left-w)/2;
            const int y=tile.top+(tile.bottom-tile.top-h)/2;
            D3D11_VIEWPORT viewport{static_cast<float>(x),static_cast<float>(y),
                static_cast<float>(w),static_cast<float>(h),0,1};
            context->RSSetViewports(1,&viewport);
            context->PSSetShaderResources(0,1,s.view.GetAddressOf());
            context->Draw(3,0);
            ID3D11ShaderResourceView* empty=nullptr;
            context->PSSetShaderResources(0,1,&empty);
        }
        context->OMSetRenderTargets(0,nullptr,nullptr);
        Verify(live>0,"Companion windows closed or revoked");
        Verify(av_hwframe_get_buffer(frames,frame,0)>=0,"Companion frame allocation failed");
        ComPtr<ID3D11VideoProcessorOutputView> output;
        D3D11_VIDEO_PROCESSOR_OUTPUT_VIEW_DESC desc{};
        desc.ViewDimension=D3D11_VPOV_DIMENSION_TEXTURE2D;
        check_hresult(videoDevice->CreateVideoProcessorOutputView(
            reinterpret_cast<ID3D11Texture2D*>(frame->data[0]),videoEnumerator.Get(),&desc,&output));
        D3D11_VIDEO_PROCESSOR_STREAM stream{};
        stream.Enable=TRUE;stream.pInputSurface=canvasInput.Get();
        check_hresult(videoContext->VideoProcessorBlt(processor.Get(),output.Get(),0,1,&stream));
    }
};

void ClearWinrtFactoryCache() noexcept { winrt::clear_factory_cache(); }

GroupCapture::GroupCapture(): impl_(std::make_unique<Impl>()) {}
GroupCapture::~GroupCapture() = default;
void GroupCapture::Close() { impl_.reset(); }
bool GroupCapture::Open(AVBufferRef* device,int w,int h,std::string& error,bool cloakedSources)
{
    try { impl_->Open(device,w,h);impl_->cloakedSources=cloakedSources;return true; }
    catch(const winrt::hresult_error&) { error="Companion GPU or Windows capture initialization failed"; }
    catch(const std::exception& ex) { error=ex.what(); }
    return false;
}
bool GroupCapture::Update(const std::vector<QcGameWindow>& targets,std::string& error)
{
    try { impl_->Update(targets);return true; }
    catch(const winrt::hresult_error&) { error="Companion window capture was denied or closed"; }
    catch(const std::exception& ex) { error=ex.what(); }
    return false;
}
bool GroupCapture::Render(AVFrame* frame,std::string& error)
{
    try { impl_->Render(frame);return true; }
    catch(const winrt::hresult_error&) { error="Companion GPU frame processing failed"; }
    catch(const std::exception& ex) { error=ex.what(); }
    return false;
}
AVBufferRef* GroupCapture::Frames() const { return impl_->frames; }
}
