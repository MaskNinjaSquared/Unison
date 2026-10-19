// =============================================================================
// MessageKeyAltJidScan
//
// Reads a user JID out of MessageKey bytes from a field the generated schema
// does not declare. Field 4 is participant and is already read the typed way,
// so it is skipped. Later length-delimited fields may hold participantAlt or
// remoteJidAlt.
// =============================================================================
using System.Text;

namespace Unison.Core.Helpers
{
    public static class MessageKeyAltJidScan
    {
        public static string Read(byte[] bytes)
        {
            if (bytes == null || bytes.Length == 0)
            {
                return null;
            }

            int index = 0;
            while (index < bytes.Length)
            {
                if (!TryReadVarint(bytes, ref index, out ulong tag))
                {
                    break;
                }

                int fieldNumber = (int)(tag >> 3);
                int wireType = (int)(tag & 0x7);
                if (wireType == 2)
                {
                    if (!TryReadVarint(bytes, ref index, out ulong length) ||
                        length > (ulong)(bytes.Length - index))
                    {
                        break;
                    }

                    int len = (int)length;
                    if (fieldNumber != 4 && len > 0 && len < 256)
                    {
                        string candidate = Encoding.UTF8.GetString(bytes, index, len);
                        if (UserJidShape.Matches(candidate))
                        {
                            return candidate;
                        }
                    }

                    index += len;
                }
                else if (wireType == 0)
                {
                    if (!TryReadVarint(bytes, ref index, out _))
                    {
                        break;
                    }
                }
                else if (wireType == 1)
                {
                    index += 8;
                }
                else if (wireType == 5)
                {
                    index += 4;
                }
                else
                {
                    break;
                }
            }

            return null;
        }

        private static bool TryReadVarint(byte[] buffer, ref int index, out ulong value)
        {
            value = 0;
            int shift = 0;
            while (index < buffer.Length && shift < 64)
            {
                byte b = buffer[index++];
                value |= (ulong)(b & 0x7F) << shift;
                if ((b & 0x80) == 0)
                {
                    return true;
                }

                shift += 7;
            }

            return false;
        }
    }
}
