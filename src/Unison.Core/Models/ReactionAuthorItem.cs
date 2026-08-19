using Unison.Core.Helpers;

namespace Unison.Core.Models
{
    /// <summary>
    /// One row of the reactions dialog: who reacted, and with which emoji.
    /// Identity fields come from <see cref="Person"/>; the emoji from the reaction itself.
    /// </summary>
    public sealed class ReactionAuthorItem : Observable
    {
        private string _jid;
        private string _displayName;
        private string _phone;
        private string _avatarUrl;
        private string _emoji;

        public string Jid
        {
            get => _jid;
            set => Set(ref _jid, value);
        }

        public string DisplayName
        {
            get => _displayName;
            set => Set(ref _displayName, value);
        }

        public string Phone
        {
            get => _phone;
            set => Set(ref _phone, value);
        }

        public string AvatarUrl
        {
            get => _avatarUrl;
            set => Set(ref _avatarUrl, value);
        }

        public string Emoji
        {
            get => _emoji;
            set => Set(ref _emoji, value);
        }
    }
}
