using System.Collections.Generic;

namespace Unison.Core.Models
{
    /// <summary>
    /// Materialized inbound Share Target payload (text and/or chat attachments).
    /// Bytes are already read by the UWP host — Core never touches StorageFile.
    /// </summary>
    public sealed class IncomingSharePayload
    {
        public string Text { get; set; }

        public IReadOnlyList<PickedChatMedia> Media { get; set; }

        public bool HasText => !string.IsNullOrWhiteSpace(Text);

        public bool HasMedia => Media != null && Media.Count > 0;

        public bool IsEmpty => !HasText && !HasMedia;
    }
}
