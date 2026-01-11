using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace Unison.UWPApp.Protocol
{
    /// <summary>
    /// Decodes WhatsApp binary format to BinaryNode.
    /// Based on WABinary decoding from Baileys.
    /// </summary>
    public class BinaryDecoder
    {
        // Use shared token list from WAConstants
        private static string[] SINGLE_BYTE_TOKENS => WAConstants.SINGLE_BYTE_TOKENS;
        
        // Double byte token lookup
        private static string GetDoubleByteToken(int dictIndex, int tokenIndex) 
            => WAConstants.GetDoubleByteToken(dictIndex, tokenIndex);

        private readonly byte[] _data;
        private int _position;

        public BinaryDecoder(byte[] data)
        {
            _data = data;
            _position = 0;
        }

        public static BinaryNode Decode(byte[] data)
        {
            if (data == null || data.Length == 0)
                return null;
                
            // First byte is flags byte - bit 1 (value 2) indicates compression
            byte flags = data[0];
            bool isCompressed = (flags & 2) != 0;
            
            byte[] payload;
            if (isCompressed)
            {
                // Skip flags byte and decompress
                payload = DecompressZlib(data, 1);
                System.Diagnostics.Debug.WriteLine($"[BinaryDecoder] Decompressed {data.Length - 1} -> {payload.Length} bytes");
            }
            else
            {
                // Skip flags byte only
                payload = new byte[data.Length - 1];
                Array.Copy(data, 1, payload, 0, payload.Length);
            }
            
            var decoder = new BinaryDecoder(payload);
            return decoder.ReadNode();
        }
        
        /// <summary>
        /// Decompresses zlib data (RFC 1950 with 2-byte header)
        /// </summary>
        private static byte[] DecompressZlib(byte[] data, int offset)
        {
            try
            {
                // Use BouncyCastle ZInputStream for zlib decompression
                using (var ms = new MemoryStream(data, offset, data.Length - offset))
                using (var zs = new Org.BouncyCastle.Utilities.Zlib.ZInputStream(ms))
                using (var output = new MemoryStream())
                {
                    zs.CopyTo(output);
                    return output.ToArray();
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[BinaryDecoder] Zlib decompression failed: {ex.Message}");
                // Fallback: try standard deflate (skip 2-byte zlib header)
                try
                {
                    using (var ms = new MemoryStream(data, offset + 2, data.Length - offset - 2))
                    using (var ds = new System.IO.Compression.DeflateStream(ms, System.IO.Compression.CompressionMode.Decompress))
                    using (var output = new MemoryStream())
                    {
                        ds.CopyTo(output);
                        return output.ToArray();
                    }
                }
                catch
                {
                    throw new InvalidOperationException($"Failed to decompress frame: {ex.Message}");
                }
            }
        }

        private BinaryNode ReadNode()
        {
            int listSize = ReadListSize();
            if (listSize == 0)
                return null;

            int descSize = listSize;
            string tag = ReadString();
            
            if (tag == null)
                throw new Exception("Invalid node: missing tag");

            var attrs = ReadAttributes((descSize - 1) / 2);
            
            // Baileys: if (listSize % 2 === 0) { read content }
            // Even listSize means there's content after attrs (1 tag + N*2 attrs + 1 content = even)
            // Odd listSize means no content (1 tag + N*2 attrs = odd)
            if (descSize % 2 != 0)
            {
                return new BinaryNode(tag, attrs);
            }

            var content = ReadContent();
            return new BinaryNode(tag, attrs, content);
        }

        private int ReadListSize()
        {
            byte b = ReadByte();
            
            switch (b)
            {
                case 0: // LIST_EMPTY
                    return 0;
                case 248: // LIST_8
                    return ReadByte();
                case 249: // LIST_16
                    return ReadUInt16();
                default:
                    throw new Exception($"Invalid list size byte: {b}");
            }
        }

        private Dictionary<string, string> ReadAttributes(int count)
        {
            var attrs = new Dictionary<string, string>();
            for (int i = 0; i < count; i++)
            {
                string key = ReadString();
                string value = ReadString();
                if (key != null && value != null)
                {
                    attrs[key] = value;
                }
            }
            return attrs;
        }

        private object ReadContent()
        {
            byte b = PeekByte();
            
            // Check if it's a list
            if (b == 0 || b == 248 || b == 249)
            {
                int listSize = ReadListSize();
                var children = new List<BinaryNode>();
                for (int i = 0; i < listSize; i++)
                {
                    var child = ReadNode();
                    if (child != null)
                        children.Add(child);
                }
                return children;
            }
            
            // Otherwise read as string/bytes
            return ReadStringOrBytes();
        }

        private string ReadString()
        {
            byte b = ReadByte();
            
            // Dictionary tokens (236-239) → double byte token lookup
            if (b >= 236 && b <= 239)
            {
                int dictIndex = b - 236;
                byte tokenIndex = ReadByte();
                return GetDoubleByteToken(dictIndex, tokenIndex);
            }
            
            // Single byte token (1-235)
            if (b > 0 && b < 236)
            {
                if (b < SINGLE_BYTE_TOKENS.Length)
                    return SINGLE_BYTE_TOKENS[b] ?? "";
                return "";
            }
            
            switch (b)
            {
                case 0: // LIST_EMPTY - empty string
                    return "";
                    
                case 247: // AD_JID - device JID with agent and device
                    {
                        byte agent = ReadByte();
                        byte device = ReadByte();
                        string user = ReadString();
                        return $"{user}.{agent}:{device}@s.whatsapp.net";
                    }
                
                case 248: // LIST_8 - shouldn't happen for string
                case 249: // LIST_16 - shouldn't happen for string
                    throw new Exception("Unexpected list in string position");
                
                case 250: // JID_PAIR - user@server
                    {
                        string user = ReadString();
                        string server = ReadString();
                        if (string.IsNullOrEmpty(user))
                            return $"@{server}";
                        return $"{user}@{server}";
                    }
                
                case 251: // HEX_8 - hex encoded string (for message IDs, etc.)
                    return ReadHexString();
                
                case 252: // BINARY_8
                    {
                        if (_position >= _data.Length) throw new EndOfStreamException();
                        int length = ReadByte();
                        if (!CanRead(length)) throw new EndOfStreamException($"Expected {length} bytes but only {_data.Length - _position} remain");
                        return ReadBinaryString(length);
                    }
                
                case 253: // BINARY_20
                    {
                        if (_position + 3 > _data.Length) throw new EndOfStreamException();
                        int length = ((ReadByte() & 0x0F) << 16) | (ReadByte() << 8) | ReadByte();
                         if (!CanRead(length)) throw new EndOfStreamException($"Expected {length} bytes but only {_data.Length - _position} remain");
                        return ReadBinaryString(length);
                    }
                
                case 254: // BINARY_32
                    {
                        if (_position + 4 > _data.Length) throw new EndOfStreamException();
                        int length = (int)ReadUInt32();
                         if (!CanRead(length)) throw new EndOfStreamException($"Expected {length} bytes but only {_data.Length - _position} remain");
                        return ReadBinaryString(length);
                    }
                
                case 255: // NIBBLE_8
                    return ReadNibbleString();
                
                default:
                    throw new Exception($"Unknown string type: {b}");
            }
        }

        private object ReadStringOrBytes()
        {
            byte b = ReadByte();
            
            switch (b)
            {
                case 252: // BINARY_8
                    {
                        if (_position >= _data.Length) throw new EndOfStreamException();
                        int length = ReadByte();
                        if (!CanRead(length)) throw new EndOfStreamException($"Expected {length} bytes but only {_data.Length - _position} remain");
                        return ReadBytes(length);
                    }
                
                case 253: // BINARY_20
                    {
                        if (_position + 3 > _data.Length) throw new EndOfStreamException();
                        int length = ((ReadByte() & 0x0F) << 16) | (ReadByte() << 8) | ReadByte();
                         if (!CanRead(length)) throw new EndOfStreamException($"Expected {length} bytes but only {_data.Length - _position} remain");
                        return ReadBytes(length);
                    }
                
                case 254: // BINARY_32
                    {
                        if (_position + 4 > _data.Length) throw new EndOfStreamException();
                        int length = (int)ReadUInt32();
                         if (!CanRead(length)) throw new EndOfStreamException($"Expected {length} bytes but only {_data.Length - _position} remain");
                        return ReadBytes(length);
                    }
                
                default:
                    // Token or other string type
                    _position--;
                    return ReadString();
            }
        }

        private string ReadBinaryString(int length)
        {
            var bytes = ReadBytes(length);
            return Encoding.UTF8.GetString(bytes);
        }

        private string ReadHexString()
        {
            // HEX_8 format: startByte indicates byte count (low 7 bits) and trim flag (high bit)
            // Each byte produces 2 hex chars, trim last char if high bit is set
            byte startByte = ReadByte();
            int byteCount = startByte & 0x7F;
            bool trimLast = (startByte & 0x80) != 0;
            
            var sb = new StringBuilder();
            for (int i = 0; i < byteCount; i++)
            {
                byte b = ReadByte();
                sb.Append(HexToChar((byte)((b >> 4) & 0x0F)));
                sb.Append(HexToChar((byte)(b & 0x0F)));
            }
            
            // If high bit was set, remove the last character
            if (trimLast && sb.Length > 0)
            {
                sb.Length--;
            }
            
            return sb.ToString();
        }

        private char HexToChar(byte hex)
        {
            if (hex < 10)
                return (char)('0' + hex);
            if (hex < 16)
                return (char)('A' + hex - 10);
            return '\0';
        }

        private string ReadNibbleString()
        {
            // NIBBLE_8 format: same as HEX_8 but with nibble chars (0-9, -, .)
            // startByte low 7 bits = byte count, high bit = trim last char
            byte startByte = ReadByte();
            int byteCount = startByte & 0x7F;
            bool trimLast = (startByte & 0x80) != 0;
            
            var sb = new StringBuilder();
            for (int i = 0; i < byteCount; i++)
            {
                byte b = ReadByte();
                sb.Append(NibbleToChar((byte)((b >> 4) & 0x0F)));
                sb.Append(NibbleToChar((byte)(b & 0x0F)));
            }
            
            // If high bit was set, remove the last character
            if (trimLast && sb.Length > 0)
            {
                sb.Length--;
            }
            
            return sb.ToString();
        }

        private char NibbleToChar(byte nibble)
        {
            if (nibble < 10)
                return (char)('0' + nibble);
            switch (nibble)
            {
                case 10: return '-';
                case 11: return '.';
                case 15: return '\0';
                default: throw new Exception($"Invalid nibble: {nibble}");
            }
        }

        private byte ReadByte()
        {
            if (_position >= _data.Length)
                throw new EndOfStreamException("Unexpected end of data");
            return _data[_position++];
        }

        private byte PeekByte()
        {
            if (_position >= _data.Length)
                throw new EndOfStreamException("Unexpected end of data");
            return _data[_position];
        }

        private ushort ReadUInt16()
        {
            return (ushort)((ReadByte() << 8) | ReadByte());
        }

        private uint ReadUInt32()
        {
            return (uint)((ReadByte() << 24) | (ReadByte() << 16) | (ReadByte() << 8) | ReadByte());
        }

        private byte[] ReadBytes(int length)
        {
            if (_position + length > _data.Length)
                throw new EndOfStreamException("Unexpected end of data");
            
            var bytes = new byte[length];
            Array.Copy(_data, _position, bytes, 0, length);
            _position += length;
            return bytes;
        }
        private bool CanRead(int length)
        {
            return _position + length <= _data.Length;
        }
    }
}
