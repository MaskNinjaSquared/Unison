// =============================================================================
// StatusAudiencePolicy
//
// Which address space a status@broadcast send travels in, and the recipient
// list expressed in that one space.
//
// A group gets its addressing from server metadata. status@broadcast has no
// metadata at all, so Unison used to send whatever the caller handed over -
// in practice the raw chat list, where some people are stored under their LID
// and some under their phone number. The stanza then carried a LID-signed
// sender key, a participant list spanning both spaces, and no addressing_mode
// attribute to reconcile them. The server answers 400 (bad-request).
//
// So the audience is collapsed into one space before any device lookup. The
// space is whichever one can name everybody, LID first: Baileys leaves a
// status list exactly as the caller gave it and signs with the LID identity,
// because its group addressing mode falls back to "lid" when there is no
// group metadata - which is always the case for status (rc14
// src/Socket/messages-send.ts, groupAddressingMode / groupSenderIdentity).
//
// Phone space was tried on device and refused the same way, so requiring it
// buys nothing and costs every recipient who has no phone number; the
// PhoneRequired mode is kept only so that experiment can be re-run.
//
// Whoever is dropped is named in the log, because a status that quietly misses
// a recipient is worse than one that visibly fails.
//
// Ports: fills the gap left by rc14 relayMessage, which sets addressing_mode
// only when isGroup (src/Socket/messages-send.ts).
// =============================================================================
using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Unison.Baileys.Protocol;
using Unison.Socket.Signal;
using Unison.Socket.WABinary;

namespace Unison.Socket.Messages
{
    /// <summary>The audience of one status send, in a single address space.</summary>
    public sealed class StatusAudience
    {
        public StatusAudience(
            IReadOnlyList<string> recipients,
            string addressingMode,
            string source,
            IReadOnlyList<string> dropped,
            bool normalized = true)
        {
            Recipients = recipients ?? new List<string>();
            AddressingMode = addressingMode;
            Source = source;
            Dropped = dropped ?? new List<string>();
            Normalized = normalized;
        }

        /// <summary>Recipients, all in <see cref="AddressingMode"/> space, one entry per person.</summary>
        public IReadOnlyList<string> Recipients { get; }

        /// <summary>"lid" or "pn" - what the stanza declares and what the list holds.</summary>
        public string AddressingMode { get; }

        /// <summary>How the mode was chosen, for the log.</summary>
        public string Source { get; }

        /// <summary>Recipients left out because they exist in neither usable space.</summary>
        public IReadOnlyList<string> Dropped { get; }

        /// <summary>
        /// False when the list was passed through untouched because no mapping store was
        /// available. The caller must not then declare an addressing mode it cannot back up.
        /// </summary>
        public bool Normalized { get; }

        public string Describe()
        {
            return "mode=" + AddressingMode + " (" + Source + "); recipients=" + Recipients.Count +
                   "; dropped=" + Dropped.Count +
                   (Dropped.Count > 0 ? " [" + string.Join(", ", Dropped) + "]" : string.Empty);
        }
    }

    /// <summary>Which address space the caller is willing to end up in.</summary>
    public enum StatusAddressSpace
    {
        /// <summary>
        /// Phone space or nobody. Recipients with no phone number are dropped and named;
        /// LID is used only when phone space would leave the audience completely empty.
        /// </summary>
        PhoneRequired,

        /// <summary>Whichever single space reaches the most people, LID considered first.</summary>
        WidestLidFirst
    }

