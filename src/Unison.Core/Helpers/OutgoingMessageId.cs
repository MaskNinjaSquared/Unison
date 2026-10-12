// =============================================================================
// OutgoingMessageId
//
// The id stamped on a message before it leaves. The same id is reused across the
// one reconnect a text send is allowed, so a retry cannot mint a second bubble.
// =============================================================================
using System.Text;

namespace Unison.Core.Helpers
{
    public static class OutgoingMessageId
    {
        public const int RandomByteCount = 10;

        /// <summary>
        /// <c>3EB0</c> plus the uppercase hex of <paramref name="randomBytes"/>.
        /// </summary>
        /// <remarks>
        /// The prefix is what WhatsApp companions use for ids they minted themselves.
        /// The bytes come from the caller so this stays a formatting rule.
        /// </remarks>
        public static string FromRandomBytes(byte[] randomBytes)
        {
            var builder = new StringBuilder(24);
            builder.Append("3EB0");
            if (randomBytes == null)
            {
                return builder.ToString();
            }

            for (int i = 0; i < randomBytes.Length; i++)
            {
                builder.Append(randomBytes[i].ToString("X2"));
            }

            return builder.ToString();
        }
    }
}
