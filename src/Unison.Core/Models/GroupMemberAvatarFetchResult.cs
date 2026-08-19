namespace Unison.Core.Models
{
    /// <summary>
    /// Outcome of one group-member picture GET. An empty URI with
    /// <see cref="IsNotFound"/> is a real answer (no photo) — callers stamp
    /// <see cref="GroupMember.AvatarFetchedAtUtc"/> and must not retry soon.
    /// </summary>
    public sealed class GroupMemberAvatarFetchResult
    {
        public string LocalUri { get; set; }

        public bool IsNotFound { get; set; }

        public bool IsTransientFailure { get; set; }

        public string FailureReason { get; set; }

        public bool HasPicture => !string.IsNullOrWhiteSpace(LocalUri);
    }
}
