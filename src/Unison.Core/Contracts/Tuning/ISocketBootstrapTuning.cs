// =============================================================================
// ISocketBootstrapTuning
//
// Neutral bootstrap knobs for the socket layer (pre-key upload batch on connect).
// Tier numbers come from the product layer; hosts only pass them through.
// =============================================================================
namespace Unison.Core.Contracts.Tuning
{
    public interface ISocketBootstrapTuning
    {
        /// <summary>Pre-keys uploaded before the first wire-up after pairing / restore.</summary>
        int InitialPreKeyUploadCount { get; }
    }
}