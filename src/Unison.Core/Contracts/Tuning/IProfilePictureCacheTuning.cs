// =============================================================================
// IProfilePictureCacheTuning
//
// In-process LRU cap for w:profile:picture IQ answers (reconnect-surviving cache).
// Values come from the product layer; the host cache only enforces the limit.
// =============================================================================
namespace Unison.Core.Contracts.Tuning
{
    public interface IProfilePictureCacheTuning
    {
        /// <summary>Max distinct (jid, query type) entries kept in RAM.</summary>
        int MaxEntries { get; }
    }
}