using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace Unison.UWPApp.Protocol
{
    /// <summary>
    /// Encodes BinaryNode to WhatsApp binary format.
    /// Based on WABinary encoding from Baileys.
    /// </summary>
    public class BinaryEncoder
    {
        // Use shared token map from WAConstants for efficient lookup
        private static Dictionary<string, byte> TokenMap => WAConstants.TokenMap;

        private readonly MemoryStream _stream;

        public BinaryEncoder()
        {
            _stream = new MemoryStream();
        }

        public byte[] Encode(BinaryNode node)
        {
            _stream.SetLength(0);
            _stream.Position = 0;
            
            // Baileys prepends a 0 byte to all encoded messages (encode.ts line 8: buffer = [0])
            WriteByte(0);
            
            WriteNode(node);
            return _stream.ToArray();
        }

        private void WriteNode(BinaryNode node)
        {
            if (node == null)
            {
                WriteByte(0); // LIST_EMPTY
                return;
            }

            int numAttrs = node.Attrs?.Count ?? 0;
            bool hasContent = node.Content != null;

            WriteListStart(2 * numAttrs + 1 + (hasContent ? 1 : 0));
            WriteString(node.Tag);
            WriteAttributes(node.Attrs);

            if (hasContent)
            {
                WriteNodeContent(node.Content);
            }
        }

        private void WriteAttributes(Dictionary<string, string> attrs)
        {
            if (attrs == null) return;

            foreach (var kv in attrs)
            {
                WriteString(kv.Key);
                WriteString(kv.Value);
            }
        }

        private void WriteNodeContent(object content)
        {
            if (content is string str)
            {
                WriteString(str);
            }
            else if (content is byte[] bytes)
            {
                WriteBytes(bytes);
            }
            else if (content is BinaryNode node)
            {
                WriteListStart(1);
                WriteNode(node);
            }
            else if (content is List<BinaryNode> children)
            {
                WriteListStart(children.Count);
                foreach (var child in children)
                {
                    WriteNode(child);
                }
            }
        }

        private void WriteListStart(int count)
        {
            if (count == 0)
            {
                WriteByte(0); // LIST_EMPTY
            }
            else if (count < 256)
            {
                WriteByte(248); // LIST_8
                WriteByte((byte)count);
            }
            else
            {
                WriteByte(249); // LIST_16
                WriteUInt16((ushort)count);
            }
        }

        private void WriteString(string str)
        {
            if (string.IsNullOrEmpty(str))
            {
                WriteByte(0); // LIST_EMPTY acts as empty string
                return;
            }

            // Check if it's a known token
            if (TokenMap.TryGetValue(str, out byte tokenIndex))
            {
                WriteByte(tokenIndex);
                return;
            }

            // Check if it's a nibble string (all digits, dash, dot)
            if (IsNibbleString(str))
            {
                WriteNibbleString(str);
                return;
            }

            // Check if it's a JID
            if (str.Contains("@"))
            {
                var parts = str.Split('@');
                if (parts.Length == 2)
                {
                    string user = parts[0];
                    string server = parts[1];

                    if (user.Contains(":"))
                    {
                        var userParts = user.Split(':');
                        string u = userParts[0];
                        string d = userParts[1];
                        
                        // Handle potential agent part (e.g. user.agent:device)
                        byte agent = 0;
                        if (u.Contains("."))
                        {
                            var uParts = u.Split('.');
                            u = uParts[0];
                            byte.TryParse(uParts[1], out agent);
                        }

                        WriteByte(247); // AD_JID
                        WriteByte(agent);
                        WriteByte(byte.Parse(d));
                        WriteString(u);
                    }
                    else
                    {
                        WriteByte(250); // JID_PAIR
                        if (string.IsNullOrEmpty(user))
                        {
                            WriteByte(0); // LIST_EMPTY for empty user
                        }
                        else
                        {
                            WriteString(user); // Recurse for user part
                        }
                        WriteString(server); // Recurse for server part
                    }
                    return;
                }
            }

            // Write as binary string
            var bytes = Encoding.UTF8.GetBytes(str);
            WriteBytes(bytes);
        }

        private bool IsNibbleString(string str)
        {
            if (string.IsNullOrEmpty(str) || str.Length > 255)
                return false;

            foreach (char c in str)
            {
                if (!((c >= '0' && c <= '9') || c == '-' || c == '.'))
                    return false;
            }
            return true;
        }

        private void WriteNibbleString(string str)
        {
            WriteByte(255); // NIBBLE_8
            
            int roundedLength = (str.Length + 1) / 2;
            if (str.Length % 2 != 0)
            {
                roundedLength |= 0x80; // Set high bit for odd length
            }
            WriteByte((byte)roundedLength);

            for (int i = 0; i < str.Length; i += 2)
            {
                byte high = PackNibble(str[i]);
                byte low = (i + 1 < str.Length) ? PackNibble(str[i + 1]) : (byte)15; // 15 = null terminator
                WriteByte((byte)((high << 4) | low));
            }
        }

        private byte PackNibble(char c)
        {
            if (c >= '0' && c <= '9')
                return (byte)(c - '0');
            if (c == '-')
                return 10;
            if (c == '.')
                return 11;
            return 15; // null
        }

        private void WriteBytes(byte[] bytes)
        {
            if (bytes.Length < 256)
            {
                WriteByte(252); // BINARY_8
                WriteByte((byte)bytes.Length);
            }
            else if (bytes.Length < 1048576) // 2^20
            {
                WriteByte(253); // BINARY_20
                // 20-bit length encoded in 3 bytes
                WriteByte((byte)((bytes.Length >> 16) & 0x0F));
                WriteByte((byte)(bytes.Length >> 8));
                WriteByte((byte)bytes.Length);
            }
            else
            {
                WriteByte(254); // BINARY_32
                WriteUInt32((uint)bytes.Length);
            }

            _stream.Write(bytes, 0, bytes.Length);
        }

        private void WriteByte(byte b)
        {
            _stream.WriteByte(b);
        }

        private void WriteUInt16(ushort value)
        {
            _stream.WriteByte((byte)(value >> 8));
            _stream.WriteByte((byte)value);
        }

        private void WriteUInt32(uint value)
        {
            _stream.WriteByte((byte)(value >> 24));
            _stream.WriteByte((byte)(value >> 16));
            _stream.WriteByte((byte)(value >> 8));
            _stream.WriteByte((byte)value);
        }
    }
}
