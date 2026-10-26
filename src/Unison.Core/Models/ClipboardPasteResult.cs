using System.Collections.Generic;

namespace Unison.Core.Models
{
    /// <summary>
    /// Clipboard snapshot for paste-into-chat: optional text plus Image/Audio/Document files.
    /// </summary>
    public sealed class ClipboardPasteResult
    {
        public static ClipboardPasteResult Empty { get; } = new ClipboardPasteResult
        {
            Kind = ClipboardPasteKind.None
        };

        public ClipboardPasteKind Kind { get; set; }

        public string Text { get; set; }

        public IReadOnlyList<PickedChatMedia> Media { get; set; }

        public bool HasMedia => Media != null && Media.Count > 0;

        public bool HasText => !string.IsNullOrWhiteSpace(Text);
    }
}
