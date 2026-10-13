// =============================================================================
// IncomingEmptyContentSkip
//
// When MessageRenderReader returns no content, the pump still has to decide
// why (logging) and whether the missing-message ledger must clear — otherwise
// a placeholder recovery that lands as an unrecognised type keeps requesting
// a resend forever.
// =============================================================================
namespace Unison.Core.Helpers
{
    public enum IncomingEmptyContentReason
    {
        HasContent = 0,
        SenderKeyDistributionOnly,
        UnrecognisedEmpty
    }

    public readonly struct IncomingEmptyContentSkip
    {
        public IncomingEmptyContentSkip(IncomingEmptyContentReason reason, bool clearMissingLedger)
        {
            Reason = reason;
            ClearMissingLedger = clearMissingLedger;
        }

        public IncomingEmptyContentReason Reason { get; }
        public bool ClearMissingLedger { get; }
        public bool ShouldSkip => Reason != IncomingEmptyContentReason.HasContent;

        public static IncomingEmptyContentSkip For(string content, bool hasSenderKeyDistribution, bool hasMessageId)
        {
            if (!string.IsNullOrEmpty(content))
            {
                return new IncomingEmptyContentSkip(IncomingEmptyContentReason.HasContent, false);
            }

            IncomingEmptyContentReason reason = hasSenderKeyDistribution
                ? IncomingEmptyContentReason.SenderKeyDistributionOnly
                : IncomingEmptyContentReason.UnrecognisedEmpty;

            return new IncomingEmptyContentSkip(reason, hasMessageId);
        }
    }
}
