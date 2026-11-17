// =============================================================================
// RangeCoder
//
// Opus/CELT range entropy decoder and its exact inverse encoder. Range-coded
// symbols travel at the front of the buffer, raw bits at the back.
// Ports: meowcaller mlow/rangecoder.go (MIT)
// =============================================================================
using System;

namespace Unison.Voip.Engine.Mlow
{
    internal static class RangeCoderConst
    {
        public const int SymBits = 8;
        public const int CodeBits = 32;
        public const uint SymMax = 255;
        public const uint CodeTop = 1u << (CodeBits - 1);
        public const uint CodeBot = CodeTop >> SymBits;
        public const int CodeExtra = (CodeBits - 2) % SymBits + 1;
        public const int WindowSize = 32;
        public const int UintBits = 8;
        public const int CodeShift = CodeBits - SymBits - 1;

        /// <summary>floor(log2(x))+1 for x&gt;0, 0 for x==0 (Go bits.Len32).</summary>
        public static int Ilog(uint x)
        {
            int n = 0;
            while (x != 0)
            {
                x >>= 1;
                n++;
            }

            return n;
        }

        /// <summary>(1&lt;&lt;n)-1 with Go shift semantics (n&gt;=32 yields all ones).</summary>
        public static uint LowMask(uint n)
        {
            return n >= 32 ? 0xFFFFFFFFu : (1u << (int)n) - 1;
        }

        public static uint Mini(uint a, uint b)
        {
            return a < b ? a : b;
        }
    }

    /// <summary>Opus/CELT range decoder. <see cref="Err"/> is a sticky decode error.</summary>
    public sealed class RangeDecoder
    {
        private readonly byte[] _buf;
        private readonly int _base;
        private readonly uint _storage;
        private uint _endOffs;
        private uint _endWindow;
        private int _nendBits;
        private int _nbitsTotal;
        private uint _offs;
        private uint _rng;
        private uint _val;
        private uint _ext;
        private int _rem;

        public RangeDecoder(byte[] buf)
            : this(buf, 0, buf == null ? 0 : buf.Length)
        {
        }

        public RangeDecoder(byte[] buf, int offset, int count)
        {
            _buf = buf ?? new byte[0];
            _base = offset;
            _storage = (uint)Math.Max(0, count);
            _nbitsTotal = RangeCoderConst.CodeBits + 1
                - ((RangeCoderConst.CodeBits - RangeCoderConst.CodeExtra) / RangeCoderConst.SymBits) * RangeCoderConst.SymBits;
            _rng = 1u << RangeCoderConst.CodeExtra;
            _rem = (int)ReadByte();
            _val = _rng - 1 - (uint)(_rem >> (RangeCoderConst.SymBits - RangeCoderConst.CodeExtra));
            Normalize();
        }

        public int Err { get; private set; }

        private uint ReadByte()
        {
            if (_offs < _storage)
            {
                byte b = _buf[_base + (int)_offs];
                _offs++;
                return b;
            }

            return 0;
        }

        private uint ReadByteFromEnd()
        {
            if (_endOffs < _storage)
            {
                _endOffs++;
                return _buf[_base + (int)(_storage - _endOffs)];
            }

            return 0;
        }

        private void Normalize()
        {
            while (_rng <= RangeCoderConst.CodeBot)
            {
                _nbitsTotal += RangeCoderConst.SymBits;
                _rng <<= RangeCoderConst.SymBits;
                int sym0 = _rem;
                _rem = (int)ReadByte();
                int sym = (sym0 << RangeCoderConst.SymBits | _rem) >> (RangeCoderConst.SymBits - RangeCoderConst.CodeExtra);
                _val = unchecked((_val << RangeCoderConst.SymBits) + (RangeCoderConst.SymMax & ~(uint)sym)) & (RangeCoderConst.CodeTop - 1);
            }
        }

        /// <summary>Returns the cumulative frequency in [0, ft) of the next symbol; follow with <see cref="Update"/>.</summary>
        public uint Decode(uint ft)
        {
            if (ft == 0)
            {
                Err = 1;
                _ext = 1;
                return 0;
            }

            _ext = _rng / ft;
            if (_ext == 0)
            {
                Err = 1;
                _ext = 1;
                return 0;
            }

            uint s = _val / _ext;
            return ft - RangeCoderConst.Mini(s + 1, ft);
        }

        private uint DecodeBin(uint bitsN)
        {
            _ext = bitsN >= 32 ? 0 : _rng >> (int)bitsN;
            if (_ext == 0)
            {
                Err = 1;
                _ext = 1;
                return 0;
            }

            uint s = _val / _ext;
            uint ft = bitsN >= 32 ? 0 : 1u << (int)bitsN;
            return ft - RangeCoderConst.Mini(s + 1, ft);
        }

