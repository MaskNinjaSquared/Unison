using Unison.Core.State;

namespace Unison.Core.Helpers
{
    /// <summary>
    /// Files a usync LID/phone pair into the alias table when the policy accepts it.
    /// </summary>
    /// <remarks>
    /// Socket registration and self-identity healing stay with the host: they need the live
    /// session and the auth state. This type only owns the table write.
    /// </remarks>
    public sealed class UsyncAliasFiling
    {
        private readonly JidAliasTable _aliases;

        public UsyncAliasFiling(JidAliasTable aliases)
        {
            _aliases = aliases ?? throw new System.ArgumentNullException(nameof(aliases));
        }

        public UsyncAliasFilingResult TryFile(string userJid, string lidJid)
        {
            string normalizedUser = JidHelper.Normalize(userJid);
            string normalizedLid = JidHelper.Normalize(lidJid);
            bool wasAlreadyAliased =
                !string.IsNullOrEmpty(normalizedLid) && _aliases.ContainsKey(normalizedLid);

            if (string.IsNullOrEmpty(normalizedUser) ||
                string.IsNullOrEmpty(normalizedLid) ||
                !AliasPairPolicy.TryAcceptPair(
                    normalizedLid,
                    normalizedUser,
                    _aliases,
                    out string acceptedLid,
                    out string acceptedPn))
            {
                return new UsyncAliasFilingResult(false, wasAlreadyAliased, null, null);
            }

            AliasPairFiling.FileBothWays(_aliases, acceptedLid, acceptedPn);
            return new UsyncAliasFilingResult(true, wasAlreadyAliased, acceptedLid, acceptedPn);
        }
    }

    public sealed class UsyncAliasFilingResult
    {
        public UsyncAliasFilingResult(
            bool filed,
            bool wasAlreadyAliased,
            string acceptedLid,
            string acceptedPn)
        {
            Filed = filed;
            WasAlreadyAliased = wasAlreadyAliased;
            AcceptedLid = acceptedLid;
            AcceptedPn = acceptedPn;
        }

        public bool Filed { get; }

        public bool WasAlreadyAliased { get; }

        public string AcceptedLid { get; }

        public string AcceptedPn { get; }
    }
}
