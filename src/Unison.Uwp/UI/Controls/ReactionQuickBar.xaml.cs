using System;
using System.Collections.Generic;
using Unison.Core.Helpers;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Input;

namespace Unison.Uwp.UI.Controls
{
    /// <summary>
    /// Quick reaction strip: glyphs from <see cref="ReactionQuickEmojis.ForStrip"/> scroll
    /// (12 default, or 13 with a custom mine at the front); plus stays fixed outside the
    /// scroller. Width never exceeds the window.
    /// </summary>
    public sealed partial class ReactionQuickBar : UserControl
    {
        private const double ScreenMargin = 24;
        private const double PlusColumnWidth = 40;
        private const double ChromePadding = 14;

        private string _selectedEmoji;

        public ReactionQuickBar()
        {
            Entries = BuildEntries(null);
            InitializeComponent();
        }

        public IReadOnlyList<ReactionQuickBarEntry> Entries { get; private set; }

        /// <summary>From-me emoji on the bubble; marks that cell in the strip.</summary>
        public string SelectedEmoji
        {
            get => _selectedEmoji;
            set
            {
                if (string.Equals(_selectedEmoji, value, StringComparison.Ordinal))
                {
                    return;
                }

                _selectedEmoji = value;
                Entries = BuildEntries(_selectedEmoji);
                if (EmojiStrip != null)
                {
                    EmojiStrip.ItemsSource = Entries;
                }
            }
        }

        public event EventHandler<string> EmojiChosen;

        public event EventHandler PlusClicked;

        private static IReadOnlyList<ReactionQuickBarEntry> BuildEntries(string selected)
        {
            IReadOnlyList<string> glyphs = ReactionQuickEmojis.ForStrip(selected);
            var list = new List<ReactionQuickBarEntry>(glyphs.Count);
            for (int i = 0; i < glyphs.Count; i++)
            {
                string glyph = glyphs[i];
                bool selectedMark = !string.IsNullOrEmpty(selected) &&
                    string.Equals(glyph, selected.Trim(), StringComparison.Ordinal);
                list.Add(new ReactionQuickBarEntry(glyph, selectedMark));
            }

            return list;
        }

        private void ReactionQuickBar_Loaded(object sender, RoutedEventArgs e)
        {
            double screenCap = double.PositiveInfinity;
            try
            {
                double screen = Window.Current?.Bounds.Width ?? 0;
                if (screen > ScreenMargin * 2)
                {
                    screenCap = screen - ScreenMargin;
                }
            }
            catch
            {
            }

            // Honor an explicit MaxWidth from the host (e.g. 250 chrome); never exceed the screen.
            double cap = MaxWidth > 0 && !double.IsInfinity(MaxWidth)
                ? Math.Min(MaxWidth, screenCap)
                : screenCap;
            if (!double.IsInfinity(cap) && cap > 0)
            {
                MaxWidth = cap;
                if (Width > cap)
                {
                    Width = cap;
                }
            }

            // Force the * column to shrink so glyphs scroll instead of expanding the flyout.
            double layoutWidth = Width > 0 ? Width : MaxWidth;
            if (EmojiScroller != null && layoutWidth > 0 && !double.IsInfinity(layoutWidth))
            {
                EmojiScroller.MaxWidth = Math.Max(
                    40,
                    layoutWidth - PlusColumnWidth - ChromePadding);
            }
        }

        private void Emoji_Tapped(object sender, TappedRoutedEventArgs e)
        {
            e.Handled = true;
            var host = sender as FrameworkElement;
            string glyph = host?.Tag as string;
            if (string.IsNullOrEmpty(glyph))
            {
                var entry = host?.DataContext as ReactionQuickBarEntry;
                glyph = entry?.Glyph;
            }

            if (string.IsNullOrEmpty(glyph))
            {
                return;
            }

            EmojiChosen?.Invoke(this, glyph);
        }

        private void Plus_Click(object sender, RoutedEventArgs e)
        {
            PlusClicked?.Invoke(this, EventArgs.Empty);
        }
    }
}
