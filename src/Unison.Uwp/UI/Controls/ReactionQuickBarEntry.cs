using Windows.UI;
using Windows.UI.Xaml.Media;

namespace Unison.Uwp.UI.Controls
{
    /// <summary>One glyph in the reaction quick strip (selected = from-me on the bubble).</summary>
    public sealed class ReactionQuickBarEntry
    {
        private static readonly Brush SelectedFill =
            new SolidColorBrush(Color.FromArgb(0x55, 0xFF, 0xFF, 0xFF));

        private static readonly Brush TransparentFill =
            new SolidColorBrush(Colors.Transparent);

        public ReactionQuickBarEntry(string glyph, bool isSelected)
        {
            Glyph = glyph ?? string.Empty;
            IsSelected = isSelected;
        }

        public string Glyph { get; }

        public bool IsSelected { get; }

        public Brush SelectedBackground => IsSelected ? SelectedFill : TransparentFill;
    }
}
