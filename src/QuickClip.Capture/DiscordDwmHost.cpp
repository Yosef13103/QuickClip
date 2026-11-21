#include "DiscordDwmHost.h"
#include <algorithm>
#include <cmath>
#include <stdexcept>

namespace qc {
namespace {
constexpr wchar_t HostClass[] = L"QuickClipDiscordDwmMirrorHost";
constexpr int MaxDimension = 4096;

int64_t StartTime(HANDLE process)
{
    FILETIME created{}, exited{}, kernel{}, user{};
    if (!process || !GetProcessTimes(process,&created,&exited,&kernel,&user)) return 0;
    return static_cast<int64_t>((static_cast<uint64_t>(created.dwHighDateTime)<<32)|created.dwLowDateTime);
}

struct PhysicalDpi {
    DPI_AWARENESS_CONTEXT prior = SetThreadDpiAwarenessContext(DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2);
    ~PhysicalDpi() { if(prior) SetThreadDpiAwarenessContext(prior); }
};
}

LRESULT CALLBACK DiscordDwmHost::WindowProc(HWND hwnd, UINT message, WPARAM wp, LPARAM lp)
{
    if(message==WM_PAINT) {
        PAINTSTRUCT paint{};
        HDC dc=BeginPaint(hwnd,&paint);
        RECT rect{};
        GetClientRect(hwnd,&rect);
        FillRect(dc,&rect,static_cast<HBRUSH>(GetStockObject(BLACK_BRUSH)));
        EndPaint(hwnd,&paint);
        return 0;
    }
    return DefWindowProcW(hwnd,message,wp,lp);
}

DiscordDwmHost::~DiscordDwmHost() { Close(); }

bool DiscordDwmHost::ValidSource() const
{
    if(!source_.handle || !IsWindow(source_.handle) || !process_ ||
        WaitForSingleObject(process_,0)!=WAIT_TIMEOUT) return false;
    DWORD pid=0;
    DWORD thread=GetWindowThreadProcessId(source_.handle,&pid);
    return thread==source_.thread && pid==source_.process &&
        StartTime(process_)==source_.started;
}

bool DiscordDwmHost::SameSource(const QcGameWindow& other) const
{
    return other.handle==source_.handle && other.thread==source_.thread &&
        other.process==source_.process && other.started==source_.started;
}

void DiscordDwmHost::Close()
{
    if(thumbnail_) { DwmUnregisterThumbnail(thumbnail_);thumbnail_=nullptr; }
    if(host_) { DestroyWindow(host_);host_=nullptr; }
    if(owner_) { DestroyWindow(owner_);owner_=nullptr; }
    if(process_) { CloseHandle(process_);process_=nullptr; }
    source_={};capture_={};width_=height_=0;
    sourceSize_={};destination_={};lastRefresh_=0;layoutInitialized_=false;
}

bool DiscordDwmHost::Open(const QcGameWindow& source, int width, int height, std::string& error)
{
    Close();
    try {
        if(!source.handle || !source.thread || !source.process || source.started<=0 ||
           width<64 || height<64 || width>MaxDimension || height>MaxDimension)
            throw std::runtime_error("Discord mirror geometry or identity is invalid");
        source_=source;width_=width;height_=height;
        process_=OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION|SYNCHRONIZE,FALSE,source.process);
        if(!ValidSource()) throw std::runtime_error("Authorized Discord source changed before mirror initialization");
        PhysicalDpi dpi;
        WNDCLASSEXW klass{};klass.cbSize=sizeof(klass);
        klass.hInstance=GetModuleHandleW(nullptr);
        klass.lpfnWndProc=WindowProc;
        klass.lpszClassName=HostClass;
        if(!RegisterClassExW(&klass) && GetLastError()!=ERROR_CLASS_ALREADY_EXISTS)
            throw std::runtime_error("Discord mirror window class could not be registered");
        RECT placement{};
        if(!GetWindowRect(source_.handle,&placement))
            throw std::runtime_error("Discord source has no window rectangle");
        owner_=CreateWindowExW(0,HostClass,L"QuickClip mirror owner",WS_POPUP,
            placement.left,placement.top,1,1,nullptr,nullptr,klass.hInstance,nullptr);
        if(!owner_) throw std::runtime_error("Discord mirror owner could not be created");
        host_=CreateWindowExW(0,HostClass,L"QuickClip Discord mirror",WS_POPUP,
            placement.left,placement.top,1,1,owner_,nullptr,klass.hInstance,nullptr);
        if(!host_ || IsWindowVisible(host_))
            throw std::runtime_error("Discord mirror capture window did not start hidden");
        BOOL cloak=TRUE;
        if(FAILED(DwmSetWindowAttribute(host_,DWMWA_CLOAK,&cloak,sizeof(cloak))))
            throw std::runtime_error("DWM application cloaking is unavailable");
        int status=0;
        if(FAILED(DwmGetWindowAttribute(host_,DWMWA_CLOAKED,&status,sizeof(status))) ||
           !(status&DWM_CLOAKED_APP))
            throw std::runtime_error("Discord mirror could not verify its hidden state");
        if(!SetWindowPos(host_,HWND_BOTTOM,placement.left,placement.top,width_,height_,
            SWP_NOACTIVATE|SWP_NOOWNERZORDER))
            throw std::runtime_error("Discord mirror could not be resized while hidden");
        ShowWindow(host_,SW_SHOWNOACTIVATE);
        SetWindowPos(host_,HWND_BOTTOM,0,0,0,0,
            SWP_NOACTIVATE|SWP_NOMOVE|SWP_NOSIZE|SWP_NOOWNERZORDER);
        if(FAILED(DwmGetWindowAttribute(host_,DWMWA_CLOAKED,&status,sizeof(status))) ||
           !(status&DWM_CLOAKED_APP))
            throw std::runtime_error("Discord mirror lost its cloak when shown");
        if(FAILED(DwmRegisterThumbnail(host_,source_.handle,&thumbnail_)) || !thumbnail_)
            throw std::runtime_error("DWM denied the authorized Discord window thumbnail");
        DWORD pid=0;
        capture_.handle=host_;
        capture_.thread=GetWindowThreadProcessId(host_,&pid);
        capture_.process=pid;
        capture_.started=StartTime(GetCurrentProcess());
        if(!capture_.thread || pid!=GetCurrentProcessId() || !Refresh(error))
            throw std::runtime_error(error.empty() ? "Discord mirror initialization failed" : error);
        return true;
    } catch(const std::exception& ex) {
        error=ex.what();
        Close();
        return false;
    }
}

