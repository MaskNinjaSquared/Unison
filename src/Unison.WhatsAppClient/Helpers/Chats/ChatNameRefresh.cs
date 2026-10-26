using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Unison.Core.Contracts;
using Unison.Core.Contracts.WhatsApp;
using Unison.Core.Models;
using Unison.Core.State;
using Unison.Core.Helpers;

namespace Unison.WhatsAppClient.Helpers
{
    /// <summary>
    /// Puts the names now known onto the chat rows and their members, in UI passes.
    /// </summary>
    /// <remarks>
    /// Run after anything that learns names: a usync answer, the address book, a group listing.
    /// Group titles go through <see cref="IGroupNaming"/> so a cached subject wins over the
    /// address book. On frugal devices the work is sliced across dispatcher turns so a single
    /// apply cannot hold the UI thread for hundreds of PropertyChanged notifications.
    /// Concurrent callers coalesce into one in-flight walk (plus at most one follow-up if more
    /// tickets arrived while it ran) so usync / subject-cache storms do not stack full catalogue
    /// walks. <see cref="Applied"/> is raised once inside the last UI pass (never with zero).
    /// </remarks>
    public sealed class ChatNameRefresh
    {
        /// <summary>Chat rows processed per UI turn when frugal.</summary>
        public const int FrugalUiSliceSize = 32;

        private static readonly TimeSpan FrugalSliceGap = TimeSpan.FromMilliseconds(24);

        private readonly IDispatcher _ui;
        private readonly IChatStateStore _chats;
        private readonly ContactDisplayNames _names;
        private readonly IGroupNaming _groupNaming;
        private readonly ISelfMarkerNaming _markers;
        private readonly Func<bool> _preferFrugal;
        private readonly SemaphoreSlim _applyGate = new SemaphoreSlim(1, 1);
        private int _applyTickets;

        public ChatNameRefresh(
            IDispatcher ui,
            IChatStateStore chats,
            ContactDisplayNames names,
            ISelfMarkerNaming markers,
            IGroupNaming groupNaming,
            Func<bool> preferFrugal = null)
        {
            _ui = ui ?? throw new ArgumentNullException(nameof(ui));
            _chats = chats ?? throw new ArgumentNullException(nameof(chats));
            _names = names ?? throw new ArgumentNullException(nameof(names));
            _markers = markers ?? throw new ArgumentNullException(nameof(markers));
            _groupNaming = groupNaming ?? throw new ArgumentNullException(nameof(groupNaming));
            _preferFrugal = preferFrugal ?? (() => false);
        }

        /// <summary>Raised with how many titles and member names changed; never with zero.</summary>
        public event EventHandler<int> Applied;

        public async Task ApplyAsync()
        {
            Interlocked.Increment(ref _applyTickets);
            await _applyGate.WaitAsync().ConfigureAwait(false);
            try
            {
                // One walk covers every ticket that arrived before we started; if more land
                // during the walk, Exchange sees them and we do a single follow-up pass.
                while (Interlocked.Exchange(ref _applyTickets, 0) > 0)
                {
                    await ApplyCoreAsync().ConfigureAwait(false);
                }
            }
            finally
            {
                _applyGate.Release();
            }
        }

        private async Task ApplyCoreAsync()
        {
            bool frugal = _preferFrugal();
            int sliceSize = frugal ? FrugalUiSliceSize : int.MaxValue;
            int totalUpdated = 0;
            int start = 0;

            while (true)
            {
                int sliceStart = start;
                bool done = false;

                await _ui.RunAsync(() =>
                {
                    IList<ChatItem> chats = _chats.Chats;
                    int chatCount = chats.Count;
                    if (sliceStart >= chatCount)
                    {
                        done = true;
                        if (totalUpdated > 0)
                        {
                            Applied?.Invoke(this, totalUpdated);
                        }

                        return;
                    }

                    int end = sliceSize == int.MaxValue
                        ? chatCount
                        : Math.Min(chatCount, sliceStart + sliceSize);
                    bool more = end < chatCount;

                    var batch = new List<ChatItem>(end - sliceStart);
                    for (int i = sliceStart; i < end; i++)
                    {
                        batch.Add(chats[i]);
                    }

                    // Frugal (Mobile): never walk GroupMembers during list name refresh —
                    // 500+ member chats made scroll freeze after open. Member names stay on
                    // roster apply / Members pivot.
                    totalUpdated += ResolvedNameApply.Apply(
                        batch,
                        ResolveForApply,
                        IsMeaningful,
                        membersToo: !_preferFrugal());

                    if (!more)
                    {
                        done = true;
                        if (totalUpdated > 0)
                        {
                            Applied?.Invoke(this, totalUpdated);
                        }
                    }
                }).ConfigureAwait(false);

                if (done)
                {
                    break;
                }

                start = sliceStart + sliceSize;
                await Task.Delay(FrugalSliceGap).ConfigureAwait(false);
            }
        }

        /// <summary>
        /// Titles only (no member relabel): the pass that runs once after persisted chats
        /// rehydrate, so phone-number placeholders become resolved names before the list paints.
        /// </summary>
        /// <returns>How many titles changed; zero when nothing moved.</returns>
        public async Task<int> NormalizePersistedTitlesAsync()
        {
            int updated = 0;
            await _ui.RunAsync(() =>
            {
                updated = ResolvedNameApply.Apply(
                    _chats.Chats,
                    ResolveForApply,
                    IsMeaningful,
                    membersToo: false);
                if (updated > 0)
                {
                    Applied?.Invoke(this, updated);
                }
            }).ConfigureAwait(false);
            return updated;
        }

        private string ResolveForApply(string jid, string context)
        {
            if (string.Equals(context, "chat", StringComparison.Ordinal) &&
                JidHelper.IsGroupJid(jid))
            {
                return _groupNaming.ResolveChatDisplayName(jid);
            }

            return _names.Resolve(jid, context);
        }

        private bool IsMeaningful(string label, string jid, bool isGroup) =>
            MeaningfulChatLabel.IsMeaningful(label, jid, isGroup, _markers.IsMarkerLabel(label));
    }
}
