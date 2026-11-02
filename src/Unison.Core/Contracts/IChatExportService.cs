// =============================================================================
// IChatExportService
//
// Builds a WhatsApp-style chat export zip (TXT + optional VCFs) off the UI thread.
// =============================================================================
using System;
using System.Threading;
using System.Threading.Tasks;

namespace Unison.Core.Contracts
{
    public sealed class ChatExportRequest
    {
        public string ChatJid { get; set; }

        /// <summary>Peer / group title used in the zip and TXT file names.</summary>
        public string ChatDisplayName { get; set; }

        /// <summary>Author label for IsFromMe rows (account push name).</summary>
        public string SelfDisplayName { get; set; }

        /// <summary>When false (this slice), media becomes &lt;Mídia oculta&gt;.</summary>
        public bool IncludeMedia { get; set; }

        /// <summary>Folder that will hold the finished zip (platform temp).</summary>
        public string OutputDirectory { get; set; }
    }

    public sealed class ChatExportProgress
    {
        public int MessagesProcessed { get; set; }

        public string StatusKey { get; set; }

        public string StatusFallback { get; set; }
    }

    public sealed class ChatExportResult
    {
        public string ZipPath { get; set; }

        public string FileName { get; set; }

        public long ByteLength { get; set; }

        public int MessageCount { get; set; }

        public int VcfCount { get; set; }
    }

    /// <summary>
    /// Export a conversation to a WhatsApp-compatible zip. Heavy work belongs here,
    /// not on the UI thread; dialogs stay with <see cref="IDialogService"/>.
    /// </summary>
    public interface IChatExportService
    {
        Task<ChatExportResult> HandleAsync(
            ChatExportRequest request,
            IProgress<ChatExportProgress> progress = null,
            CancellationToken cancellationToken = default);
    }
}