    public static class StatusAudiencePolicy
    {
        /// <summary>
        /// Collapses a mixed recipient list into one address space.
        /// </summary>
        /// <param name="recipients">Raw recipient JIDs, either space, possibly duplicated across both.</param>
        /// <param name="meId">The account's phone-number JID.</param>
        /// <param name="meLid">The account's LID, or null when it has none.</param>
        /// <param name="pnToLid">Phone number user parts to LID user parts; may query the server.</param>
        /// <param name="lidToPn">LID user parts to phone number user parts; cache-only.</param>
        /// <param name="space">
        /// Whether phone space is required (what status@broadcast accepts) or merely one of
        /// two candidates.
        /// </param>
        public static async Task<StatusAudience> ResolveAsync(
            IEnumerable<string> recipients,
            string meId,
            string meLid,
            Func<IReadOnlyList<string>, Task<IReadOnlyList<LidMapping>>> pnToLid,
            Func<IReadOnlyList<string>, Task<IReadOnlyList<LidMapping>>> lidToPn,
            StatusAddressSpace space = StatusAddressSpace.WidestLidFirst)
        {
            var pnUsers = new List<string>();
            var lidUsers = new List<string>();
            CollectUsers(recipients, pnUsers, lidUsers);

            if (pnUsers.Count == 0 && lidUsers.Count == 0)
            {
                return new StatusAudience(new List<string>(), "pn", "empty", new List<string>());
            }

            // Our own pair is known without asking anyone, and asking would waste a usync slot
            // on the one account whose mapping we are certain of.
            var pnToLidUsers = new Dictionary<string, string>(StringComparer.Ordinal);
            var lidToPnUsers = new Dictionary<string, string>(StringComparer.Ordinal);
            AddSelfPair(meId, meLid, pnToLidUsers, lidToPnUsers);

            // Without a LID of our own we cannot sign in LID space, so phone space is the only
            // answer whether or not every LID resolves.
            if (string.IsNullOrEmpty(JidUtils.GetUser(meLid)))
            {
                Projection only = await BuildPnAsync(pnUsers, lidUsers, lidToPnUsers, lidToPn)
                    .ConfigureAwait(false);
                return new StatusAudience(
                    only.List,
                    "pn",
                    only.Missing.Count == 0 ? "pn-no-lid-of-our-own" : "pn-partial-no-lid-of-our-own",
                    only.Missing);
            }

            if (space == StatusAddressSpace.PhoneRequired)
            {
                Projection required = await BuildPnAsync(pnUsers, lidUsers, lidToPnUsers, lidToPn)
                    .ConfigureAwait(false);
                if (required.List.Count > 0)
                {
                    return new StatusAudience(
                        required.List,
                        "pn",
                        required.Missing.Count == 0 ? "pn-required" : "pn-required-partial",
                        required.Missing);
                }

                // Phone space reaches nobody at all. Sending to no one is not a kinder
                // failure than sending in the space we can actually name people in.
                Projection salvage = await BuildLidAsync(pnUsers, lidUsers, pnToLidUsers, pnToLid)
                    .ConfigureAwait(false);
                return new StatusAudience(salvage.List, "lid", "lid-no-phone-space", salvage.Missing);
            }

            Projection lid = await BuildLidAsync(pnUsers, lidUsers, pnToLidUsers, pnToLid)
                .ConfigureAwait(false);
            if (lid.Missing.Count == 0)
            {
                return new StatusAudience(lid.List, "lid", "lid-complete", lid.Missing);
            }

            // Some number has no LID. Phone space may still hold everyone, so price both
            // before giving up on anybody.
            Projection pn = await BuildPnAsync(pnUsers, lidUsers, lidToPnUsers, lidToPn)
                .ConfigureAwait(false);
            if (pn.Missing.Count == 0)
            {
                return new StatusAudience(pn.List, "pn", "pn-complete", pn.Missing);
            }

            return lid.List.Count >= pn.List.Count
                ? new StatusAudience(lid.List, "lid", "lid-partial", lid.Missing)
                : new StatusAudience(pn.List, "pn", "pn-partial", pn.Missing);
        }

        /// <summary>One candidate answer: the list in a space, and who could not be put there.</summary>
        private sealed class Projection
        {
            public List<string> List;
            public List<string> Missing;
        }

        private static async Task<Projection> BuildLidAsync(
            IReadOnlyList<string> pnUsers,
            IReadOnlyList<string> lidUsers,
            Dictionary<string, string> pnToLidUsers,
            Func<IReadOnlyList<string>, Task<IReadOnlyList<LidMapping>>> pnToLid)
        {
            await FillAsync(pnToLid, pnUsers, pnToLidUsers, JidUtils.ServerWhatsApp, takeLid: true)
                .ConfigureAwait(false);

            List<string> missing;
            List<string> list = Project(pnUsers, lidUsers, pnToLidUsers, JidUtils.ServerLid, out missing);
            return new Projection { List = list, Missing = missing };
        }

