// =============================================================================
// IncomingSenderResolution
//
// Who wrote this message, and whether that is us. The pump used to answer it
// inline with two nearly identical branches (group / DM); spelling it once keeps
// the offline summary, the toast and the ChatMessage row on the same answer.
// =============================================================================
using System;

namespace Unison.Core.Helpers
{
    /// <summary>
    /// The display name and from-me flag an incoming envelope should put on the row.
    /// </summary>
    public struct IncomingSenderResolution
    {
        public string SenderName;
        public bool IsFromMe;
    }

    public static class IncomingSenderResolver
    {
        /// <summary>
        /// Resolves the author label and the true from-me bit from what the envelope
        /// already told us. Lookups (GetResolvedName) stay with the caller — this only
        /// picks between the answers they already have.
        /// </summary>
        /// <param name="accountDisplayName">
        /// Our own push name when known; otherwise the caller passes the localized
        /// "You" fallback as <paramref name="selfFallbackName"/>.
        /// </param>
        public static IncomingSenderResolution Resolve(
            bool isGroup,
            bool envelopeFromMe,
            string accountDisplayName,
            string selfFallbackName,
            string participantResolvedName,
            string chatResolvedName,
            bool hasParticipant)
        {
            string selfLabel = FirstNonEmpty(accountDisplayName, selfFallbackName) ?? "You";

            if (isGroup)
            {
                if (envelopeFromMe)
                {
                    return new IncomingSenderResolution
                    {
                        SenderName = selfLabel,
                        IsFromMe = true
                    };
                }

                return new IncomingSenderResolution
                {
                    SenderName = hasParticipant
                        ? participantResolvedName
                        : chatResolvedName,
                    IsFromMe = false
                };
            }

            if (envelopeFromMe)
            {
                return new IncomingSenderResolution
                {
                    SenderName = selfLabel,
                    IsFromMe = true
                };
            }

            return new IncomingSenderResolution
            {
                SenderName = chatResolvedName,
                IsFromMe = false
            };
        }

        private static string FirstNonEmpty(params string[] values)
        {
            if (values == null)
            {
                return null;
            }

            for (int i = 0; i < values.Length; i++)
            {
                if (!string.IsNullOrWhiteSpace(values[i]))
                {
                    return values[i].Trim();
                }
            }

            return null;
        }
    }
}