        /// <summary>Decodes a uniform nbits-bit symbol directly off the range stream.</summary>
        public uint DecodeRawSymbol(uint nbits)
        {
            uint sym = DecodeBin(nbits);
            Update(sym, sym + 1, nbits >= 32 ? 0 : 1u << (int)nbits);
            return sym;
        }

        /// <summary>Advances past the symbol with cumulative range [fl, fh) out of ft.</summary>
        public void Update(uint fl, uint fh, uint ft)
        {
            uint s = unchecked(_ext * (ft - fh));
            _val = unchecked(_val - s);
            if (fl > 0)
            {
                _rng = unchecked(_ext * (fh - fl));
            }
            else
            {
                _rng = unchecked(_rng - s);
            }

            Normalize();
        }

        /// <summary>Decodes one bit with P(0) = 1/2^logp.</summary>
        public int BitLogp(uint logp)
        {
            uint r = _rng;
            uint dv = _val;
            uint s = logp >= 32 ? 0 : r >> (int)logp;
            int ret = dv < s ? 1 : 0;
            if (ret == 0)
            {
                _val = dv - s;
                _rng = r - s;
            }
            else
            {
                _rng = s;
            }

            Normalize();
            return ret;
        }

        /// <summary>Decodes a symbol against an inverse-CDF table; ftb = log2(ft).</summary>
        public int DecodeIcdf(byte[] icdf, uint ftb)
        {
            if (icdf == null || icdf.Length == 0)
            {
                Err = 1;
                return 0;
            }

            uint s0 = _rng;
            uint dv = _val;
            uint r = ftb >= 32 ? 0 : s0 >> (int)ftb;
            int ret = -1;
            uint t;
            uint s = s0;
            while (true)
            {
                t = s;
                ret++;
                s = unchecked(r * icdf[ret]);
                if (dv >= s || ret >= icdf.Length - 1)
                {
                    break;
                }
            }

            _val = unchecked(dv - s);
            _rng = unchecked(t - s);
            Normalize();
            return ret;
        }

        /// <summary>Decodes against a uint16 cumulative CDF; the total is cdf[n-1]-cdf[0].</summary>
        public int DecodeCdf(ushort[] cdf)
        {
            return DecodeCdf(cdf, 0, cdf == null ? 0 : cdf.Length);
        }

        public int DecodeCdf(ArraySegment<ushort> cdf)
        {
            return DecodeCdf(cdf.Array, cdf.Offset, cdf.Count);
        }

        public int DecodeCdf(ushort[] cdf, int offset, int n)
        {
            if (n < 2)
            {
                Err = 1;
                return 0;
            }

            uint b = cdf[offset];
            if (cdf[offset + n - 1] <= b)
            {
                Err = 1;
                return 0;
            }

            uint ft = cdf[offset + n - 1] - b;
            uint fs = Decode(ft);
            uint target = b + fs;
            int k = 0;
            while (k < n - 1)
            {
                if (cdf[offset + k + 1] > target)
                {
                    break;
                }

                k++;
            }

            Update(cdf[offset + k] - b, cdf[offset + k + 1] - b, ft);
            return k;
        }

        /// <summary>Reads n raw bits from the back of the buffer, LSB-first.</summary>
        public uint BitsN(uint n)
        {
            uint window = _endWindow;
            int available = _nendBits;
            if ((uint)available < n)
            {
                while (true)
                {
                    window |= ReadByteFromEnd() << available;
                    available += RangeCoderConst.SymBits;
                    if ((uint)available > RangeCoderConst.WindowSize - RangeCoderConst.SymBits)
                    {
                        break;
                    }
                }
            }

            uint ret = window & RangeCoderConst.LowMask(n);
            window = n >= 32 ? 0 : window >> (int)n;
            available -= (int)n;
            _endWindow = window;
            _nendBits = available;
            _nbitsTotal += (int)n;
            return ret;
        }

        /// <summary>Decodes an integer uniformly distributed in [0, ft0) for ft0 &gt; 1.</summary>
        public uint DecodeUint(uint ft0)
        {
            uint ft = ft0 - 1;
            int ftb = RangeCoderConst.Ilog(ft);
            if (ftb > RangeCoderConst.UintBits)
            {
                ftb -= RangeCoderConst.UintBits;
                uint t = (ft >> ftb) + 1;
                uint s = Decode(t);
                Update(s, s + 1, t);
                uint v = (s << ftb) | BitsN((uint)ftb);
                if (v <= ft)
                {
                    return v;
                }

                Err = 1;
                return ft;
            }

            ft++;
            uint s2 = Decode(ft);
            Update(s2, s2 + 1, ft);
            return s2;
        }

