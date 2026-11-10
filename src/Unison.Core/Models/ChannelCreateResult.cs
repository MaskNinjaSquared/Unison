// =============================================================================
// ChannelCreateResult
//
// Outcome of creating a channel. The picture is a separate update: when it
// fails the channel still exists, so it is reported next to the JID.
// =============================================================================
namespace Unison.Core.Models
{
    public sealed class ChannelCreateResult
    {
        public const string ReasonEmptyName = "empty-name";
        public const string ReasonNotConnected = "not-connected";
        public const string ReasonNoChannel = "no-channel";

        public string ChannelJid { get; set; }

        /// <summary>Why the create itself failed; null when the channel exists.</summary>
        public string FailureReason { get; set; }

        public bool PhotoFailed { get; set; }

        public bool Succeeded => !string.IsNullOrEmpty(ChannelJid);

        public static ChannelCreateResult Failed(string reason) =>
            new ChannelCreateResult { FailureReason = reason };
    }
}