bool DiscordDwmHost::Refresh(std::string& error)
{
    if(!host_ || !thumbnail_ || !ValidSource()) {
        error="Authorized Discord window closed or changed";
        return false;
    }
    // Source may move across physical monitors. Only the exact previously
    // authorized HWND can supply pixels; never switch to another application.
    auto now=GetTickCount64();
    if(layoutInitialized_ && now-lastRefresh_<120) return true;
    lastRefresh_=now;
    // A visible proxy would defeat this backend's privacy/UX contract.
    int cloak=0;
    if(FAILED(DwmGetWindowAttribute(host_,DWMWA_CLOAKED,&cloak,sizeof(cloak))) ||
       !(cloak&DWM_CLOAKED_APP)) {
        error="Discord mirror was no longer cloaked; capture stopped";
        return false;
    }
    SIZE current{};
    HRESULT hr=DwmQueryThumbnailSourceSize(thumbnail_,&current);
    if(FAILED(hr)||current.cx<=0||current.cy<=0 ||
       current.cx>MaxDimension||current.cy>MaxDimension) {
        error="Discord window source dimensions unavailable";
        return false;
    }
    // Keep a host at the source's true DWM pixel size: the old composite
    // shader handles the only proportional scale into the output tile.
    if(current.cx!=width_ || current.cy!=height_) {
        if(!SetWindowPos(host_,HWND_BOTTOM,0,0,current.cx,current.cy,
              SWP_NOACTIVATE|SWP_NOMOVE|SWP_NOOWNERZORDER)) {
            error="Discord mirror host could not follow source geometry";
            return false;
        }
        width_=current.cx;
        height_=current.cy;
        InvalidateRect(host_,nullptr,TRUE);
        UpdateWindow(host_);
    }
    RECT rect{0,0,current.cx,current.cy};
    if(!layoutInitialized_ || current.cx!=sourceSize_.cx || current.cy!=sourceSize_.cy ||
        rect.left!=destination_.left || rect.top!=destination_.top ||
        rect.right!=destination_.right || rect.bottom!=destination_.bottom) {
        DWM_THUMBNAIL_PROPERTIES properties{};
        properties.dwFlags=DWM_TNP_VISIBLE|DWM_TNP_OPACITY|
            DWM_TNP_RECTDESTINATION|DWM_TNP_SOURCECLIENTAREAONLY;
        properties.fVisible=TRUE;
        properties.opacity=255;
        properties.fSourceClientAreaOnly=FALSE;
        properties.rcDestination=rect;
        hr=DwmUpdateThumbnailProperties(thumbnail_,&properties);
        if(FAILED(hr)) { error="Discord mirror could not update its source geometry";return false; }
        sourceSize_=current;destination_=rect;layoutInitialized_=true;
        InvalidateRect(host_,nullptr,TRUE); // clear old thumbnail locations
        UpdateWindow(host_);
    }
    return true;
}
}
