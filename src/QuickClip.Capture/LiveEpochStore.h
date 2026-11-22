// Authorized, volatile encoded epoch retention shared by successive game graphs.
// No capture acquisition, disk writes, permission inference or per-frame processing.
#pragma once
#include "DetachedEpochArchive.h"
#include <atomic>
#include <chrono>
#include <condition_variable>
#include <deque>
#include <mutex>
#include <thread>

namespace qc
{
    class LiveEpochStore final
    {
    public:
        using Epoch = DetachedEpochArchive::Epoch;

        LiveEpochStore(uint64_t session, uint64_t grant, std::string scope,
            const QcGameWindow& owner, int64_t budget, int64_t windowUs)
            : session_(session), grant_(grant), scope_(std::move(scope)),
              owner_(owner), budget_(budget),
              archive_(budget, windowUs + 2'000'000),
              reclaimer_([this] { ReclaimLoop(); }) {}

        LiveEpochStore(const LiveEpochStore&) = delete;
        LiveEpochStore& operator=(const LiveEpochStore&) = delete;

        ~LiveEpochStore()
        {
            Discard();
            {
                std::lock_guard guard(lock_);
                closing_ = true;
            }
            wake_.notify_one();
            // reclaimer_ is destroyed first and joins before the mutex/queue.
        }

        bool Matches(uint64_t session, uint64_t grant, const std::string& scope) const
        {
            return !revoked_.load(std::memory_order_acquire) &&
                session == session_ && grant == grant_ && scope == scope_;
        }

        bool SameProcess(const QcGameWindow& game) const
        {
            return !revoked_.load(std::memory_order_acquire) && owner_.process == game.process &&
                owner_.started == game.started && owner_.process != 0 && owner_.started > 0;
        }

        bool Append(Engine& engine, uint64_t session, uint64_t epoch,
            uint64_t grant, const std::string& scope, std::string& error)
        {
            if (!Matches(session, grant, scope)) { error = "Archived grant changed."; return false; }
            std::lock_guard guard(lock_);
            if (revoked_) { error = "Archived source revoked."; return false; }
            // The just-quiesced graph no longer has an active ring after transfer.
            // Clear its pressure sample before the new epoch enters the archive.
            activeBytes_.store(0, std::memory_order_release);
            const bool result = engine.SealEpoch(archive_, session, epoch, grant, scope, error);
            bytes_.store(archive_.Bytes(), std::memory_order_release);
            wake_.notify_one();
            return result;
        }

        // Called while PacketRing holds its own producer lock. This must remain
        // noexcept, allocation-free and completely independent of archive locks.
        // Only the reclaimer may evict/free encoded packets.
        void Pressure(int64_t activeBytes) noexcept
        {
            activeBytes_.store(activeBytes, std::memory_order_release);
            const int64_t archived = bytes_.load(std::memory_order_acquire);
            if (!revoked_.load(std::memory_order_acquire) && archived > 0 &&
                activeBytes > budget_ - archived)
                wake_.notify_one();
        }

        void Discard()
        {
            revoked_.store(true, std::memory_order_release);
            std::unique_lock guard(lock_);
            std::vector<std::shared_ptr<const Epoch>> retired;
            archive_.TrimToBytes(0, &retired);
            for (auto& epoch : retired) reclaim_.push_back(std::move(epoch));
            bytes_.store(0, std::memory_order_release);
            guard.unlock();
            wake_.notify_one();
        }

        size_t Count() const
        {
            std::lock_guard guard(lock_);
            return revoked_ ? 0 : archive_.Count();
        }

        std::shared_ptr<const Epoch> Pin(size_t index) const
        {
            std::lock_guard guard(lock_);
            return revoked_ ? nullptr : archive_.Pin(index);
        }

        std::shared_ptr<const Epoch> PinById(uint64_t epochId) const
        {
            std::lock_guard guard(lock_);
            if (revoked_) return nullptr;
            for (size_t i = 0; i < archive_.Count(); ++i)
            {
                auto epoch = archive_.Pin(i);
                if (epoch && epoch->header.epochId == epochId) return epoch;
            }
            return nullptr;
        }

        int64_t Bytes() const { return bytes_.load(std::memory_order_acquire); }
        int64_t Budget() const { return budget_; }
        uint64_t Session() const { return session_; }
        bool Revoked() const { return revoked_.load(std::memory_order_acquire); }

    private:
        void ReclaimLoop()
        {
            // No file or device access. Large chains of AVPacket references are
            // released outside every capture and archive lock.
            SetThreadPriority(GetCurrentThread(), THREAD_PRIORITY_BELOW_NORMAL);
            while (true)
            {
                std::deque<std::shared_ptr<const Epoch>> pending;
                {
                    std::unique_lock guard(lock_);
                    wake_.wait_for(guard, std::chrono::milliseconds(250), [&] {
                        const int64_t archived = bytes_.load(std::memory_order_acquire);
                        return closing_ || !reclaim_.empty() ||
                            (!revoked_.load(std::memory_order_acquire) && archived > 0 &&
                                activeBytes_.load(std::memory_order_acquire) > budget_ - archived);
                    });
                    if (!revoked_ && archive_.Count() > 0)
                    {
                        std::vector<std::shared_ptr<const Epoch>> retired;
                        archive_.TrimExpired(NowUs(), &retired);
                        const int64_t active = activeBytes_.load(std::memory_order_acquire);
                        archive_.TrimToBytes(std::max<int64_t>(0, budget_ - active), &retired);
                        for (auto& epoch : retired) reclaim_.push_back(std::move(epoch));
                        bytes_.store(archive_.Bytes(), std::memory_order_release);
                    }
                    pending.swap(reclaim_);
                    if (pending.empty() && closing_) return;
                }
                pending.clear();
            }
        }

        const uint64_t session_;
        const uint64_t grant_;
        const std::string scope_;
        const QcGameWindow owner_;
        const int64_t budget_;
        mutable std::mutex lock_;
        std::condition_variable wake_;
        bool closing_ = false;
        DetachedEpochArchive archive_;
        std::deque<std::shared_ptr<const Epoch>> reclaim_;
        std::atomic<bool> revoked_{false};
        std::atomic<int64_t> bytes_{0};
        std::atomic<int64_t> activeBytes_{0};
        std::jthread reclaimer_;
    };
}
