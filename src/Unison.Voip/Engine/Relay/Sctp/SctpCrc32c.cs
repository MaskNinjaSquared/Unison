// =============================================================================
// SctpCrc32c
//
// Castagnoli CRC-32C used by SCTP (RFC 4960). Matches the Go/pion vectors.
// =============================================================================
namespace Unison.Voip.Engine.Relay.Sctp
{
    public static class SctpCrc32c
    {
        private static readonly uint[] Table = BuildTable();

        public static uint Compute(byte[] data, int offset, int length)
        {
            uint crc = 0xFFFFFFFFu;
            for (int i = 0; i < length; i++)
            {
                byte b = data[offset + i];
                crc = Table[(crc ^ b) & 0xFF] ^ (crc >> 8);
            }

            return crc ^ 0xFFFFFFFFu;
        }

        private static uint[] BuildTable()
        {
            const uint poly = 0x82F63B78u; // reflected Castagnoli
            var table = new uint[256];
            for (uint i = 0; i < 256; i++)
            {
                uint crc = i;
                for (int j = 0; j < 8; j++)
                {
                    if ((crc & 1) != 0)
                    {
                        crc = (crc >> 1) ^ poly;
                    }
                    else
                    {
                        crc >>= 1;
                    }
                }

                table[i] = crc;
            }

            return table;
        }
    }
}
