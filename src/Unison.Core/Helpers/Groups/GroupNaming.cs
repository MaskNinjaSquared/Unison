using System;
using Unison.Core.Contracts;
using Unison.Core.Contracts.WhatsApp;
using Unison.Core.State;

namespace Unison.Core.Helpers
{
    /// <summary>
    /// The labels the group flows put on members and rows, and whether a group label is a real
    /// name. Group subjects are preferred for chat titles so AddressBook / push names cannot
    /// revert a live rename; members still resolve through <see cref="ContactDisplayNames"/>.
    /// </summary>
    public sealed class GroupNaming : IGroupNaming
    {
        private const string ChatContext = "chat";
        private const string SenderContext = "sender";

        private readonly ContactDisplayNames _names;
        private readonly DisplayNameTable _subjects;
        private readonly IJidResolver _jids;
        private readonly ISelfMarkerNaming _markers;
        private readonly IStringResources _strings;

        public GroupNaming(
            ContactDisplayNames names,
            IJidResolver jids,
            ISelfMarkerNaming markers,
            IStringResources strings,
            DisplayNameTable subjects = null)
        {
            _names = names ?? throw new ArgumentNullException(nameof(names));
            _jids = jids ?? throw new ArgumentNullException(nameof(jids));
            _markers = markers ?? throw new ArgumentNullException(nameof(markers));
            _strings = strings ?? throw new ArgumentNullException(nameof(strings));
            _subjects = subjects;
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

        public string ResolveChatDisplayName(string jid)
        {
            if (JidHelper.IsGroupJid(jid) && _subjects != null)
            {
                string subject;
                if (_subjects.TryGetValue(jid, out subject) && IsMeaningfulGroupLabel(subject, jid))
                {
                    return subject;
                }
            }

            return _names.Resolve(jid, ChatContext);
        }

        public bool IsMeaningfulGroupLabel(string label, string groupJid) =>
            MeaningfulChatLabel.IsMeaningful(label, groupJid, true, _markers.IsMarkerLabel(label));
    }
}
