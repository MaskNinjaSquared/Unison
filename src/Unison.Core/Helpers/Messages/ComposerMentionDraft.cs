// =============================================================================
// ComposerMentionDraft
//
// The text in the field stays readable ("@Ana"). Each chosen mention is a span
// holding the JID, or the everyone flag. GetMessage is the only place that
// turns a person into @number for the wire. Everyone stays the inserted word
// and raises MentionAll — the word depends on the language, the flag does not.
// =============================================================================
using System;
using System.Collections.Generic;
using Unison.Core.Models;

namespace Unison.Core.Helpers
{
    public sealed class ComposerMentionDraft
    {
        private readonly List<Span> _spans = new List<Span>();
        private string _text = string.Empty;

        public string Text
        {
            get { return _text; }
        }

        public bool MentionAll
        {
            get
            {
                for (int i = 0; i < _spans.Count; i++)
                {
                    if (_spans[i].IsAll)
                    {
                        return true;
                    }
                }

                return false;
            }
        }

        /// <summary>
        /// Applies a field edit. A change that touches a mention removes that
        /// whole mention, so a backspace inside "@Ana" does not leave "@An".
        /// Returns the text the field should show.
        /// </summary>
        public string SetText(string incoming)
        {
            string text = incoming ?? string.Empty;
            if (string.Equals(text, _text, StringComparison.Ordinal))
            {
                return _text;
            }

            int prefix = 0;
            int oldLength = _text.Length;
            int newLength = text.Length;
            int shared = oldLength < newLength ? oldLength : newLength;
            while (prefix < shared && _text[prefix] == text[prefix])
            {
                prefix++;
            }

            int oldEnd = oldLength;
            int newEnd = newLength;
            while (oldEnd > prefix && newEnd > prefix && _text[oldEnd - 1] == text[newEnd - 1])
            {
                oldEnd--;
                newEnd--;
            }

            int delta = (newEnd - prefix) - (oldEnd - prefix);
            var doomed = new List<Span>();
            var kept = new List<Span>();
            for (int i = 0; i < _spans.Count; i++)
            {
                Span span = _spans[i];
                bool overlaps = span.Start < oldEnd && span.End > prefix;
                bool insertInside = oldEnd == prefix && prefix > span.Start && prefix < span.End;
                if (overlaps || insertInside)
                {
                    doomed.Add(span);
                }
                else
                {
                    kept.Add(span);
                }
            }

            doomed.Sort(delegate(Span left, Span right) { return right.Start.CompareTo(left.Start); });
            for (int i = 0; i < doomed.Count; i++)
            {
                text = RemoveSpanLeftover(text, doomed[i], prefix, oldEnd, newEnd);
            }

            _spans.Clear();
            for (int i = 0; i < kept.Count; i++)
            {
                Span span = kept[i];
                int start = span.Start >= oldEnd ? span.Start + delta : span.Start;
                start -= RemovedBefore(doomed, span.Start, prefix, oldEnd, newEnd, delta);
                if (start < 0 || start + span.Label.Length > text.Length)
                {
                    continue;
                }

                if (!string.Equals(text.Substring(start, span.Label.Length), span.Label, StringComparison.Ordinal))
                {
                    continue;
                }

                _spans.Add(new Span(start, span.Label, span.Jid, span.IsAll));
            }

            _spans.Sort(delegate(Span left, Span right) { return left.Start.CompareTo(right.Start); });
            _text = text;
            return _text;
        }

        /// <summary>
        /// Replaces the open @token with the choice. The caret after the call
        /// sits on the space that follows the mention.
        /// </summary>
        public string Insert(MentionChoice choice, int caret, int tokenLength)
        {
            if (choice == null)
            {
                throw new ArgumentNullException(nameof(choice));
            }

            if (tokenLength < 1)
            {
                tokenLength = 1;
            }

            int start = caret - tokenLength;
            if (start < 0)
            {
                start = 0;
            }

            if (caret > _text.Length)
            {
                caret = _text.Length;
            }

            if (start > caret)
            {
                start = caret;
            }

            string label = "@" + (choice.Name ?? string.Empty).Trim();
            string insertion = label + " ";
            string next = _text.Substring(0, start) + insertion + _text.Substring(caret);
            int growth = insertion.Length - (caret - start);

            var kept = new List<Span>();
            for (int i = 0; i < _spans.Count; i++)
            {
                Span span = _spans[i];
                if (span.End <= start)
                {
                    kept.Add(span);
                }
                else if (span.Start >= caret)
                {
                    kept.Add(new Span(span.Start + growth, span.Label, span.Jid, span.IsAll));
                }
            }

            kept.Add(new Span(start, label, choice.IsAll ? null : choice.Jid, choice.IsAll));
            kept.Sort(delegate(Span left, Span right) { return left.Start.CompareTo(right.Start); });
            _spans.Clear();
            _spans.AddRange(kept);
            _text = next;
            return _text;
        }

