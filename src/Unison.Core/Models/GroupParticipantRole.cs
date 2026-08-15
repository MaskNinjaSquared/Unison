namespace Unison.Core.Models
{
    /// <summary>
    /// Role of the logged-in user (or a participant) inside a WhatsApp group.
    /// Maps from the <c>admin</c> attribute on <c>participant</c> nodes in group metadata.
    /// </summary>
    public enum GroupParticipantRole
    {
        /// <summary>Regular member (no <c>admin</c> attr).</summary>
        Member = 0,

        /// <summary>Group admin (<c>admin="admin"</c>).</summary>
        Admin = 1,

        /// <summary>Group creator / owner (<c>admin="superadmin"</c>).</summary>
        SuperAdmin = 2
    }
}