        private static async Task<Projection> BuildPnAsync(
            IReadOnlyList<string> pnUsers,
            IReadOnlyList<string> lidUsers,
            Dictionary<string, string> lidToPnUsers,
            Func<IReadOnlyList<string>, Task<IReadOnlyList<LidMapping>>> lidToPn)
        {
            await FillAsync(lidToPn, lidUsers, lidToPnUsers, JidUtils.ServerLid, takeLid: false)
                .ConfigureAwait(false);

            List<string> missing;
            List<string> list = Project(lidUsers, pnUsers, lidToPnUsers, JidUtils.ServerWhatsApp, out missing);
            return new Projection { List = list, Missing = missing };
        }

        /// <summary>Splits raw JIDs into phone and LID user parts, each kept once.</summary>
        private static void CollectUsers(
            IEnumerable<string> recipients,
            List<string> pnUsers,
            List<string> lidUsers)
        {
            if (recipients == null)
            {
                return;
            }

            var seenPn = new HashSet<string>(StringComparer.Ordinal);
            var seenLid = new HashSet<string>(StringComparer.Ordinal);

            foreach (string raw in recipients)
            {
                if (string.IsNullOrWhiteSpace(raw))
                {
                    continue;
                }

                string user = JidUtils.GetUser(raw);
                if (string.IsNullOrEmpty(user))
                {
                    continue;
                }

                if (JidUtils.IsAnyLid(raw))
                {
                    if (seenLid.Add(user))
                    {
                        lidUsers.Add(user);
                    }
                }
                else if (JidUtils.IsAnyPn(raw) && seenPn.Add(user))
                {
                    pnUsers.Add(user);
                }
            }
        }

        private static void AddSelfPair(
            string meId,
            string meLid,
            Dictionary<string, string> pnToLidUsers,
            Dictionary<string, string> lidToPnUsers)
        {
            string meUser = JidUtils.GetUser(meId);
            string meLidUser = JidUtils.GetUser(meLid);
            if (string.IsNullOrEmpty(meUser) || string.IsNullOrEmpty(meLidUser))
            {
                return;
            }

            pnToLidUsers[meUser] = meLidUser;
            lidToPnUsers[meLidUser] = meUser;
        }

        /// <summary>Asks the resolver for the users we do not already have a pair for.</summary>
        private static async Task FillAsync(
            Func<IReadOnlyList<string>, Task<IReadOnlyList<LidMapping>>> resolver,
            IReadOnlyList<string> users,
            Dictionary<string, string> known,
            string askServer,
            bool takeLid)
        {
            if (resolver == null)
            {
                return;
            }

            // The mapping store reads full JIDs, not bare user parts.
            var ask = new List<string>();
            foreach (string user in users)
            {
                if (!known.ContainsKey(user))
                {
                    ask.Add(WA.JidEncode(user, askServer, 0));
                }
            }

            if (ask.Count == 0)
            {
                return;
            }

            IReadOnlyList<LidMapping> pairs = await resolver(ask).ConfigureAwait(false);
            if (pairs == null)
            {
                return;
            }

            foreach (LidMapping pair in pairs)
            {
                if (pair == null)
                {
                    continue;
                }

                string lidUser = JidUtils.GetUser(pair.Lid);
                string pnUser = JidUtils.GetUser(pair.Pn);
                if (string.IsNullOrEmpty(lidUser) || string.IsNullOrEmpty(pnUser))
                {
                    continue;
                }

                known[takeLid ? pnUser : lidUser] = takeLid ? lidUser : pnUser;
            }
        }

        /// <summary>
        /// Builds the single-space list: everyone already in the target space, plus everyone we
        /// could translate into it. Device zero, because a status is addressed per person and the
        /// device fan-out is usync's job.
        /// </summary>
        private static List<string> Project(
            IReadOnlyList<string> needTranslating,
            IReadOnlyList<string> alreadyThere,
            Dictionary<string, string> translations,
            string server,
            out List<string> missing)
        {
            var result = new List<string>();
            var seen = new HashSet<string>(StringComparer.Ordinal);
            missing = new List<string>();

            foreach (string user in alreadyThere)
            {
                if (seen.Add(user))
                {
                    result.Add(WA.JidEncode(user, server, 0));
                }
            }

            foreach (string user in needTranslating)
            {
                string translated;
                if (!translations.TryGetValue(user, out translated) || string.IsNullOrEmpty(translated))
                {
                    missing.Add(user);
                    continue;
                }

                if (seen.Add(translated))
                {
                    result.Add(WA.JidEncode(translated, server, 0));
                }
            }

            return result;
        }
    }
}
