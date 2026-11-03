// =============================================================================
// MessageBubbleContentProbe
//
// Dump text bodies / one bubble on tap so we can compare Unison-sent vs
// WA wrapping (single-line overflow). Dual-writes Debug + IRuntimeDiagnostics
// so export/save report captures layout metrics on device.
// =============================================================================
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using Unison.Core.Contracts;
using Unison.Core.Models;

namespace Unison.Core.Helpers.Messages
{
    public static class MessageBubbleContentProbe
    {
        private static IRuntimeDiagnostics _journal;

        /// <summary>
        /// Optional host journal (App wires <see cref="IRuntimeDiagnostics"/> once at startup).
        /// </summary>
        public static void Attach(IRuntimeDiagnostics journal)
        {
            _journal = journal;
        }

        public static void DumpRecentTextBodies(IList<ChatMessage> messages, int take = 30, string chatJid = null)
        {
            if (messages == null || messages.Count == 0)
            {
                Emit("dump", "empty chat=" + (chatJid ?? string.Empty));
                return;
            }

            int start = Math.Max(0, messages.Count - Math.Max(1, take));
            Emit(
                "dump",
                "chat=" + (chatJid ?? string.Empty) +
                " total=" + messages.Count +
                " dump=" + (messages.Count - start));

            for (int i = start; i < messages.Count; i++)
            {
                ChatMessage msg = messages[i];
                if (msg == null || msg.Kind != ChatMessageKind.Text)
                {
                    continue;
                }

                Emit("body", FormatBodyLine(i, msg));
            }
        }

        /// <summary>
        /// One bubble tap: full string stats + optional layout metrics from the UI tree.
        /// </summary>
        public static void DumpTap(ChatMessage msg, string layoutNote = null)
        {
            if (msg == null)
            {
                Emit("tap", "msg=null");
                return;
            }

            string content = msg.Content ?? string.Empty;
            CharStats stats = Analyze(content);

            var sb = new StringBuilder(512);
            sb.Append("id=").Append(msg.Id ?? "(null)");
            sb.Append(" fromMe=").Append(msg.IsFromMe);
            sb.Append(" kind=").Append(msg.Kind);
            sb.Append(" showText=").Append(msg.ShowTextContent);
            sb.Append(" revoked=").Append(msg.IsRevoked);
            sb.Append(" status=").Append(msg.Status ?? "(null)");
            sb.Append(" senderName=").Append(msg.SenderName ?? "(null)");
            sb.Append(" participant=").Append(msg.ParticipantJid ?? "(null)");
            sb.Append(" remoteJid=").Append(msg.RemoteJid ?? "(null)");
            sb.Append(" ts=").Append(msg.Timestamp.ToString("o", CultureInfo.InvariantCulture));
            sb.Append(" len=").Append(content.Length.ToString(CultureInfo.InvariantCulture));
            sb.Append(" spaces=").Append(stats.Spaces.ToString(CultureInfo.InvariantCulture));
            sb.Append(" nl=").Append(stats.Newlines.ToString(CultureInfo.InvariantCulture));
            sb.Append(" cr=").Append(stats.CarriageReturns.ToString(CultureInfo.InvariantCulture));
            sb.Append(" tab=").Append(stats.Tabs.ToString(CultureInfo.InvariantCulture));
            sb.Append(" nbsp=").Append(stats.Nbsp.ToString(CultureInfo.InvariantCulture));
            sb.Append(" nnbsp=").Append(stats.NarrowNbsp.ToString(CultureInfo.InvariantCulture));
            sb.Append(" zwsp=").Append(stats.Zwsp.ToString(CultureInfo.InvariantCulture));
            sb.Append(" zj=").Append(stats.Zwnj + stats.Zwj);
            sb.Append(" shy=").Append(stats.SoftHyphen.ToString(CultureInfo.InvariantCulture));
            sb.Append(" maxRun=").Append(stats.LongestRun.ToString(CultureInfo.InvariantCulture));
            sb.Append(" words=").Append(stats.WordCount.ToString(CultureInfo.InvariantCulture));
            sb.Append(" hasWsBreak=").Append(stats.Spaces + stats.Newlines + stats.Tabs > 0);
            Emit("tap", sb.ToString());

            if (!string.IsNullOrEmpty(layoutNote))
            {
                Emit("tap-layout", layoutNote);
            }

            Emit("tap-preview", "\"" + EscapePreview(content, 200) + "\"");

            // Full content escaped (cap so Output window / journal stay usable).
            const int fullCap = 2000;
            string full = content.Length <= fullCap
                ? content
                : content.Substring(0, fullCap) + "…[+ " +
                  (content.Length - fullCap).ToString(CultureInfo.InvariantCulture) + "]";
            Emit("tap-full", "\"" + EscapePreview(full, full.Length + 32) + "\"");

            Emit("tap-hex", HexSample(content, 96));
            Emit("tap-cps", CodePointsSample(content, 48));
        }

        private static void Emit(string eventName, string details)
        {
            string line = "[BubbleProbe][" + eventName + "] " + (details ?? string.Empty);
            Debug.WriteLine(line);
            _journal?.Write("bubble-probe", eventName, details);
        }