        /// <summary>Decodes the 64-symbol uniform fine-lag value.</summary>
        public int Decode64FineSym()
        {
            _ext = _rng >> 6;
            if (_ext == 0)
            {
                Err = 1;
                _ext = 1;
                return 0;
            }

            uint s = _val / _ext;
            long sym = 63L - s;
            if (sym < 0)
            {
                sym = 0;
            }
            else if (sym > 64)
            {
                sym = 64;
            }

            Update((uint)sym, (uint)sym + 1, 64);
            return (int)sym;
        }

        /// <summary>Number of bits consumed so far, rounded up.</summary>
        public int Tell()
        {
            return _nbitsTotal - RangeCoderConst.Ilog(_rng);
        }
    }

    /// <summary>Opus/CELT range encoder, the exact inverse of <see cref="RangeDecoder"/>.</summary>
    public sealed class RangeEncoder
    {
        private readonly byte[] _buf;
        private readonly uint _storage;
        private uint _endOffs;
        private uint _endWindow;
        private int _nendBits;
        private int _nbitsTotal;
        private uint _offs;
        private uint _rng;
        private uint _val;
        private uint _ext;
        private int _rem;

        public RangeEncoder(int size)
        {
            _buf = new byte[size];
            _storage = (uint)size;
            _nbitsTotal = RangeCoderConst.CodeBits + 1;
            _rng = RangeCoderConst.CodeTop;
            _rem = -1;
        }

        /// <summary>Sticky encode error (-1 on failure).</summary>
        public int Err { get; private set; }

        private void WriteByte(uint b)
        {
            if (_offs + _endOffs < _storage)
            {
                _buf[_offs] = (byte)b;
                _offs++;
            }
            else
            {
                Err = -1;
            }
        }

        private void WriteByteAtEnd(uint b)
        {
            if (_offs + _endOffs < _storage)
            {
                _endOffs++;
                _buf[_storage - _endOffs] = (byte)b;
            }
            else
            {
                Err = -1;
            }
        }

        private void CarryOut(int c)
        {
            if ((uint)c != RangeCoderConst.SymMax)
            {
                int carry = c >> RangeCoderConst.SymBits;
                if (_rem >= 0)
                {
                    WriteByte((uint)(_rem + carry));
                }

                if (_ext > 0)
                {
                    uint sym = (uint)(((int)RangeCoderConst.SymMax + carry) & (int)RangeCoderConst.SymMax);
                    while (true)
                    {
                        WriteByte(sym);
                        _ext--;
                        if (_ext == 0)
                        {
                            break;
                        }
                    }
                }

                _rem = c & (int)RangeCoderConst.SymMax;
            }
            else
            {
                _ext++;
            }
        }

        private void Normalize()
        {
            while (_rng <= RangeCoderConst.CodeBot)
            {
                CarryOut((int)(_val >> RangeCoderConst.CodeShift));
                _val = (_val << RangeCoderConst.SymBits) & (RangeCoderConst.CodeTop - 1);
                _rng <<= RangeCoderConst.SymBits;
                _nbitsTotal += RangeCoderConst.SymBits;
            }
        }

        /// <summary>Encodes the symbol with cumulative range [fl, fh) out of ft.</summary>
        public void Encode(uint fl, uint fh, uint ft)
        {
            if (ft == 0)
            {
                Err = -1;
                return;
            }

            uint r = _rng / ft;
            if (fl > 0)
            {
                _val = unchecked(_val + _rng - r * (ft - fl));
                _rng = unchecked(r * (fh - fl));
            }
            else
            {
                _rng = unchecked(_rng - r * (ft - fh));
            }

            Normalize();
        }

        /// <summary>Encodes one bit with P(0) = 1/2^logp.</summary>
        public void BitLogp(int val, uint logp)
        {
            uint r = _rng;
            uint l = _val;
            uint s = logp >= 32 ? 0 : r >> (int)logp;
            uint r2 = r - s;
            if (val != 0)
            {
                _val = unchecked(l + r2);
                _rng = s;
            }
            else
            {
                _rng = r2;
            }

            Normalize();
        }

        /// <summary>Encodes symbol s against an inverse-CDF table; ftb = log2(ft).</summary>
        public void EncodeIcdf(int s, byte[] icdf, uint ftb)
        {
            uint r = ftb >= 32 ? 0 : _rng >> (int)ftb;
            if (s > 0)
            {
                _val = unchecked(_val + _rng - r * icdf[s - 1]);
                _rng = unchecked(r * (byte)(icdf[s - 1] - icdf[s]));
            }
            else
            {
                _rng = unchecked(_rng - r * icdf[s]);
            }

            Normalize();
        }

