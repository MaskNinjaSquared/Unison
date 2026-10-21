using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Unison.Core.Contracts;
using Unison.Core.Models;
using Unison.Core.State;

namespace Unison.Core.Helpers
{
    /// <summary>
    /// Puts the names now known onto the chat rows and their members, in UI passes.
    /// </summary>
    /// <remarks>
    /// Run after anything that learns names: a usync answer, the address book, a group listing.
    /// On frugal devices the work is sliced across dispatcher turns so a single apply cannot hold
    /// the UI thread for hundreds of PropertyChanged notifications. <see cref="Applied"/> is
    /// raised once inside the last UI pass (never with zero).
    /// </remarks>
    public sealed class ChatNameRefresh
    {
        /// <summary>Chat rows processed per UI turn when frugal.</summary>
        public const int FrugalUiSliceSize = 32;

        private static readonly TimeSpan FrugalSliceGap = TimeSpan.FromMilliseconds(24);

        private readonly IDispatcher _ui;
        private readonly IChatStateStore _chats;
        private readonly ContactDisplayNames _names;
        private readonly ISelfMarkerNaming _markers;
        private readonly Func<bool> _preferFrugal;

        public ChatNameRefresh(
            IDispatcher ui,
            IChatStateStore chats,
            ContactDisplayNames names,
            ISelfMarkerNaming markers,
            Func<bool> preferFrugal = null)
        {
            _ui = ui ?? throw new ArgumentNullException(nameof(ui));
            _chats = chats ?? throw new ArgumentNullException(nameof(chats));
            _names = names ?? throw new ArgumentNullException(nameof(names));
            _markers = markers ?? throw new ArgumentNullException(nameof(markers));
            _preferFrugal = preferFrugal ?? (() => false);
        }

        /// <summary>Raised with how many titles and member names changed; never with zero.</summary>
        public event EventHandler<int> Applied;

        public async Task ApplyAsync()
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

                    totalUpdated += ResolvedNameApply.Apply(
                        batch,
                        _names.Resolve,
                        IsMeaningful,
                        membersToo: true);

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

        private bool IsMeaningful(string label, string jid, bool isGroup) =>
            MeaningfulChatLabel.IsMeaningful(label, jid, isGroup, _markers.IsMarkerLabel(label));
    }
}
