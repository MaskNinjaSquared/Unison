// =============================================================================
// IChatWindowTuning
//
// Open-chat bubble window (initial paint vs hard cap while the pane is open).
// Follows memory tier, not device family; product layer owns the numbers.
// =============================================================================
namespace Unison.Core.Contracts.Tuning
{
    public interface IChatWindowTuning
    {
        int InitialUiMessageWindow { get; }

        int MaxUiMessageWindow { get; }
    }
}