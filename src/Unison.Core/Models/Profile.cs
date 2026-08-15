namespace Unison.Core.Models
{
    /// <summary>
    /// Logged-in user profile for UI/session (not protocol auth keys).
    /// <see cref="AvatarUrl"/> null/empty means no photo.
    /// </summary>
    public class Profile
    {
        public string Id { get; set; }
        public string Lid { get; set; }
        public string Name { get; set; }
        /// <summary>Account phone digits; UI placeholder when <see cref="Name"/> is empty.</summary>
        public string Phone { get; set; }
        public string AvatarUrl { get; set; }
    }
}
