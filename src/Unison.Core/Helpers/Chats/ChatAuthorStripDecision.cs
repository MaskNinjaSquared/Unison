using System;

namespace Unison.Core.Helpers
{
    /// <summary>What a list row should do with one attempt at naming the last sender.</summary>
    public enum ChatAuthorStripAction
    {
        /// <summary>Leave the strip as it is.</summary>
        Keep,

        /// <summary>Write the resolved name (and remember it as the row's sender name).</summary>
        ShowName,

        /// <summary>Write the sender's number as a stand-in until a name is known.</summary>
        ShowNumber
    }

    /// <summary>
    /// The one rule behind the group author strip: a name beats a number, a number beats nothing,
    /// and neither ever downgrades what the row already shows.
    /// </summary>
    /// <remarks>
    /// Names reach a chat row from several directions — the history chunk, a live envelope, the
    /// group roster, the address book — and they do not arrive in a useful order. Keeping the
    /// choice in one place is what stops the strip from oscillating between two valid labels or
    /// from putting an identifier back over a name that was already resolved.
    /// </remarks>
    public static class ChatAuthorStripDecision
    {
        /// <param name="currentSenderName">Name the row already carries, if any.</param>
        /// <param name="currentIsRealName">Whether that label is a name rather than a JID/number.</param>
        /// <param name="resolvedName">Name this pass managed to resolve, if any.</param>
        /// <param name="resolvedIsRealName">Whether the resolved label is a name.</param>
        /// <param name="hasNumber">Whether a phone number is known for the sender.</param>
        public static ChatAuthorStripAction Decide(
            string currentSenderName,
            bool currentIsRealName,
            string resolvedName,
            bool resolvedIsRealName,
            bool hasNumber)
        {
            if (resolvedIsRealName && !string.IsNullOrWhiteSpace(resolvedName))
            {
                if (string.Equals(resolvedName, currentSenderName, StringComparison.Ordinal))
                {
                    return ChatAuthorStripAction.Keep;
                }

                return currentIsRealName
                    ? ChatAuthorStripAction.Keep
                    : ChatAuthorStripAction.ShowName;
            }

            return !currentIsRealName && hasNumber
                ? ChatAuthorStripAction.ShowNumber
                : ChatAuthorStripAction.Keep;
        }

        /// <summary>
        /// Whether a row that <see cref="Decide"/> wants to <see cref="ChatAuthorStripAction.Keep"/>
        /// is in fact showing something other than the name it already knows.
        /// </summary>
        /// <remarks>
        /// The rendered prefix and the sender name are two fields, and they are persisted
        /// independently — a tip that carries no push name writes digits into the prefix while the
        /// store carries the previously resolved name forward beside it. Decide only ever reads
        /// the name, so it sees a named row, returns Keep, and the digits become permanent: the
        /// one pass that could rewrite the prefix is also the one that declines to.
        /// </remarks>
        /// <param name="currentAuthorPrefix">The prefix the row is rendering, e.g. "Ana: ".</param>
        /// <param name="currentSenderName">The name the row already knows for that sender.</param>
        public static bool AuthorPrefixDisagrees(string currentAuthorPrefix, string currentSenderName)
        {
            if (string.IsNullOrWhiteSpace(currentSenderName))
            {
                return false;
            }

            return string.IsNullOrEmpty(currentAuthorPrefix) ||
                   !currentAuthorPrefix.StartsWith(
                       currentSenderName.Trim(),
                       StringComparison.Ordinal);
        }
    }
}
