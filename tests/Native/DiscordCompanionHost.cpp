// Synthetic-only native replay fixture; never run while an actual game is active.
#define NOMINMAX
#include "../../src/QuickClip.Capture/Engine.h"
#include <windows.h>
#include <memory>
#include <stdexcept>
#include <iostream>
#include <string>
#include <chrono>
#include <thread>

void Require(bool yes,const char* message){if(!yes)throw std::runtime_error(message);}
struct Window {
 HWND hwnd=nullptr;
 Window(const wchar_t* cls,int x,int y,int w,int h,COLORREF color) {
  hwnd=CreateWindowExW(0,cls,L"QuickClip generated fixture",WS_POPUP,x,y,w,h,nullptr,nullptr,GetModuleHandleW(nullptr),nullptr);
  Require(hwnd!=nullptr,"create window");
  SetWindowLongPtrW(hwnd,GWLP_USERDATA,(LONG_PTR)color);
  ShowWindow(hwnd,SW_SHOWNOACTIVATE);InvalidateRect(hwnd,nullptr,TRUE);
 }
 ~Window(){if(hwnd)DestroyWindow(hwnd);}
 QcGameWindow Owner()const {
  DWORD pid=0;DWORD thread=GetWindowThreadProcessId(hwnd,&pid);
  Require(thread && pid==GetCurrentProcessId(),"fixture identity");
  FILETIME created{},exited{},kernel{},user{};
  Require(GetProcessTimes(GetCurrentProcess(),&created,&exited,&kernel,&user)!=FALSE,"process lifetime");
  return {hwnd,thread,pid,(int64_t)(((uint64_t)created.dwHighDateTime<<32)|created.dwLowDateTime)};
 }
};
LRESULT CALLBACK Proc(HWND hwnd,UINT msg,WPARAM w,LPARAM l) {
 if(msg==WM_PAINT) {
  PAINTSTRUCT ps;HDC dc=BeginPaint(hwnd,&ps);RECT rc{};GetClientRect(hwnd,&rc);
  HBRUSH b=CreateSolidBrush((COLORREF)GetWindowLongPtrW(hwnd,GWLP_USERDATA));
  FillRect(dc,&rc,b);DeleteObject(b);EndPaint(hwnd,&ps);return 0;
 }
 return DefWindowProcW(hwnd,msg,w,l);
}
void Pump(){MSG m{};while(PeekMessageW(&m,nullptr,0,0,PM_REMOVE)){TranslateMessage(&m);DispatchMessageW(&m);}}
template <class T>T Export(HMODULE dll,const char* name){auto p=GetProcAddress(dll,name);Require(p!=nullptr,"export missing");return reinterpret_cast<T>(p);}
int wmain(int argc,wchar_t** argv) {
 if(argc!=4){std::cerr<<"expected DLL output duration\n";return 2;}
 try {
  int seconds=_wtoi(argv[3]);Require(seconds>=7&&seconds<=15,"bounded duration");
  SetProcessDPIAware();
  WNDCLASSW wc{};wc.lpfnWndProc=Proc;wc.lpszClassName=L"QuickClipGeneratedCompanionHost";wc.hInstance=GetModuleHandleW(nullptr);
  Require(RegisterClassW(&wc)!=0,"register class");
  Window main(wc.lpszClassName,110,110,660,440,RGB(230,35,35));
  std::unique_ptr<Window> popup, popup2;
  Pump();
  std::wstring dllPath=argv[1];
  auto slash=dllPath.find_last_of(L"\\/");
  Require(slash!=std::wstring::npos,"native DLL needs absolute path");
  std::wstring ffmpeg=dllPath.substr(0,slash)+L"\\ffmpeg";
  auto cookie=AddDllDirectory(ffmpeg.c_str());
  Require(cookie!=nullptr,"register isolated ffmpeg dependencies");
  HMODULE dll=LoadLibraryExW(argv[1],nullptr,
      LOAD_LIBRARY_SEARCH_DLL_LOAD_DIR|LOAD_LIBRARY_SEARCH_USER_DIRS|LOAD_LIBRARY_SEARCH_DEFAULT_DIRS);
  if(!dll) {
   auto error=GetLastError();
   std::cerr<<"Native dependency load failed: "<<error<<"\n";
   throw std::runtime_error("load candidate native engine");
  }
  using Start=int(*)(const QcConfig*,const QcEndpointTrack*,int,const QcVideoOptions*,const QcGameWindow*,int,wchar_t*,int);
  using Update=int(*)(const QcGameWindow*,int);
  using Stop=void(*)();
  using Save=int(*)(const wchar_t*,int,const wchar_t*,wchar_t*,int);
  using Stats=void(*)(QcStats*);
  auto start=Export<Start>(dll,"qc_start_companion");
  auto update=Export<Update>(dll,"qc_update_companion");
  auto stop=Export<Stop>(dll,"qc_stop");
  auto save=Export<Save>(dll,"qc_save");
  auto stats=Export<Stats>(dll,"qc_get_stats");
  struct Guard{Stop stop;HMODULE dll;bool running=false;~Guard(){if(running)stop();if(dll)FreeLibrary(dll);}} guard{stop,dll};
  QcConfig cfg{};cfg.fps=30;cfg.outWidth=1280;cfg.outHeight=720;cfg.encoder="h264_nvenc";
  cfg.videoKbps=5000;cfg.audioKbps=128;cfg.bufferSeconds=15;
  QcVideoOptions options{};options.backend=0;options.nvencPreset=2;
  QcGameWindow windows[3]{main.Owner(),{}, {}};
  wchar_t error[1024]{};
  if(!start(&cfg,nullptr,0,&options,windows,1,error,1024)){std::wcerr<<L"Native start: "<<error<<L"\n";return 1;}
  guard.running=true;
  auto t=std::chrono::steady_clock::now();bool added=false,removed=false;
  while(std::chrono::steady_clock::now()-t<std::chrono::seconds(seconds)) {
   Pump();auto elapsed=std::chrono::steady_clock::now()-t;
   if(!added&&elapsed>=std::chrono::seconds(2)) {
    popup=std::make_unique<Window>(wc.lpszClassName,490,175,360,250,RGB(30,215,70));
    popup2=std::make_unique<Window>(wc.lpszClassName,900,210,320,220,RGB(35,50,220));
    Pump();windows[1]=popup->Owner();windows[2]=popup2->Owner();
    Require(update(windows,3)!=0,"add two popup windows in one update");
    added=true;
   }
   if(added&&!removed&&elapsed>=std::chrono::seconds(5)) {
    Require(update(windows,2)!=0,"remove one of three sources");
    popup2.reset();removed=true;
   }
   std::this_thread::sleep_for(std::chrono::milliseconds(16));
  }
  QcStats result{};stats(&result);
  std::wcout<<L"frames output "<<result.width<<L"x"<<result.height<<L", buffer "<<result.bufferSeconds<<L"s, bytes "<<result.bufferBytes<<L"\n";
  Require(result.bufferSeconds>=5&&result.width==1280&&result.height==720,
      "ring lost history on window membership update");
  if(!save(argv[2],5,L"QuickClip generated Discord fixture",error,1024)){std::wcerr<<L"Native save: "<<error<<L"\n";return 1;}
  std::cout<<"Native replay save succeeded\n";return 0;
 } catch(const std::exception& e) {std::cerr<<"Fixture failure: "<<e.what()<<"\n";return 1;}
}
