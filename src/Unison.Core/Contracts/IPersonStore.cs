using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Unison.Core.Models;

namespace Unison.Core.Contracts
{
    /// <summary>
    /// SQLite-backed contact / participant store (not the logged-in Profile).
    /// Keyed by canonical JID; <see cref="Person.Phone"/> is indexed for address-book match.
    /// </summary>
    public interface IPersonStore
    {
        /// <summary>
        /// A person row was inserted or updated (payload is the normalized JID). Lets projections
        /// refresh only what depends on that JID instead of sweeping everything. May fire off the
        /// UI thread — marshal before touching bound state.
        /// </summary>
        event EventHandler<string> PersonChanged;

        Task InitializeAsync();

        Task<Person> GetAsync(string jid);

        /// <summary>
        /// In-memory only (no disk I/O). Null when the JID has not been loaded yet.
        /// </summary>
        Person TryGetCached(string jid);

        /// <summary>
        /// Inserts or updates when <see cref="Person.RequiresUpdate"/> is true.
        /// <paramref name="source"/> never downgrades. Address-book source may replace Name;
        /// a lower source cannot. Empty name/avatar/phone means leave unchanged.
        /// Returns true when a write occurred.
        /// </summary>
        Task<bool> UpsertIfChangedAsync(
            string jid,
            string name,
            string avatarUrl,
            string phone,
            PersonSource source);

        /// <summary>Indexed lookup: people whose stored phone digits equal <paramref name="digits"/>.</summary>
        Task<IReadOnlyList<Person>> FindByPhoneAsync(string digits);

        /// <summary>Rows that already have a phone (one scan; used by the address-book overlay).</summary>
        Task<IReadOnlyList<Person>> ListWithPhoneAsync();

        /// <summary>
        /// Replaces this person's group memberships for <paramref name="groupJid"/>'s roster write:
        /// upsert each (person, group) pair from the listing.
        /// </summary>
        Task ReplaceGroupMembershipsAsync(
            string groupJid,
            IReadOnlyList<PersonGroupMembership> members);

        /// <summary>Groups this person is in (for groups-in-common UI).</summary>
        Task<IReadOnlyList<PersonGroupMembership>> ListGroupsForPersonAsync(string personJid);
    }
}
