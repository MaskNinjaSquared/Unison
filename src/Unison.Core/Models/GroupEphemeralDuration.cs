// =============================================================================
// GroupEphemeralDuration
//
// Disappearing-message preset offered while creating a group.
// =============================================================================
namespace Unison.Core.Models
{
    public enum GroupEphemeralDuration
    {
        Off = 0,
        Hours24 = 1,
        Days7 = 2,
        Days90 = 3
    }

    public static class GroupEphemeralDurationExtensions
    {
        /// <summary>The <c>ephemeral expiration</c> the server expects; 0 turns the timer off.</summary>
        public static int ToSeconds(this GroupEphemeralDuration duration)
        {
            switch (duration)
            {
                case GroupEphemeralDuration.Hours24:
                    return 24 * 60 * 60;
                case GroupEphemeralDuration.Days7:
                    return 7 * 24 * 60 * 60;
                case GroupEphemeralDuration.Days90:
                    return 90 * 24 * 60 * 60;
                default:
                    return 0;
            }
        }
    }
}
