#pragma once
#include "QcGameWindow.h"
#include <string>
extern "C" {
#include <libavutil/frame.h>
#include <libavutil/buffer.h>
}
#include <vector>
#include <memory>

namespace qc {
// A single authorized application's HWND group. WGC is never replaced by a monitor capture.
// The owner calls Update/Render only on its video thread; authorization changes revoke
// packet publication synchronously through the owning engine's video generation.
class GroupCapture
{
public:
    GroupCapture();
    ~GroupCapture();
    void Close();
    GroupCapture(const GroupCapture&) = delete;
    GroupCapture& operator=(const GroupCapture&) = delete;
    bool Open(AVBufferRef* device, int outputWidth, int outputHeight, std::string& error);
    bool Update(const std::vector<QcGameWindow>& targets, std::string& error);
    bool Render(AVFrame* frame, std::string& error);
    AVBufferRef* Frames() const;
private:
    struct Impl;
    std::unique_ptr<Impl> impl_;
};
}
