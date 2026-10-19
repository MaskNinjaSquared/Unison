// =============================================================================
// FirstNonBlankText
//
// The first usable string in a list of fallbacks, trimmed. Blank and missing
// entries are skipped rather than returned as empty.
// =============================================================================
namespace Unison.Core.Helpers
{
    public static class FirstNonBlankText
    {
        public static string Pick(params string[] values)
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
