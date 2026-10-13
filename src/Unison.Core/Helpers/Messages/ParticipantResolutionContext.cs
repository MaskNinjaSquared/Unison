using System;
using Unison.Core.Contracts;
using Unison.Core.Contracts.WhatsApp;
using Unison.Core.State;

namespace Unison.Core.Helpers
{
    /// <summary>
    /// The four things it takes to put a name and a face on a group participant.
    /// </summary>
    /// <remarks>
    /// Resolution walks four sources in order - the roster, the account's own name caches, the
    /// Person table, and the 1:1 chat with the same person - and each of those lives behind a
    /// different contract. Passing them as one argument keeps the resolver's signatures readable
    /// and, more importantly, keeps the list of what it is allowed to reach visible in one place.
    /// </remarks>
    public sealed class ParticipantResolutionContext
    {
        public ParticipantResolutionContext(
            IJidResolver jids,
            IContactService contacts,
            IChatStateStore chatState,
            IPersonStore people)
        {
            if (jids == null)
            {
                throw new ArgumentNullException(nameof(jids));
            }

            Jids = jids;
            Contacts = contacts;
            ChatState = chatState;
            People = people;
        }

        public IJidResolver Jids { get; }

        /// <summary>Names the account knows. Null when name resolution is not available yet.</summary>
        public IContactService Contacts { get; }

        /// <summary>The chat list, used to borrow a label or picture from the 1:1 conversation.</summary>
        public IChatStateStore ChatState { get; }

        /// <summary>Local person table. Null in contexts that do not persist people.</summary>
        public IPersonStore People { get; }
    }
}