        private static string FormatBodyLine(int index, ChatMessage msg)
        {
            string content = msg.Content ?? string.Empty;
            CharStats stats = Analyze(content);
            return
                "#" + index.ToString(CultureInfo.InvariantCulture) +
                " fromMe=" + msg.IsFromMe +
                " id=" + (msg.Id ?? string.Empty) +
                " len=" + content.Length.ToString(CultureInfo.InvariantCulture) +
                " spaces=" + stats.Spaces.ToString(CultureInfo.InvariantCulture) +
                " nl=" + stats.Newlines.ToString(CultureInfo.InvariantCulture) +
                " nbsp=" + stats.Nbsp.ToString(CultureInfo.InvariantCulture) +
                " zwsp=" + stats.Zwsp.ToString(CultureInfo.InvariantCulture) +
                " shy=" + stats.SoftHyphen.ToString(CultureInfo.InvariantCulture) +
                " maxRun=" + stats.LongestRun.ToString(CultureInfo.InvariantCulture) +
                " text=\"" + EscapePreview(content, 96) + "\"";
        }

        private static CharStats Analyze(string content)
        {
            var stats = new CharStats();
            int run = 0;
            bool inWord = false;
            for (int c = 0; c < content.Length; c++)
            {
                char ch = content[c];
                if (ch == ' ')
                {
                    stats.Spaces++;
                    run = 0;
                    inWord = false;
                }
                else if (ch == '\n')
                {
                    stats.Newlines++;
                    run = 0;
                    inWord = false;
                }
                else if (ch == '\r')
                {
                    stats.CarriageReturns++;
                    run = 0;
                    inWord = false;
                }
                else if (ch == '\t')
                {
                    stats.Tabs++;
                    run = 0;
                    inWord = false;
                }
                else if (ch == '\u00A0')
                {
                    stats.Nbsp++;
                    // NBSP does not break Wrap in the same way as U+0020.
                    run++;
                    if (run > stats.LongestRun)
                    {
                        stats.LongestRun = run;
                    }

                    inWord = true;
                }
                else if (ch == '\u202F')
                {
                    stats.NarrowNbsp++;
                    run++;
                    if (run > stats.LongestRun)
                    {
                        stats.LongestRun = run;
                    }

                    inWord = true;
                }
                else if (ch == '\u200B')
                {
                    stats.Zwsp++;
                    run = 0;
                    inWord = false;
                }
                else if (ch == '\u200C')
                {
                    stats.Zwnj++;
                    run = 0;
                    inWord = false;
                }
                else if (ch == '\u200D')
                {
                    stats.Zwj++;
                    run++;
                    if (run > stats.LongestRun)
                    {
                        stats.LongestRun = run;
                    }

                    inWord = true;
                }
                else if (ch == '\u00AD')
                {
                    stats.SoftHyphen++;
                    run = 0;
                    inWord = false;
                }
                else
                {
                    run++;
                    if (run > stats.LongestRun)
                    {
                        stats.LongestRun = run;
                    }

                    if (!inWord)
                    {
                        stats.WordCount++;
                        inWord = true;
                    }
                }
            }

            return stats;
        }

        private static string EscapePreview(string content, int maxChars)
        {
            if (string.IsNullOrEmpty(content))
            {
                return string.Empty;
            }

            string preview = content.Length <= maxChars
                ? content
                : content.Substring(0, maxChars) + "…";
            return preview
                .Replace("\r", "\\r")
                .Replace("\n", "\\n")
                .Replace("\t", "\\t")
                .Replace("\u00A0", "\\u00A0")
                .Replace("\u202F", "\\u202F")
                .Replace("\u200B", "\\u200B")
                .Replace("\u200C", "\\u200C")
                .Replace("\u200D", "\\u200D")
                .Replace("\u00AD", "\\u00AD");
        }

        private static string HexSample(string content, int maxChars)
        {
            if (string.IsNullOrEmpty(content))
            {
                return "(empty)";
            }

            int n = Math.Min(content.Length, maxChars);
            var sb = new StringBuilder(n * 3);
            for (int i = 0; i < n; i++)
            {
                if (i > 0)
                {
                    sb.Append(' ');
                }

                sb.Append(((int)content[i]).ToString("X4", CultureInfo.InvariantCulture));
            }

            if (content.Length > maxChars)
            {
                sb.Append(" …");
            }

            return sb.ToString();
        }

        private static string CodePointsSample(string content, int maxChars)
        {
            if (string.IsNullOrEmpty(content))
            {
                return "(empty)";
            }

            int n = Math.Min(content.Length, maxChars);
            var sb = new StringBuilder(n * 8);
            for (int i = 0; i < n; i++)
            {
                if (i > 0)
                {
                    sb.Append(' ');
                }

                char ch = content[i];
                if (ch >= 0x20 && ch < 0x7F && ch != ' ')
                {
                    sb.Append('\'').Append(ch).Append('\'');
                }
                else if (ch == ' ')
                {
                    sb.Append("SP");
                }
                else
                {
                    sb.Append('U').Append('+')
                        .Append(((int)ch).ToString("X4", CultureInfo.InvariantCulture));
                }
            }

            if (content.Length > maxChars)
            {
                sb.Append(" …");
            }

            return sb.ToString();
        }

        private struct CharStats
        {
            public int Spaces;
            public int Newlines;
            public int CarriageReturns;
            public int Tabs;
            public int Nbsp;
            public int NarrowNbsp;
            public int Zwsp;
            public int Zwnj;
            public int Zwj;
            public int SoftHyphen;
            public int LongestRun;
            public int WordCount;
        }
    }
}
