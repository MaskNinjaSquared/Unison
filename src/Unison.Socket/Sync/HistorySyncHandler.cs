// =============================================================================
// HistorySyncHandler
//
// Reacts to the notification that says "your history is ready".
//
// It is a protocol message we send to ourselves, which is why the first thing
// this does is refuse one that did not come from us: accepting a history
// notification from a stranger would let them point the app at a blob of their
// choosing. rc14 added that guard; the current code has no equivalent.
//
// After that it is straightforward - download, process, store the LID pairs the
// chunk revealed, publish. The messages are not upserted here: the app decides
// what a history chunk means for its chat list, and doing that inside the socket
// is how the old god class started.
//
// Ports: rc14 the HISTORY_SYNC_NOTIFICATION branch of processMessage in
// src/Utils/process-message.ts
// =============================================================================
using System;
using System.Threading.Tasks;
using Unison.Socket.Abstractions;
using Unison.Socket.Events;
using Unison.Socket.Messages;
using Unison.Socket.Signal;

namespace Unison.Socket.Sync
{
    public sealed class HistorySyncHandler
    {
        private readonly HistorySyncDownloader _downloader;
        private readonly IWaEventBus _events;
        private readonly LidMappingStore _lidMappings;
        private readonly ISocketLog _log;

        private readonly object _queueGate = new object();

        private bool _seenAnyChunk;

        /// <summary>
        /// Chunks are processed one after another off the receive path. A chunk can take seconds
        /// to download and inflate, and the node queue must not wait for it, but two chunks
        /// processed at once would publish the history out of order.
        /// </summary>
        private Task _queue = Task.FromResult(true);

        public HistorySyncHandler(
            HistorySyncDownloader downloader,
            IWaEventBus events,
            LidMappingStore lidMappings = null,
            ISocketLog log = null)
        {
            if (downloader == null)
            {
                throw new ArgumentNullException(nameof(downloader));
            }

            if (events == null)
            {
                throw new ArgumentNullException(nameof(events));
            }

            _downloader = downloader;
            _events = events;
            _lidMappings = lidMappings;
            _log = log ?? NullSocketLog.Instance;
        }

        /// <summary>
        /// Decides whether a chunk is wanted. A full history sync is expensive and most hosts do
        /// not want one unasked, so this mirrors rc14's default of taking everything except FULL.
        /// </summary>
        public Func<global::Proto.Message.Types.HistorySyncNotification, bool> ShouldProcess { get; set; }

        /// <summary>
        /// Called with the blob exactly as it was downloaded, before it is turned into a chunk.
        /// The processed chunk is the shape the app should want, so this exists for one reason:
        /// a host still running its own history code needs the protobuf that code was written
        /// against.
        /// </summary>
        public Func<global::Proto.HistorySync, Task> RawSyncReceived { get; set; }

        /// <summary>
        /// Called once a chunk has been downloaded, published and consumed, so the caller can
        /// tell the server it may send the next one.
        /// </summary>
        /// <remarks>
        /// This is the back-pressure that makes a large history arrive as a paced sequence
        /// instead of a flood. Acknowledging when the notification lands, rather than when the
        /// work it announced is done, lets the server queue every remaining chunk at once, and
        /// they then pile up here waiting their turn - so the app has nothing to show for a long
        /// time and then everything at once. A chunk that could not be read is deliberately left
        /// unacknowledged, because the phone resending it is exactly what we want.
        /// </remarks>
        public Func<MessageEnvelope, Task> ChunkConsumed { get; set; }

        /// <summary>
        /// Reads the history notification out of a message, unwrapping it first. Null when the
        /// message is not one.
        /// </summary>
        public static global::Proto.Message.Types.HistorySyncNotification GetNotification(global::Proto.Message message)
        {
            var normalized = MessageContent.Normalize(message);
            if (normalized == null || normalized.ProtocolMessage == null)
            {
                return null;
            }

            return normalized.ProtocolMessage.HistorySyncNotification;
        }

