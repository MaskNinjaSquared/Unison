using System;
using Unison.Core.Contracts;
using Unison.Core.Contracts.WhatsApp;
using Unison.Core.State;

namespace Unison.Core.Helpers
{
    /// <summary>
    /// The labels the group flows put on members and rows, and whether a group label is a real
    /// name. Reads the shared name tables through <see cref="ContactDisplayNames"/>, the same
    /// resolver the chat list and <c>ContactFacade</c> use.
    /// </summary>
    public sealed class GroupNaming : IGroupNaming
    {
        private const string ChatContext = "chat";
        private const string SenderContext = "sender";

        private readonly ContactDisplayNames _names;
        private readonly IJidResolver _jids;
        private readonly ISelfMarkerNaming _markers;
        private readonly IStringResources _strings;

        public GroupNaming(
            ContactDisplayNames names,
            IJidResolver jids,
            ISelfMarkerNaming markers,
            IStringResources strings)
        {
            _names = names ?? throw new ArgumentNullException(nameof(names));
            _jids = jids ?? throw new ArgumentNullException(nameof(jids));
            _markers = markers ?? throw new ArgumentNullException(nameof(markers));
            _strings = strings ?? throw new ArgumentNullException(nameof(strings));
        }

        public string ResolveMemberDisplayName(string jid)
        {
            if (_jids.IsSelfLinked(jid))
            {
                return _strings.Get("Chat_SelfFallbackName", "You");
            }

            string resolved = _names.Resolve(jid, ChatContext);
            if (!string.IsNullOrWhiteSpace(resolved) &&
                MeaningfulChatLabel.IsMeaningful(resolved, jid, false, _markers.IsMarkerLabel(resolved)))
            {
                return resolved;
            }

            return _names.Resolve(jid, SenderContext) ?? jid;
        }

        public string ResolveChatDisplayName(string jid) => _names.Resolve(jid, ChatContext);

        public bool IsMeaningfulGroupLabel(string label, string groupJid) =>
            MeaningfulChatLabel.IsMeaningful(label, groupJid, true, _markers.IsMarkerLabel(label));
    }
}
