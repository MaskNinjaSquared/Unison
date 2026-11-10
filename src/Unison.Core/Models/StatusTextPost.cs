using System.Collections.Generic;

namespace Unison.Core.Models
{
    /// <summary>Who sees a Status the account publishes (WhatsApp "Status privacy").</summary>
    public enum StatusAudienceMode
    {
        /// <summary>Every contact.</summary>
        Contacts,

        /// <summary>Every contact except the excluded list.</summary>
        ContactsExcept,

        /// <summary>Only the selected list.</summary>
        OnlyShareWith
    }

    /// <summary>A text Status ready to publish: content, look and resolved recipients.</summary>
    public sealed class StatusTextPost
    {
        public string Text { get; set; }

        public uint BackgroundArgb { get; set; }

        public uint TextArgb { get; set; }

        /// <summary>Wire value of WhatsApp's ExtendedTextMessage font type.</summary>
        public int Font { get; set; }

        /// <summary>Contacts that receive the Status. The account itself is added by the façade.</summary>
        public IReadOnlyList<string> RecipientJids { get; set; }
    }
}
