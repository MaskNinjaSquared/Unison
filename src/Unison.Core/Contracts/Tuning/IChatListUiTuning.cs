// =============================================================================
// IChatListUiTuning
//
// Chat-list UI pacing: how many rows release per batch and how many previews
// hydrate per idle slice. Tier values live in the product layer.
// =============================================================================
namespace Unison.Core.Contracts.Tuning
{
    public interface IChatListUiTuning
    {
        int ChatListReleaseBatchSize { get; }

        int PreviewHydrateBatchSize { get; }
    }
}