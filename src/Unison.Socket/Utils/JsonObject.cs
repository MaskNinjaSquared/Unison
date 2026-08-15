// =============================================================================
// JsonObject
//
// A reader for the small, flat JSON the media hosts answer with.
//
// The stack has no JSON dependency and does not need one: the whole protocol is
// protobuf and binary nodes, and the only JSON in sight is an upload response of
// three or four fields. Taking on a serializer for that would be a poor trade,
// so this reads the top-level members of one object and skips anything nested.
//
// Values come back as strings whatever they were written as - the callers want
// a URL, a path and a handle, and a number is just as readable as text.
// =============================================================================
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace Unison.Socket.Utils
{
    public static class JsonObject
    {
        /// <summary>
        /// Reads the top-level members of a JSON object. Nested objects and arrays are skipped
        /// rather than flattened, and malformed input yields whatever was read up to that point:
        /// a truncated answer is reported by the field the caller wanted being absent.
        /// </summary>
        public static Dictionary<string, string> Parse(string json)
        {
            var result = new Dictionary<string, string>(StringComparer.Ordinal);
            if (string.IsNullOrEmpty(json))
            {
                return result;
            }

            var i = 0;
            SkipWhitespace(json, ref i);

            if (i >= json.Length || json[i] != '{')
            {
                return result;
            }

            i++;

            while (i < json.Length)
            {
                SkipWhitespace(json, ref i);

                if (i >= json.Length || json[i] == '}')
                {
                    break;
                }

                if (json[i] == ',')
                {
                    i++;
                    continue;
                }

                if (json[i] != '"')
                {
                    break;
                }

                var name = ReadString(json, ref i);

                SkipWhitespace(json, ref i);
                if (i >= json.Length || json[i] != ':')
                {
                    break;
                }

                i++;
                SkipWhitespace(json, ref i);

                if (i >= json.Length)
                {
                    break;
                }

                if (json[i] == '"')
                {
                    result[name] = ReadString(json, ref i);
                }
                else if (json[i] == '{' || json[i] == '[')
                {
                    SkipContainer(json, ref i);
                }
                else
                {
                    result[name] = ReadLiteral(json, ref i);
                }
            }

            return result;
        }

        public static string Value(Dictionary<string, string> members, string name)
        {
            string value;
            return members != null && members.TryGetValue(name, out value) ? value : null;
        }

        private static void SkipWhitespace(string json, ref int i)
        {
            while (i < json.Length && char.IsWhiteSpace(json[i]))
            {
                i++;
            }
        }

        private static string ReadString(string json, ref int i)
        {
            // Called with i on the opening quote.
            i++;

            var builder = new StringBuilder();

            while (i < json.Length)
            {
                var c = json[i++];

                if (c == '"')
                {
                    break;
                }

                if (c != '\\')
                {
                    builder.Append(c);
                    continue;
                }

                if (i >= json.Length)
                {
                    break;
                }

                var escape = json[i++];
                switch (escape)
                {
                    case 'n':
                        builder.Append('\n');
                        break;
                    case 'r':
                        builder.Append('\r');
                        break;
                    case 't':
                        builder.Append('\t');
                        break;
                    case 'b':
                        builder.Append('\b');
                        break;
                    case 'f':
                        builder.Append('\f');
                        break;
                    case 'u':
                        if (i + 4 <= json.Length)
                        {
                            int code;
                            if (int.TryParse(
                                json.Substring(i, 4),
                                NumberStyles.HexNumber,
                                CultureInfo.InvariantCulture,
                                out code))
                            {
                                builder.Append((char)code);
                            }

                            i += 4;
                        }

                        break;
                    default:
                        builder.Append(escape);
                        break;
                }
            }

            return builder.ToString();
        }

        private static string ReadLiteral(string json, ref int i)
        {
            var start = i;

            while (i < json.Length && json[i] != ',' && json[i] != '}' && !char.IsWhiteSpace(json[i]))
            {
                i++;
            }

            return json.Substring(start, i - start);
        }

        private static void SkipContainer(string json, ref int i)
        {
            var open = json[i];
            var close = open == '{' ? '}' : ']';
            var depth = 0;

            while (i < json.Length)
            {
                var c = json[i];

                if (c == '"')
                {
                    ReadString(json, ref i);
                    continue;
                }

                i++;

                if (c == open)
                {
                    depth++;
                }
                else if (c == close)
                {
                    depth--;
                    if (depth == 0)
                    {
                        return;
                    }
                }
            }
        }
    }
}
