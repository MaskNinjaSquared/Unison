using System.Collections.Generic;
using Unison.Core.Contracts.WhatsApp;
using Unison.Core.Models;

namespace Unison.Core.Tests.Helpers
{
    /// <summary>Counts catalogue saves instead of writing them.</summary>
    internal sealed class FakeCatalog : IChatCatalogPersistence
    {
        public int Schedules { get; private set; }

        public List<ChatItem> PersistedRows { get; } = new List<ChatItem>();

        public void Schedule() => Schedules++;

        public void PersistRows(IList<ChatItem> chats)
        {
            if (chats != null)
            {
                PersistedRows.AddRange(chats);
            }
        }
    }
}
