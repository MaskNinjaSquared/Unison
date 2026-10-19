using System;
using System.Threading.Tasks;
using Unison.Core.Contracts;
using Unison.Core.State;

namespace Unison.Core.Helpers
{
    /// <summary>
    /// Puts the names now known onto the chat rows and their members, in one UI pass.
    /// </summary>
    /// <remarks>
    /// Run after anything that learns names: a usync answer, the address book, a group listing.
    /// <see cref="Applied"/> is raised inside the pass, so listeners that touch bound state are
    /// already on the UI thread.
    /// </remarks>
    public sealed class ChatNameRefresh
    {
        private readonly IDispatcher _ui;
        private readonly IChatStateStore _chats;
        private readonly ContactDisplayNames _names;
        private readonly ISelfMarkerNaming _markers;

        public ChatNameRefresh(
            IDispatcher ui,
            IChatStateStore chats,
            ContactDisplayNames names,
            ISelfMarkerNaming markers)
        {
            _ui = ui ?? throw new ArgumentNullException(nameof(ui));
            _chats = chats ?? throw new ArgumentNullException(nameof(chats));
            _names = names ?? throw new ArgumentNullException(nameof(names));
            _markers = markers ?? throw new ArgumentNullException(nameof(markers));
        }

        /// <summary>Raised with how many titles and member names changed; never with zero.</summary>
        public event EventHandler<int> Applied;

        public Task ApplyAsync()
        {
            return _ui.RunAsync(() =>
            {
                int updated = ResolvedNameApply.Apply(_chats.Chats, _names.Resolve, IsMeaningful, membersToo: true);
                if (updated > 0)
                {
                    Applied?.Invoke(this, updated);
                }
            });
        }

        private bool IsMeaningful(string label, string jid, bool isGroup) =>
            MeaningfulChatLabel.IsMeaningful(label, jid, isGroup, _markers.IsMarkerLabel(label));
    }
}
