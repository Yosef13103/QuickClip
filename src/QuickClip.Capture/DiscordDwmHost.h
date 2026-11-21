#pragma once
#include "QcGameWindow.h"
#include <windows.h>
#include <dwmapi.h>
#include <string>

// Research-backed Windows 10 single-window Discord mirror. A DWM thumbnail
// renders into an application-cloaked, owned destination; existing GroupCapture
// acquires that destination through WGC. Never discovers or authorizes sources.
namespace qc {
class DiscordDwmHost {
public:
    DiscordDwmHost() = default;
    ~DiscordDwmHost();
    DiscordDwmHost(const DiscordDwmHost&) = delete;
    DiscordDwmHost& operator=(const DiscordDwmHost&) = delete;
    bool Open(const QcGameWindow& source, int width, int height, std::string& error);
    bool Refresh(std::string& error);
    bool SameSource(const QcGameWindow& source) const;
    QcGameWindow CaptureTarget() const { return capture_; }
    void Close();
private:
    static LRESULT CALLBACK WindowProc(HWND, UINT, WPARAM, LPARAM);
    bool ValidSource() const;
    QcGameWindow source_{};
    QcGameWindow capture_{};
    HANDLE process_ = nullptr;
    HWND owner_ = nullptr;
    HWND host_ = nullptr;
    HTHUMBNAIL thumbnail_ = nullptr;
    int width_ = 0, height_ = 0;
    SIZE sourceSize_{};
    RECT destination_{};
    ULONGLONG lastRefresh_ = 0;
    bool layoutInitialized_ = false;
};
}
