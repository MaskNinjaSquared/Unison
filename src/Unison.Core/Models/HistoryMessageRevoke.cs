namespace Unison.Core.Models
{
    /// <summary>Marks an existing <c>history_message</c> row as revoked.</summary>
    public sealed class HistoryMessageRevoke
    {
        public string ChatJid { get; set; }

        public string MessageId { get; set; }
    }
}
