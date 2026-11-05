using System;
using System.Windows.Input;
using Unison.Core.Helpers;

namespace Unison.Core.Models
{
    /// <summary>One of the account's own Status posts on the "My status" page.</summary>
    public sealed class OwnStatusItem : Observable
    {
        private int _viewCount;
        private string _viewCountText;

        public string MessageId { get; set; }

        public string AuthorJid { get; set; }

        /// <summary>Status thumb / media already on disk; null until materialized.</summary>
        public string ThumbnailUri { get; set; }

        public DateTime? TimestampUtc { get; set; }

        /// <summary>Wired by <c>StatusListViewModel</c> — Save via FileSavePicker.</summary>
        public ICommand SaveCommand { get; set; }

        public int ViewCount
        {
            get => _viewCount;
            set => Set(ref _viewCount, value);
        }

        /// <summary>Localized "X views" title.</summary>
        public string ViewCountText
        {
            get => _viewCountText;
            set => Set(ref _viewCountText, value);
        }
    }
}
