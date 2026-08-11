using System;
using SQLite;

namespace Unison.Uwp.Data.Entities
{
    /// <summary>
    /// SQLite row for <see cref="Unison.Core.Models.Person"/> (persistence only).
    /// </summary>
    [Table("Person")]
    public sealed class PersonRow
    {
        [PrimaryKey]
        public string Jid { get; set; }

        public string Name { get; set; }

        public string AvatarUrl { get; set; }

        public string Phone { get; set; }

        public DateTime UpdatedAtUtc { get; set; }
    }
}
