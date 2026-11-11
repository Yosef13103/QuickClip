#pragma once

#include "Engine.h"
#include <d3d11.h>
#include <wrl/client.h>
extern "C" {
#include <libavutil/hwcontext_d3d11va.h>
}

namespace qc
{
    /// D3D11 video-processor scaling without CPU readback. Keeps BGRA, like ddagrab and gfxcapture.
    /// The bundled scale_d3d11 requests VIDEO_ENCODER-bound textures, rejected by some NVIDIA drivers;
    /// NVENC accepts ordinary render-target textures instead.
    class GpuScale
    {
    public:
        ~GpuScale() { Close(); }
        bool Open(AVBufferRef* inputFrames, int width, int height, std::string& error);
        bool Apply(AVFrame* frame, std::string& error);
        void Close();
        AVBufferRef* Frames() const { return frames_; }
    private:
        AVBufferRef* frames_ = nullptr;
        AVD3D11VADeviceContext* device_ = nullptr;
        Microsoft::WRL::ComPtr<ID3D11VideoDevice> video_;
        Microsoft::WRL::ComPtr<ID3D11VideoContext> context_;
        Microsoft::WRL::ComPtr<ID3D11VideoProcessorEnumerator> enumerator_;
        Microsoft::WRL::ComPtr<ID3D11VideoProcessor> processor_;
        int width_ = 0, height_ = 0;
    };
}
