#pragma once
#ifndef NOMINMAX
#define NOMINMAX
#endif
#include <windows.h>
#include <stdint.h>
extern "C" {
struct QcGameWindow {
    HWND handle;
    DWORD thread, process;
    int64_t started;
};
}