        /// <summary>Encodes symbol s against a uint16 cumulative CDF table.</summary>
        public void EncodeCdf(int s, ushort[] cdf)
        {
            EncodeCdf(s, cdf, 0, cdf == null ? 0 : cdf.Length);
        }

        public void EncodeCdf(int s, ArraySegment<ushort> cdf)
        {
            EncodeCdf(s, cdf.Array, cdf.Offset, cdf.Count);
        }

        public void EncodeCdf(int s, ushort[] cdf, int offset, int n)
        {
            if (n < 2 || s < 0 || s + 1 >= n)
            {
                Err = -1;
                return;
            }

            uint b = cdf[offset];
            if (cdf[offset + n - 1] <= b)
            {
                Err = -1;
                return;
            }

            uint ft = cdf[offset + n - 1] - b;
            Encode(cdf[offset + s] - b, cdf[offset + s + 1] - b, ft);
        }

        /// <summary>Writes the low n bits of fl as raw bits toward the back of the buffer.</summary>
        public void BitsN(uint fl, uint n)
        {
            uint window = _endWindow;
            int used = _nendBits;
            if (used + (int)n > RangeCoderConst.WindowSize)
            {
                while (true)
                {
                    WriteByteAtEnd(window & RangeCoderConst.SymMax);
                    window >>= RangeCoderConst.SymBits;
                    used -= RangeCoderConst.SymBits;
                    if (used < RangeCoderConst.SymBits)
                    {
                        break;
                    }
                }
            }

            window |= used >= 32 ? 0 : fl << used;
            used += (int)n;
            _endWindow = window;
            _nendBits = used;
            _nbitsTotal += (int)n;
        }

        /// <summary>Encodes an integer uniformly distributed in [0, ft0).</summary>
        public void EncodeUint(uint fl, uint ft0)
        {
            uint ft = ft0 - 1;
            int ftb = RangeCoderConst.Ilog(ft);
            if (ftb > RangeCoderConst.UintBits)
            {
                int shift = ftb - RangeCoderConst.UintBits;
                uint t = (ft >> shift) + 1;
                Encode(fl >> shift, (fl >> shift) + 1, t);
                BitsN(fl & RangeCoderConst.LowMask((uint)shift), (uint)shift);
            }
            else
            {
                Encode(fl, fl + 1, ft + 1);
            }
        }

        /// <summary>Encodes a uniform nbits-bit symbol on the range stream.</summary>
        public void EncodeRawSymbol(uint sym, uint nbits)
        {
            Encode(sym, sym + 1, nbits >= 32 ? 0 : 1u << (int)nbits);
        }

        /// <summary>Encodes the 64-symbol uniform fine-lag value.</summary>
        public void Encode64FineSym(int sym)
        {
            Encode((uint)sym, (uint)sym + 1, 64);
        }

        /// <summary>Flushes the range coder and merges the back raw-bit stream.</summary>
        public void Done()
        {
            int l = RangeCoderConst.CodeBits - RangeCoderConst.Ilog(_rng);
            uint msk = l >= 32 ? 0 : (RangeCoderConst.CodeTop - 1) >> l;
            uint end = unchecked(_val + msk) & ~msk;
            if ((end | msk) >= unchecked(_val + _rng))
            {
                l++;
                msk >>= 1;
                end = unchecked(_val + msk) & ~msk;
            }

            while (l > 0)
            {
                CarryOut((int)(end >> RangeCoderConst.CodeShift));
                end = (end << RangeCoderConst.SymBits) & (RangeCoderConst.CodeTop - 1);
                l -= RangeCoderConst.SymBits;
            }

            if (_rem >= 0 || _ext > 0)
            {
                CarryOut(0);
            }

            uint window = _endWindow;
            int used = _nendBits;
            while (used >= RangeCoderConst.SymBits)
            {
                WriteByteAtEnd(window & RangeCoderConst.SymMax);
                window >>= RangeCoderConst.SymBits;
                used -= RangeCoderConst.SymBits;
            }

            if (Err == 0)
            {
                for (uint i = _offs; i < _storage - _endOffs; i++)
                {
                    _buf[i] = 0;
                }

                if (used > 0)
                {
                    if (_endOffs >= _storage - _offs)
                    {
                        Err = -1;
                    }
                    else
                    {
                        _buf[_storage - _endOffs - 1] |= (byte)window;
                    }
                }
            }
        }

        /// <summary>The encoder's output buffer (valid after <see cref="Done"/>).</summary>
        public byte[] Bytes()
        {
            return _buf;
        }

        /// <summary>Front range bytes plus back raw-bit bytes (the gap is zero padding).</summary>
        public int ConsumedLen()
        {
            return (int)(_offs + _endOffs);
        }
    }
}
