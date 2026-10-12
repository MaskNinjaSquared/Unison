using System;
using Windows.Foundation;
using Windows.UI.Xaml.Controls;

namespace Unison.Uwp.UI.Controls
{
    /// <summary>
    /// Square tile that holds a bubble's media placeholders (pending download, play overlay):
    /// as wide as the bubble is allowed to be, capped at <see cref="MaxSide"/>.
    /// </summary>
    /// <remarks>
    /// A chat bubble is Auto-width, so a stretched child has nothing to stretch against: the
    /// placeholder collapsed onto whatever it contained — the 56×56 play circle, or the download
    /// glyph — and the overlay border with it. Reading the width off the messages
    /// <c>ListView</c> in <c>Loaded</c>/<c>SizeChanged</c> did not hold either: items are realized
    /// while the list is still measuring, so <c>ActualWidth</c> came back as 0, the size was
    /// skipped, and nothing asked again afterwards. Measure is already told how much width the
    /// bubble may take, which is why the tile is decided here instead of from the visual tree.
    /// </remarks>
    public sealed class ChatMediaPlaceholderHost : Grid
    {
        /// <summary>Largest tile side, matching the WhatsApp media placeholder.</summary>
        public const double MaxSide = 250;

        protected override Size MeasureOverride(Size availableSize)
        {
            double side = ResolveSide(availableSize.Width);
            Size natural = base.MeasureOverride(new Size(side, side));

            // Every placeholder inside is x:Load'ed, so a text bubble realizes none of them.
            // Claiming the square anyway would reserve a blank tile under every message.
            if (natural.Width <= 0)
            {
                return natural;
            }

            return new Size(side, side);
        }

        /// <summary>
        /// Unconstrained width means no bubble constraint reached us (the list scrolls
        /// horizontally, or we are measured outside a chat). The cap is the honest answer there.
        /// </summary>
        private static double ResolveSide(double availableWidth)
        {
            if (double.IsNaN(availableWidth) ||
                double.IsInfinity(availableWidth) ||
                availableWidth <= 0)
            {
                return MaxSide;
            }

            return Math.Min(availableWidth, MaxSide);
        }
    }
}
