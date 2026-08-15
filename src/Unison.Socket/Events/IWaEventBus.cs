// =============================================================================
// IWaEventBus
//
// The only channel from the socket layer to the host. Facades subscribe here and
// turn payloads into domain models and persistence; the socket never calls into
// the app, which is what keeps the dependency arrow pointing one way.
//
// It replaces plain C# events for three reasons: emitting is awaitable, handlers
// are isolated so one throwing subscriber cannot silence the rest, and events can
// be buffered and flushed as a batch during bursts such as the initial sync.
//
// Ports: rc14 BaileysBufferableEventEmitter in src/Utils/event-buffer.ts
// =============================================================================
using System;
using System.Threading.Tasks;

namespace Unison.Socket.Events
{
    /// <summary>
    /// Event channel between the socket layer and the host. Handlers get batches, not single
    /// events, and buffering lets an initial sync land as one flush instead of thousands of writes.
    /// </summary>
    public interface IWaEventBus
    {
        bool IsBuffering { get; }

        /// <summary>
        /// Publishes an event. Returns true when it was buffered for a later flush, false when it
        /// was dispatched right away.
        /// </summary>
        Task<bool> EmitAsync(WaEventKind kind, object payload);

        /// <summary>Subscribes to batches. Dispose the result to unsubscribe.</summary>
        IDisposable Process(Func<WaEventBatch, Task> handler);

        /// <summary>Starts (or joins) buffering. Nested calls are counted.</summary>
        void Buffer();

        /// <summary>Releases everything buffered as a single batch. False when not buffering.</summary>
        Task<bool> FlushAsync();

        /// <summary>Buffers events for the duration of <paramref name="work"/>.</summary>
        Task RunBufferedAsync(Func<Task> work);

        Task<T> RunBufferedAsync<T>(Func<Task<T>> work);

        void RegisterMerger(IWaEventMerger merger);

        /// <summary>Drops pending events, timers and subscribers.</summary>
        void Destroy();
    }
}
