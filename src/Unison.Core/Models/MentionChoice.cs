namespace Unison.Core.Models
{
    /// <summary>
    /// One row in the composer @ menu: everyone, or a group member.
    /// The name is what the field shows. <see cref="Jid"/> is what the send carries.
    /// </summary>
    public sealed class MentionChoice
    {
        public bool IsAll { get; set; }

        /// <summary>Empty when <see cref="IsAll"/> is true.</summary>
        public string Jid { get; set; }

        public string Name { get; set; }

        /// <summary>Second line: the everyone description, or a phone hint.</summary>
        public string Detail { get; set; }

        public string AvatarUrl { get; set; }
    }
}
