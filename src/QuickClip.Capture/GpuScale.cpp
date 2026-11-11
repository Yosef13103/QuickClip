#include "GpuScale.h"

namespace qc
{
    namespace
    {
        struct DeviceLock
        {
            AVD3D11VADeviceContext* device;
            explicit DeviceLock(AVD3D11VADeviceContext* d) : device(d) { device->lock(device->lock_ctx); }
            ~DeviceLock() { device->unlock(device->lock_ctx); }
        };
        bool Failed(HRESULT hr, std::string& error)
        {
            if (SUCCEEDED(hr)) return false;
            char code[32];
            snprintf(code, sizeof code, "0x%08lX", static_cast<unsigned long>(hr));
            error = std::string("D3D11 GPU scaling failed: ") + code;
            return true;
        }
    }

    void GpuScale::Close()
    {
        processor_.Reset(); enumerator_.Reset(); context_.Reset(); video_.Reset();
        av_buffer_unref(&frames_);
        device_ = nullptr;
    }

    bool GpuScale::Open(AVBufferRef* inputFrames, int width, int height, std::string& error)
    {
        Close();
        if (!inputFrames) { error = "GPU scaling needs D3D11 input frames."; return false; }
        auto* input = reinterpret_cast<AVHWFramesContext*>(inputFrames->data);
        if (input->format != AV_PIX_FMT_D3D11 || input->sw_format != AV_PIX_FMT_BGRA)
        { error = "GPU scaling needs BGRA D3D11 frames."; return false; }
        auto* hw = reinterpret_cast<AVHWDeviceContext*>(input->device_ref->data);
        device_ = static_cast<AVD3D11VADeviceContext*>(hw->hwctx);
        DeviceLock lock(device_);
        if (Failed(device_->device->QueryInterface(IID_PPV_ARGS(&video_)), error) ||
            Failed(device_->device_context->QueryInterface(IID_PPV_ARGS(&context_)), error)) return false;
        D3D11_VIDEO_PROCESSOR_CONTENT_DESC desc{};
        desc.InputFrameFormat = D3D11_VIDEO_FRAME_FORMAT_PROGRESSIVE;
        desc.InputWidth = input->width; desc.InputHeight = input->height;
        desc.OutputWidth = width; desc.OutputHeight = height;
        desc.Usage = D3D11_VIDEO_USAGE_PLAYBACK_NORMAL;
        if (Failed(video_->CreateVideoProcessorEnumerator(&desc, &enumerator_), error)) return false;
        UINT support = 0;
        if (Failed(enumerator_->CheckVideoProcessorFormat(DXGI_FORMAT_B8G8R8A8_UNORM, &support), error) ||
            (support & (D3D11_VIDEO_PROCESSOR_FORMAT_SUPPORT_INPUT | D3D11_VIDEO_PROCESSOR_FORMAT_SUPPORT_OUTPUT)) !=
                (D3D11_VIDEO_PROCESSOR_FORMAT_SUPPORT_INPUT | D3D11_VIDEO_PROCESSOR_FORMAT_SUPPORT_OUTPUT))
        { error = "D3D11 video processor does not support BGRA scaling."; return false; }
        if (Failed(video_->CreateVideoProcessor(enumerator_.Get(), 0, &processor_), error)) return false;
        frames_ = av_hwframe_ctx_alloc(input->device_ref);
        if (!frames_) { error = "Allocating GPU scaler frames failed."; return false; }
        auto* frames = reinterpret_cast<AVHWFramesContext*>(frames_->data);
        frames->format = AV_PIX_FMT_D3D11;
        frames->sw_format = AV_PIX_FMT_BGRA;
        frames->width = width; frames->height = height;
        auto* output = static_cast<AVD3D11VAFramesContext*>(frames->hwctx);
        output->BindFlags = D3D11_BIND_RENDER_TARGET;
        int ret = av_hwframe_ctx_init(frames_);
        if (ret < 0) { error = "Creating GPU scaler textures failed: " + AvError(ret); return false; }
        width_ = width; height_ = height;
        RECT src{ 0, 0, input->width, input->height }, dst{ 0, 0, width, height };
        context_->VideoProcessorSetStreamSourceRect(processor_.Get(), 0, TRUE, &src);
        context_->VideoProcessorSetStreamDestRect(processor_.Get(), 0, TRUE, &dst);
        context_->VideoProcessorSetOutputTargetRect(processor_.Get(), TRUE, &dst);
        context_->VideoProcessorSetStreamFrameFormat(processor_.Get(), 0, D3D11_VIDEO_FRAME_FORMAT_PROGRESSIVE);
        context_->VideoProcessorSetStreamAutoProcessingMode(processor_.Get(), 0, FALSE);
        // Full-range RGB in and out; the encoder performs the existing BT.709 limited-range conversion.
        D3D11_VIDEO_PROCESSOR_COLOR_SPACE color{};
        color.RGB_Range = 0; color.Nominal_Range = 2;
        context_->VideoProcessorSetStreamColorSpace(processor_.Get(), 0, &color);
        context_->VideoProcessorSetOutputColorSpace(processor_.Get(), &color);
        return true;
    }

    bool GpuScale::Apply(AVFrame* frame, std::string& error)
    {
        if (!frames_) return true;
        AVFrame* out = av_frame_alloc();
        if (!out) { error = "Allocating scaled frame failed."; return false; }
        int ret = av_hwframe_get_buffer(frames_, out, 0);
        if (ret >= 0) ret = av_frame_copy_props(out, frame);
        if (ret < 0) { error = "Allocating GPU frame failed: " + AvError(ret); av_frame_free(&out); return false; }
        DeviceLock lock(device_);
        Microsoft::WRL::ComPtr<ID3D11VideoProcessorInputView> input;
        Microsoft::WRL::ComPtr<ID3D11VideoProcessorOutputView> output;
        D3D11_VIDEO_PROCESSOR_INPUT_VIEW_DESC inDesc{};
        inDesc.ViewDimension = D3D11_VPIV_DIMENSION_TEXTURE2D;
        inDesc.Texture2D.ArraySlice = static_cast<UINT>(reinterpret_cast<uintptr_t>(frame->data[1]));
        D3D11_VIDEO_PROCESSOR_OUTPUT_VIEW_DESC outDesc{};
        outDesc.ViewDimension = D3D11_VPOV_DIMENSION_TEXTURE2D;
        HRESULT hr = video_->CreateVideoProcessorInputView(reinterpret_cast<ID3D11Texture2D*>(frame->data[0]), enumerator_.Get(), &inDesc, &input);
        if (SUCCEEDED(hr)) hr = video_->CreateVideoProcessorOutputView(reinterpret_cast<ID3D11Texture2D*>(out->data[0]), enumerator_.Get(), &outDesc, &output);
        D3D11_VIDEO_PROCESSOR_STREAM stream{};
        stream.Enable = TRUE; stream.pInputSurface = input.Get();
        if (SUCCEEDED(hr)) hr = context_->VideoProcessorBlt(processor_.Get(), output.Get(), 0, 1, &stream);
        if (Failed(hr, error)) { av_frame_free(&out); return false; }
        out->width = width_; out->height = height_;
        av_frame_unref(frame);
        av_frame_move_ref(frame, out);
        av_frame_free(&out);
        return true;
    }
}
