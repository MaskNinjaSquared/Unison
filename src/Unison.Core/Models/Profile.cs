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
        public string AvatarUrl { get; set; }
    }
}
