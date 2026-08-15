using System;
using SQLite;

namespace Unison.Uwp.Data.Entities
{
    /// <summary>
    /// One entry of the LID mapping store. Both directions are stored as rows: the forward key
    /// is the phone-number user, the reverse key is the LID user with a suffix.
    /// </summary>
    [Table("LidMapping")]
    public sealed class LidMappingRow
    {
        [PrimaryKey]
        public string Key { get; set; }

        public string Value { get; set; }

        public DateTime UpdatedAtUtc { get; set; }
    }
}
