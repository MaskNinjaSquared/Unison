// =============================================================================
// AppStateIndex
//
// Reads the mutation index, which travels as a JSON array of strings.
//
// It is parsed by hand rather than with a JSON library for one reason: this is
// the only JSON in the whole socket layer, and a dependency the host would have
// to supply for one array of strings is a poor trade. The shape is fixed by the
// protocol - ["mute","5511...@s.whatsapp.net"] - so there are no numbers,
// objects or nesting to handle.
//
// Ports: rc14 the JSON.parse of syncAction.index in src/Utils/chat-utils.ts
// =============================================================================
using System.Collections.Generic;
using System.Text;

namespace Unison.Socket.AppState
{
    public static class AppStateIndex
    {
        public static IList<string> Parse(byte[] index)
        {
            return Parse(index != null ? Encoding.UTF8.GetString(index) : null);
        }

        public static IList<string> Parse(string json)
        {
            var parts = new List<string>();
            if (string.IsNullOrEmpty(json))
            {
                return parts;
            }

            var current = new StringBuilder();
            var inString = false;
            var escaped = false;

            foreach (var character in json)
            {
                if (escaped)
                {
                    current.Append(character);
                    escaped = false;
                    continue;
                }

                if (character == '\\' && inString)
                {
                    escaped = true;
                    continue;
                }

                if (character == '"')
                {
                    if (inString)
                    {
                        parts.Add(current.ToString());
                        current.Length = 0;
                    }

                    inString = !inString;
                    continue;
                }

                if (inString)
                {
                    current.Append(character);
                }
            }

            return parts;
        }
    }
}