        public int CaretAfterInsert(int tokenStart, string insertedName)
        {
            string label = "@" + (insertedName ?? string.Empty).Trim();
            return tokenStart + label.Length + 1;
        }

        public OutgoingTextMentions GetMessage()
        {
            string wire = _text;
            var mentions = new List<string>();
            bool mentionAll = false;
            for (int i = _spans.Count - 1; i >= 0; i--)
            {
                Span span = _spans[i];
                if (span.Start < 0 || span.End > wire.Length)
                {
                    continue;
                }

                if (span.IsAll)
                {
                    mentionAll = true;
                    continue;
                }

                string digits = MentionLookupBuilder.ExtractUserDigits(span.Jid);
                if (string.IsNullOrEmpty(digits))
                {
                    continue;
                }

                wire = wire.Substring(0, span.Start) + "@" + digits + wire.Substring(span.End);
                mentions.Insert(0, span.Jid);
            }

            return new OutgoingTextMentions
            {
                Text = wire,
                Mentions = mentions,
                MentionAll = mentionAll
            };
        }

        public void Clear()
        {
            _text = string.Empty;
            _spans.Clear();
        }

        private static string RemoveSpanLeftover(string text, Span span, int prefix, int oldEnd, int newEnd)
        {
            int removeStart = int.MaxValue;
            int removeEnd = int.MinValue;
            if (span.Start < prefix)
            {
                removeStart = span.Start;
                removeEnd = prefix < span.End ? prefix : span.End;
            }

            int delta = (newEnd - prefix) - (oldEnd - prefix);
            if (span.End > oldEnd)
            {
                int rightStart = (span.Start > oldEnd ? span.Start : oldEnd) + delta;
                int rightEnd = span.End + delta;
                if (rightStart < removeStart)
                {
                    removeStart = rightStart;
                }

                if (rightEnd > removeEnd)
                {
                    removeEnd = rightEnd;
                }
            }

            if (span.Start < oldEnd && span.End > prefix && prefix < newEnd)
            {
                if (prefix < removeStart)
                {
                    removeStart = prefix;
                }

                if (newEnd > removeEnd)
                {
                    removeEnd = newEnd;
                }
            }

            if (removeStart < 0)
            {
                removeStart = 0;
            }

            if (removeEnd > text.Length)
            {
                removeEnd = text.Length;
            }

            if (removeEnd <= removeStart)
            {
                return text;
            }

            return text.Remove(removeStart, removeEnd - removeStart);
        }

        /// <summary>
        /// How many characters doomed spans removed before <paramref name="oldIndex"/>
        /// in the edited string, so a later span lands on the label that survived.
        /// </summary>
        private static int RemovedBefore(List<Span> doomed, int oldIndex, int prefix, int oldEnd, int newEnd, int delta)
        {
            int removed = 0;
            for (int i = 0; i < doomed.Count; i++)
            {
                Span span = doomed[i];
                int endInNew = span.End <= prefix
                    ? span.End
                    : (span.Start >= oldEnd ? span.End + delta : prefix);
                if (endInNew <= Map(oldIndex, prefix, oldEnd, delta))
                {
                    int leftover = LeftoverLength(span, prefix, oldEnd, newEnd);
                    removed += leftover;
                }
            }

            return removed;
        }

        private static int Map(int oldIndex, int prefix, int oldEnd, int delta)
        {
            return oldIndex >= oldEnd ? oldIndex + delta : oldIndex;
        }

        private static int LeftoverLength(Span span, int prefix, int oldEnd, int newEnd)
        {
            int length = 0;
            if (span.Start < prefix)
            {
                length += (prefix < span.End ? prefix : span.End) - span.Start;
            }

            if (span.End > oldEnd)
            {
                length += span.End - (span.Start > oldEnd ? span.Start : oldEnd);
            }

            if (span.Start < oldEnd && span.End > prefix && prefix < newEnd)
            {
                length += newEnd - prefix;
            }

            return length;
        }

        private sealed class Span
        {
            public Span(int start, string label, string jid, bool isAll)
            {
                Start = start;
                Label = label ?? string.Empty;
                Jid = jid;
                IsAll = isAll;
            }

            public int Start { get; }

            public int End
            {
                get { return Start + Label.Length; }
            }

            public string Label { get; }

            public string Jid { get; }

            public bool IsAll { get; }
        }
    }
}
