using System;
using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace Unison.Core.Models
{
    /// <summary>
    /// One participant of a group chat, persisted on <see cref="ChatItem.GroupMembers"/>.
    /// </summary>
    public sealed class GroupMember : INotifyPropertyChanged
    {
        public event PropertyChangedEventHandler PropertyChanged;

        private string _jid;
        public string Jid
        {
            get => _jid;
            set { if (_jid == value) return; _jid = value; OnPropertyChanged(); }
        }

        private string _phoneNumber;
        public string PhoneNumber
        {
            get => _phoneNumber;
            set { if (_phoneNumber == value) return; _phoneNumber = value; OnPropertyChanged(); }
        }

        private string _lid;
        public string Lid
        {
            get => _lid;
            set { if (_lid == value) return; _lid = value; OnPropertyChanged(); }
        }

        private string _displayName;
        public string DisplayName
        {
            get => _displayName;
            set { if (_displayName == value) return; _displayName = value; OnPropertyChanged(); }
        }

        private GroupParticipantRole _role = GroupParticipantRole.Member;
        public GroupParticipantRole Role
        {
            get => _role;
            set
            {
                if (_role == value) return;
                _role = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(IsAdmin));
            }
        }

        [Newtonsoft.Json.JsonIgnore]
        public bool IsAdmin =>
            _role == GroupParticipantRole.Admin ||
            _role == GroupParticipantRole.SuperAdmin;

        private string _avatarUrl;
        /// <summary>Local cache URI when a picture is known (1:1 chat or a later fetch).</summary>
        public string AvatarUrl
        {
            get => _avatarUrl;
            set { if (_avatarUrl == value) return; _avatarUrl = value; OnPropertyChanged(); }
        }

        private DateTime? _avatarFetchedAtUtc;
        /// <summary>
        /// Last picture lookup (UTC), including a confirmed miss (no photo).
        /// Empty <see cref="AvatarUrl"/> plus this stamp means "asked, nobody has a picture".
        /// </summary>
        public DateTime? AvatarFetchedAtUtc
        {
            get => _avatarFetchedAtUtc;
            set { if (_avatarFetchedAtUtc == value) return; _avatarFetchedAtUtc = value; OnPropertyChanged(); }
        }

        private DateTime? _avatarFetchFailedAtUtc;
        /// <summary>Transient lookup failure (timeout / socket). Distinct from a no-picture miss.</summary>
        public DateTime? AvatarFetchFailedAtUtc
        {
            get => _avatarFetchFailedAtUtc;
            set { if (_avatarFetchFailedAtUtc == value) return; _avatarFetchFailedAtUtc = value; OnPropertyChanged(); }
        }

        private string _avatarFetchFailureReason;
        public string AvatarFetchFailureReason
        {
            get => _avatarFetchFailureReason;
            set { if (_avatarFetchFailureReason == value) return; _avatarFetchFailureReason = value; OnPropertyChanged(); }
        }

        /// <summary>
        /// Whether another picture GET is worth it. People with a cached photo are skipped.
        /// Confirmed misses wait <paramref name="noPictureRetry"/>; timeouts wait
        /// <paramref name="failureBackoff"/>.
        /// </summary>
        public bool NeedsAvatarLookup(DateTime nowUtc, TimeSpan noPictureRetry, TimeSpan failureBackoff)
        {
            if (!string.IsNullOrWhiteSpace(AvatarUrl))
            {
                return false;
            }

            if (AvatarFetchFailedAtUtc.HasValue)
            {
                return nowUtc - ToUtc(AvatarFetchFailedAtUtc.Value) >= failureBackoff;
            }

            if (!AvatarFetchedAtUtc.HasValue)
            {
                return true;
            }

            return nowUtc - ToUtc(AvatarFetchedAtUtc.Value) >= noPictureRetry;
        }

        private static DateTime ToUtc(DateTime value)
        {
            if (value.Kind == DateTimeKind.Utc)
            {
                return value;
            }

            if (value.Kind == DateTimeKind.Local)
            {
                return value.ToUniversalTime();
            }

            return DateTime.SpecifyKind(value, DateTimeKind.Utc);
        }

        private void OnPropertyChanged([CallerMemberName] string propertyName = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }
    }
}
