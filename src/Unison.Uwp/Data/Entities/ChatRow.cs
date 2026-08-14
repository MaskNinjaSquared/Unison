using System;

using SQLite;



namespace Unison.Uwp.Data.Entities

{

    /// <summary>

    /// SQLite row for local chat metadata.

    /// </summary>

    [Table("Chat")]

    public sealed class ChatRow

    {

        [PrimaryKey]

        public string Jid { get; set; }



        /// <summary><see cref="Unison.Core.Models.ChatLocalStatus"/> as int.</summary>

        public int Status { get; set; }



        public bool IsChatPinned { get; set; }



        public bool IsWidgetPinned { get; set; }



        /// <summary>Unix seconds; null = not muted.</summary>

        public long? MutedUntil { get; set; }



        public DateTime UpdatedAtUtc { get; set; }

    }

}

