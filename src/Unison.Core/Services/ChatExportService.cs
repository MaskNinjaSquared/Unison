// =============================================================================
// ChatExportService
//
// Walks SQLite history, writes WhatsApp-style TXT (+ VCFs), packs a zip.
// =============================================================================
using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Unison.Core.Contracts;
using Unison.Core.Helpers;
using Unison.Core.Models;

namespace Unison.Core.Services
{
    public sealed class ChatExportService : IChatExportService
    {
        private const int PageSize = 200;
        private readonly IHistoryMessageStore _history;
        private readonly IStringResources _strings;
        private readonly IJidResolver _jids;

        public ChatExportService(
            IHistoryMessageStore history,
            IStringResources strings,
            IJidResolver jids)
        {
            _history = history ?? throw new ArgumentNullException(nameof(history));
            _strings = strings;
            _jids = jids;
        }

        public async Task<ChatExportResult> HandleAsync(
            ChatExportRequest request,
            IProgress<ChatExportProgress> progress = null,
            CancellationToken cancellationToken = default)
        {
            if (request == null)
            {
                throw new ArgumentNullException(nameof(request));
            }

            if (string.IsNullOrWhiteSpace(request.ChatJid))
            {
                throw new ArgumentException("Chat JID is required.", nameof(request));
            }

            if (string.IsNullOrWhiteSpace(request.OutputDirectory))
            {
                throw new ArgumentException("Output directory is required.", nameof(request));
            }

            cancellationToken.ThrowIfCancellationRequested();
            Report(progress, 0, "ChatExport_Loading", "Loading messages…");

            List<HistoryMessage> messages = await LoadAllMessagesAsync(request.ChatJid, progress, cancellationToken)
                .ConfigureAwait(false);

            string baseName = WhatsAppChatExportFormatter.BuildBaseFileName(request.ChatDisplayName);
            string txtName = baseName + ".txt";
            string zipName = baseName + ".zip";
            Directory.CreateDirectory(request.OutputDirectory);
            string zipPath = Path.Combine(request.OutputDirectory, zipName);
            if (File.Exists(zipPath))
            {
                File.Delete(zipPath);
            }

            Report(progress, messages.Count, "ChatExport_Writing", "Writing export…");
            var vcfs = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var txt = new StringBuilder();

            if (messages.Count > 0)
            {
                DateTime firstLocal = (messages[0].TimestampUtc ?? messages[0].UpdatedAtUtc).ToLocalTime();
                string header = _strings?.Get(
                    "ChatExport_EncryptionHeader",
                    "As mensagens e ligações são protegidas com a criptografia de ponta a ponta. Somente as pessoas que fazem parte da conversa podem ler, ouvir e compartilhar esse conteúdo. *Saiba mais*");
                txt.AppendLine(WhatsAppChatExportFormatter.FormatEncryptionHeader(firstLocal, header));
            }

            string selfName = string.IsNullOrWhiteSpace(request.SelfDisplayName)
                ? (_strings?.Get("ChatExport_You", "You") ?? "You")
                : request.SelfDisplayName.Trim();
            string peerName = string.IsNullOrWhiteSpace(request.ChatDisplayName)
                ? "Chat"
                : request.ChatDisplayName.Trim();

            for (int i = 0; i < messages.Count; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                HistoryMessage row = messages[i];
                string author = row.IsFromMe
                    ? selfName
                    : (!string.IsNullOrWhiteSpace(row.SenderName) ? row.SenderName.Trim() : peerName);

                string line = WhatsAppChatExportFormatter.FormatMessageLine(
                    row,
                    author,
                    request.IncludeMedia,
                    out string vcfName);
                if (line == null)
                {
                    continue;
                }

                txt.AppendLine(line);

                if (!string.IsNullOrWhiteSpace(vcfName) &&
                    row.Kind == ChatPreviewKind.Contact &&
                    !vcfs.ContainsKey(vcfName))
                {
                    string vcf = WhatsAppChatExportFormatter.BuildVcf(row);
                    if (!string.IsNullOrWhiteSpace(vcf))
                    {
                        vcfs[vcfName] = vcf;
                    }
                }

                if ((i + 1) % 100 == 0)
                {
                    Report(progress, i + 1, "ChatExport_Writing", "Writing export…");
                }
            }

            Report(progress, messages.Count, "ChatExport_Zipping", "Creating zip…");
            using (FileStream zipStream = File.Create(zipPath))
            using (var archive = new ZipArchive(zipStream, ZipArchiveMode.Create))
            {
                WriteEntry(archive, txtName, txt.ToString());
                foreach (KeyValuePair<string, string> pair in vcfs)
                {
                    WriteEntry(archive, pair.Key, pair.Value);
                }
            }

            var info = new FileInfo(zipPath);
            return new ChatExportResult
            {
                ZipPath = zipPath,
                FileName = zipName,
                ByteLength = info.Exists ? info.Length : 0,
                MessageCount = messages.Count,
                VcfCount = vcfs.Count
            };
        }

        private async Task<List<HistoryMessage>> LoadAllMessagesAsync(
            string chatJid,
            IProgress<ChatExportProgress> progress,
            CancellationToken cancellationToken)
        {
            string canonical = _jids != null
                ? _jids.GetCanonicalJid(chatJid)
                : chatJid;
            var keys = new List<string>();
            if (!string.IsNullOrWhiteSpace(canonical))
            {
                keys.Add(canonical);
            }

            if (!string.IsNullOrWhiteSpace(chatJid) &&
                !string.Equals(chatJid, canonical, StringComparison.OrdinalIgnoreCase))
            {
                keys.Add(chatJid);
            }

            var pages = new List<IReadOnlyList<HistoryMessage>>();
            DateTime? beforeUtc = null;
            string beforeId = null;
            int loaded = 0;

            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                IReadOnlyList<HistoryMessage> page = await _history
                    .GetForChatKeysAsync(keys, PageSize, beforeUtc, beforeId)
                    .ConfigureAwait(false);
                if (page == null || page.Count == 0)
                {
                    break;
                }

                pages.Add(page);
                loaded += page.Count;

                HistoryMessage oldest = page[0];
                beforeUtc = oldest.TimestampUtc ?? oldest.UpdatedAtUtc;
                beforeId = oldest.MessageId;

                Report(progress, loaded, "ChatExport_Loading", "Loading messages…");

                if (page.Count < PageSize)
                {
                    break;
                }
            }

            var chronological = new List<HistoryMessage>(loaded);
            for (int p = pages.Count - 1; p >= 0; p--)
            {
                chronological.AddRange(pages[p]);
            }

            return chronological;
        }

        private static void WriteEntry(ZipArchive archive, string entryName, string content)
        {
            ZipArchiveEntry entry = archive.CreateEntry(entryName, CompressionLevel.Optimal);
            using (Stream stream = entry.Open())
            using (var writer = new StreamWriter(stream, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false)))
            {
                writer.Write(content);
            }
        }

        private static void Report(
            IProgress<ChatExportProgress> progress,
            int count,
            string key,
            string fallback)
        {
            progress?.Report(new ChatExportProgress
            {
                MessagesProcessed = count,
                StatusKey = key,
                StatusFallback = fallback
            });
        }
    }
}