        /// <summary>
        /// Queues the chunk if the envelope carries one, without waiting for it. Returns true
        /// when the envelope was a history notification and has been taken over.
        /// </summary>
        public bool TryEnqueue(MessageEnvelope envelope)
        {
            if (envelope == null || envelope.Message == null || GetNotification(envelope.Message) == null)
            {
                return false;
            }

            lock (_queueGate)
            {
                _queue = _queue.ContinueWith(_ => TryHandleAsync(envelope)).Unwrap();
            }

            return true;
        }

        /// <summary>Waits for every queued chunk to finish. Mainly for tests and shutdown.</summary>
        public Task DrainAsync()
        {
            lock (_queueGate)
            {
                return _queue;
            }
        }

        /// <summary>
        /// Handles the notification if the envelope carries one. Returns true when a chunk was
        /// downloaded and published.
        /// </summary>
        public async Task<bool> TryHandleAsync(MessageEnvelope envelope)
        {
            if (envelope == null || envelope.Message == null)
            {
                return false;
            }

            var notification = GetNotification(envelope.Message);
            if (notification == null)
            {
                return false;
            }

            // A history notification is something we send to ourselves. Anything else is an
            // attempt to make us download a blob we did not ask for.
            if (!envelope.Key.FromMe)
            {
                _log.Warn("[History] Ignoring a history notification that did not come from us: " + envelope.Author);
                return false;
            }

            if (ShouldProcess != null && !ShouldProcess(notification))
            {
                _log.Debug("[History] Skipping a " + notification.SyncType + " chunk by request");
                return false;
            }

            var isLatest = !_seenAnyChunk;
            _log.Info("[History] Got a " + notification.SyncType + " notification (first=" + isLatest + ")");

            try
            {
                var sync = await _downloader.DownloadAsync(notification).ConfigureAwait(false);
                if (sync == null)
                {
                    return false;
                }

                var raw = RawSyncReceived;
                if (raw != null)
                {
                    await raw(sync).ConfigureAwait(false);
                }

                var chunk = HistorySyncProcessor.Process(sync, _log);

                // On-demand chunks answer a request and say nothing about the initial flow, so
                // they neither claim to be the first nor mark that one has been seen.
                if (!chunk.IsOnDemand)
                {
                    chunk.IsLatest = isLatest;
                    _seenAnyChunk = true;
                }

                chunk.ChunkOrder = notification.HasChunkOrder ? (int?)notification.ChunkOrder : null;
                chunk.PeerDataRequestSessionId = notification.PeerDataRequestSessionId;

                await StoreMappingsAsync(chunk).ConfigureAwait(false);

                await _events.EmitAsync(WaEventKind.MessagingHistorySet, chunk).ConfigureAwait(false);

                var consumed = ChunkConsumed;
                if (consumed != null)
                {
                    await consumed(envelope).ConfigureAwait(false);
                }

                return true;
            }
            catch (Exception ex)
            {
                // A chunk we cannot read is not fatal: the phone resends, and the rest of the
                // sync continues.
                _log.Error("[History] Failed to process a " + notification.SyncType + " chunk", ex);
                return false;
            }
        }

        /// <summary>Resets the "first chunk" tracking, for a fresh login.</summary>
        public void Reset()
        {
            _seenAnyChunk = false;
        }

        private async Task StoreMappingsAsync(MessagingHistorySet chunk)
        {
            if (_lidMappings == null || chunk.LidMappings.Count == 0)
            {
                return;
            }

            try
            {
                await _lidMappings.StoreMappingsAsync(chunk.LidMappings).ConfigureAwait(false);
                _log.Debug("[History] Stored " + chunk.LidMappings.Count + " LID mapping(s) from the chunk");
            }
            catch (Exception ex)
            {
                _log.Warn("[History] Could not store the chunk's LID mappings", ex);
            }
        }
    }
}
