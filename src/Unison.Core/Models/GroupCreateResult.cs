// =============================================================================
// GroupCreateResult
//
// Outcome of creating a group or community. The follow-up steps (picture,
// disappearing timer) are separate queries: when one of them fails the group
// still exists, so it is reported next to the JID rather than as a failure.
// =============================================================================
namespace Unison.Core.Models
{
    public sealed class GroupCreateResult
    {
        public const string ReasonEmptySubject = "empty-subject";
        public const string ReasonNotConnected = "not-connected";
        public const string ReasonNoGroup = "no-group";

        public string GroupJid { get; set; }

        /// <summary>Why the create itself failed; null when the group exists.</summary>
        public string FailureReason { get; set; }

        public bool PhotoFailed { get; set; }

        public bool EphemeralFailed { get; set; }

        public bool Succeeded => !string.IsNullOrEmpty(GroupJid);

        public bool HasPartialFailure => PhotoFailed || EphemeralFailed;

        public static GroupCreateResult Failed(string reason) =>
            new GroupCreateResult { FailureReason = reason };
    }
}
