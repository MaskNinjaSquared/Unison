// =============================================================================
// IWaEventMerger
//
// How the buffer folds many events of one kind into a single consolidated
// payload while buffering is active. rc14 does this in one large append()
// switch; here each kind brings its own rule, so a feature registers its merger
// when it is migrated and the bus needs no central switch to grow.
//
// Ports: rc14 append() in src/Utils/event-buffer.ts
// =============================================================================
namespace Unison.Socket.Events
{
    /// <summary>
    /// Consolidation rule for one bufferable event kind - the C# counterpart of a single
    /// <c>case</c> of the rc14 <c>append</c> switch.
    /// </summary>
    /// <remarks>
    /// An event kind with no registered merger is never buffered: it is dispatched immediately.
    /// Passing an event through unbuffered is always correct, whereas merging it with the wrong
    /// rule is not, so the bus fails safe while features are still being migrated.
    /// </remarks>
    public interface IWaEventMerger
    {
        WaEventKind Kind { get; }

        /// <summary>
        /// Folds <paramref name="incoming"/> into what is already buffered.
        /// <paramref name="existing"/> is null for the first event of the batch.
        /// </summary>
        object Merge(object existing, object incoming);
    }
}
