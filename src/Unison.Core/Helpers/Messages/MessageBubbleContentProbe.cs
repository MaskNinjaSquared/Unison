// =============================================================================
// MessageBubbleContentProbe
//
// DEBUG: dump last N text bodies so we can compare Unison-sent vs WA wrapping.
// =============================================================================
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using Unison.Core.Models;

namespace Unison.Core.Helpers.Messages
{
    public static class MessageBubbleContentProbe
    {
        public static void DumpRecentTextBodies(IList<ChatMessage> messages, int take = 30, string chatJid = null)
        {
            if (messages == null || messages.Count == 0)
            {
                Debug.WriteLine("[BubbleProbe] empty chat=" + (chatJid ?? string.Empty));
                return;
            }

            int start = Math.Max(0, messages.Count - Math.Max(1, take));
            Debug.WriteLine(
                "[BubbleProbe] chat=" + (chatJid ?? string.Empty) +
                " total=" + messages.Count +
                " dump=" + (messages.Count - start));

            for (int i = start; i < messages.Count; i++)
            {
                ChatMessage msg = messages[i];
                if (msg == null || msg.Kind != ChatMessageKind.Text)
                {
                    continue;
                }

                string content = msg.Content ?? string.Empty;
                int spaces = 0;
                int newlines = 0;
                int zwsp = 0;
                int softHyphen = 0;
                int longestRun = 0;
                int run = 0;
                for (int c = 0; c < content.Length; c++)
                {
                    char ch = content[c];
                    if (ch == ' ')
                    {
                        spaces++;
                        run = 0;
                    }
                    else if (ch == '\n' || ch == '\r')
                    {
                        newlines++;
                        run = 0;
                    }
                    else if (ch == '\u200B')
                    {
                        zwsp++;
                        run = 0;
                    }
                    else if (ch == '\u00AD')
                    {
                        softHyphen++;
                        run = 0;
                    }
                    else
                    {
                        run++;
                        if (run > longestRun)
                        {
                            longestRun = run;
                        }
                    }
                }

                string preview = content.Length <= 96 ? content : content.Substring(0, 96) + "…";
                preview = preview
                    .Replace("\r", "\\r")
                    .Replace("\n", "\\n")
                    .Replace("\u200B", "\\u200B")
                    .Replace("\u00AD", "\\u00AD");

                Debug.WriteLine(
                    "[BubbleProbe] #" + i.ToString(CultureInfo.InvariantCulture) +
                    " fromMe=" + msg.IsFromMe +
                    " len=" + content.Length.ToString(CultureInfo.InvariantCulture) +
                    " spaces=" + spaces.ToString(CultureInfo.InvariantCulture) +
                    " nl=" + newlines.ToString(CultureInfo.InvariantCulture) +
                    " zwsp=" + zwsp.ToString(CultureInfo.InvariantCulture) +
                    " shy=" + softHyphen.ToString(CultureInfo.InvariantCulture) +
                    " maxRun=" + longestRun.ToString(CultureInfo.InvariantCulture) +
                    " text=\"" + preview + "\"");
            }
        }
    }
}
