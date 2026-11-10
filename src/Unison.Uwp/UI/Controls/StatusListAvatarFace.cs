using Unison.Core.Models;

namespace Unison.Uwp.UI.Controls
{
    /// <summary>
    /// Payload for <see cref="StatusListAvatarControl"/>'s face <c>ContentControl</c>:
    /// the kind selector picks text vs photo templates; templates bind these fields.
    /// </summary>
    public sealed class StatusListAvatarFace
    {
        public ChatPreviewKind Kind { get; set; }

        public string AvatarUrl { get; set; }

        public uint BackgroundArgb { get; set; }

        public uint TextArgb { get; set; }

        public string PreviewText { get; set; }

        /// <summary>Inner face diameter (ring hole); stamped by the host control.</summary>
        public double Size { get; set; } = 48d;
    }
}
