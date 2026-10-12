// =============================================================================
// GroupMemberAvatarAttempt
//
// What one member's picture candidates added up to. A miss is remembered. Any
// failure that might clear on the next try keeps the member in the queue, and
// the last of those answers is the one that is recorded.
// =============================================================================
using Unison.Core.Models;

namespace Unison.Core.Helpers
{
    public sealed class GroupMemberAvatarAttempt
    {
        private bool _sawNotFound;
        private bool _sawTransient;
        private string _lastReason;

        public GroupMemberAvatarFetchResult Picture(string localUri)
        {
            return new GroupMemberAvatarFetchResult { LocalUri = localUri };
        }

        public void NoteNotFound()
        {
            _sawNotFound = true;
            _lastReason = "no-picture";
        }

        public void NoteEmptyDownload()
        {
            _sawTransient = true;
            _lastReason = "download:empty";
        }

        public void NoteTimeout()
        {
            _sawTransient = true;
            _lastReason = "timeout";
        }

        public void NoteOtherFailure(string reason)
        {
            _sawTransient = true;
            _lastReason = reason ?? "no-url";
        }

        public GroupMemberAvatarFetchResult Finish()
        {
            if (_sawNotFound && !_sawTransient)
            {
                return new GroupMemberAvatarFetchResult
                {
                    IsNotFound = true,
                    FailureReason = "no-picture"
                };
            }

            return new GroupMemberAvatarFetchResult
            {
                IsTransientFailure = true,
                FailureReason = _lastReason ?? "no-url"
            };
        }
    }
}
