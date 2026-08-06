using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace AudioConvert.Services
{
    internal static class TeaCipher
    {
        private const int Rounds = 16;
        private const uint Delta = 0x9E3779B9u;
        private const uint ExpectedSum = 0xE3779B90u;
        private const int BlockSize = 8;

        public static ulong EcbDecrypt(ulong value, uint[] key)
        {
            uint y = (uint)(value >> 32);
            uint z = (uint)value;
            uint sum = ExpectedSum;

            for (int i = 0; i < Rounds; i++)
            {
                z -= ((y << 4) + key[2]) ^ (y + sum) ^ ((y >> 5) + key[3]);
                y -= ((z << 4) + key[0]) ^ (z + sum) ^ ((z >> 5) + key[1]);
                sum -= Delta;
            }

            return ((ulong)y << 32) | z;
        }

        public static byte[]? CbcDecrypt(ReadOnlySpan<byte> cipher, uint[] key)
        {
            if (cipher.Length % BlockSize != 0 || cipher.Length < BlockSize * 2)
                return null;

            ulong iv1 = 0, iv2 = 0;
            byte[] header = new byte[BlockSize * 2];

            DecryptRound(header.AsSpan(0, BlockSize), cipher, ref iv1, ref iv2, key);
            DecryptRound(header.AsSpan(BlockSize), cipher.Slice(BlockSize), ref iv1, ref iv2, key);
            int inOffset = BlockSize * 2;

            int hdrSkipLen = 1 + (header[0] & 7) + 2;
            int realPlainLen = cipher.Length - hdrSkipLen - 7;
            if (realPlainLen <= 0)
                return null;

            byte[] result = new byte[realPlainLen];
            int copyLen = Math.Min(header.Length - hdrSkipLen, realPlainLen);
            Buffer.BlockCopy(header, hdrSkipLen, result, 0, copyLen);
            int outOffset = copyLen;

            if (realPlainLen > copyLen)
            {
                int loopBytes = cipher.Length - BlockSize * 3;
                while (loopBytes > 0)
                {
                    DecryptRound(result.AsSpan(outOffset, BlockSize), cipher.Slice(inOffset), ref iv1, ref iv2, key);
                    inOffset += BlockSize;
                    outOffset += BlockSize;
                    loopBytes -= BlockSize;
                }

                DecryptRound(header.AsSpan(BlockSize), cipher.Slice(inOffset), ref iv1, ref iv2, key);
                result[outOffset] = header[BlockSize];
            }

            ulong verify = BinaryPrimitives.ReadUInt64BigEndian(header.AsSpan(BlockSize)) << 8;
            if (verify != 0)
                return null;

            return result;
        }

        public static byte[]? CbcDecrypt(ReadOnlySpan<byte> cipher, ReadOnlySpan<byte> keyBytes)
        {
            uint[] key = new uint[4];
            for (int i = 0; i < 4; i++)
                key[i] = BinaryPrimitives.ReadUInt32BigEndian(keyBytes.Slice(i * 4));
            return CbcDecrypt(cipher, key);
        }

        private static void DecryptRound(
            Span<byte> plain, ReadOnlySpan<byte> cipher,
            ref ulong iv1, ref ulong iv2, uint[] key)
        {
            ulong iv1Next = BinaryPrimitives.ReadUInt64BigEndian(cipher);
            ulong iv2Next = EcbDecrypt(iv1Next ^ iv2, key);
            ulong plainVal = iv2Next ^ iv1;
            iv1 = iv1Next;
            iv2 = iv2Next;
            BinaryPrimitives.WriteUInt64BigEndian(plain, plainVal);
        }
    }

    internal static class EKeyDecryptor
    {
        private const string V2Prefix = "UVFNdXNpYyBFbmNWMixLZXk6";

        private static readonly byte[] V2Key1 =
        {
        0x33, 0x38, 0x36, 0x5A, 0x4A, 0x59, 0x21, 0x40,
        0x23, 0x2A, 0x24, 0x25, 0x5E, 0x26, 0x29, 0x28,
    };

        private static readonly byte[] V2Key2 =
        {
        0x2A, 0x2A, 0x23, 0x21, 0x28, 0x23, 0x24, 0x25,
        0x26, 0x5E, 0x61, 0x31, 0x63, 0x5A, 0x2C, 0x54,
    };

        public static byte[]? Decrypt(string ekey)
        {
            if (ekey.StartsWith(V2Prefix, StringComparison.Ordinal))
                return DecryptV2(ekey.Substring(V2Prefix.Length));

            return DecryptV1(ekey);
        }

        private static byte[]? DecryptV1(string ekey)
        {
            byte[] decoded;
            try
            {
                string b64 = ekey.Replace('-', '+').Replace('_', '/');
                switch (b64.Length % 4)
                {
                    case 2: b64 += "=="; break;
                    case 3: b64 += "="; break;
                }
                decoded = Convert.FromBase64String(b64);
            }
            catch (FormatException exception)
            {
                Debug.WriteLine($"Failed to decode base64 ekey: {exception}");
                return null;
            }
            catch (ArgumentException exception)
            {
                Debug.WriteLine($"Invalid ekey content: {exception}");
                return null;
            }

            int len = decoded.Length;
            while (len > 0 && decoded[len - 1] == 0) len--;
            if (len < 8)
                return null;

            uint[] teaKey =
            {
            0x69005600u | ((uint)decoded[0] << 16) | decoded[1],
            0x46003800u | ((uint)decoded[2] << 16) | decoded[3],
            0x2b002000u | ((uint)decoded[4] << 16) | decoded[5],
            0x15000b00u | ((uint)decoded[6] << 16) | decoded[7],
        };

            byte[]? decrypted = TeaCipher.CbcDecrypt(decoded.AsSpan(8, len - 8), teaKey);
            if (decrypted == null || decrypted.Length == 0)
                return null;

            byte[] result = new byte[8 + decrypted.Length];
            Buffer.BlockCopy(decoded, 0, result, 0, 8);
            Buffer.BlockCopy(decrypted, 0, result, 8, decrypted.Length);
            return result;
        }

        private static byte[]? DecryptV2(string ekeyV2)
        {
            byte[] data = Encoding.ASCII.GetBytes(ekeyV2);
            byte[]? step1 = TeaCipher.CbcDecrypt(data, V2Key1);
            if (step1 == null) return null;

            byte[]? step2 = TeaCipher.CbcDecrypt(step1, V2Key2);
            if (step2 == null) return null;

            return DecryptV1(Encoding.ASCII.GetString(step2));
        }
    }

    internal interface IQmc2Decryptor
    {
        void Decrypt(Span<byte> data, long offset);
    }

    internal sealed class Qmc2MapDecryptor : IQmc2Decryptor
    {
        private const int MapKeySize = 128;
        private const long MapIndexOffset = 71214;
        private const long MapOffsetBoundary = 0x7FFF;

        private readonly byte[] _key = new byte[MapKeySize];

        public Qmc2MapDecryptor(ReadOnlySpan<byte> key)
        {
            int n = key.Length;
            for (int i = 0; i < MapKeySize; i++)
            {
                long j = ((long)i * i + MapIndexOffset) % n;
                int shift = (int)((j + 4) % 8);
                _key[i] = (byte)((key[(int)j] << shift) | (key[(int)j] >> shift));
            }
        }

        public void Decrypt(Span<byte> data, long offset)
        {
            for (int i = 0; i < data.Length; i++)
            {
                long idx = (offset <= MapOffsetBoundary) ? offset : (offset % MapOffsetBoundary);
                data[i] ^= _key[(int)(idx % MapKeySize)];
                offset++;
            }
        }
    }

    internal sealed class Qmc2Rc4Decryptor : IQmc2Decryptor
    {
        private const int FirstSegmentSize = 0x0080;
        private const int OtherSegmentSize = 0x1400;
        private const int Rc4StreamSize = OtherSegmentSize + 512;

        private readonly byte[] _key;
        private readonly double _hash;
        private readonly byte[] _keyStream = new byte[Rc4StreamSize];

        public Qmc2Rc4Decryptor(ReadOnlySpan<byte> key)
        {
            _key = key.ToArray();
            _hash = ComputeKeyHash(_key);

            int n = key.Length;
            byte[] state = new byte[n];
            for (int i = 0; i < n; i++)
                state[i] = (byte)i;

            for (int i = 0, j = 0; i < n; i++)
            {
                j = (j + state[i] + key[i]) % n;
                (state[i], state[j]) = (state[j], state[i]);
            }

            int si = 0, sj = 0;
            for (int k = 0; k < Rc4StreamSize; k++)
            {
                si = (si + 1) % n;
                sj = (sj + state[si]) % n;
                (state[si], state[sj]) = (state[sj], state[si]);
                _keyStream[k] = state[(state[si] + state[sj]) % n];
            }
        }

        public void Decrypt(Span<byte> data, long offset)
        {
            int pos = 0;
            if (offset < FirstSegmentSize)
            {
                int n = DecryptFirstSegment(data, offset);
                pos += n;
                offset += n;
            }

            while (pos < data.Length)
            {
                int n = DecryptOtherSegment(data.Slice(pos), offset);
                pos += n;
                offset += n;
            }
        }

        private int DecryptFirstSegment(Span<byte> data, long offset)
        {
            int n = _key.Length;
            int processLen = (int)Math.Min(data.Length, FirstSegmentSize - offset);
            for (int i = 0; i < processLen; i++)
            {
                long idx = GetSegmentKey(_hash, offset, _key[(int)(offset % n)]) % (uint)n;
                data[i] ^= _key[(int)idx];
                offset++;
            }
            return processLen;
        }

        private int DecryptOtherSegment(Span<byte> data, long offset)
        {
            int n = _key.Length;
            long segmentIdx = offset / OtherSegmentSize;
            long segmentOffset = offset % OtherSegmentSize;

            long skipLen = GetSegmentKey(_hash, segmentIdx, _key[(int)(segmentIdx % n)]) & 0x1FF;
            int processLen = (int)Math.Min(data.Length, OtherSegmentSize - segmentOffset);
            int streamStart = (int)(skipLen + segmentOffset);
            for (int i = 0; i < processLen; i++)
            {
                data[i] ^= _keyStream[streamStart + i];
            }
            return processLen;
        }

        private static long GetSegmentKey(double keyHash, long segmentId, byte seed)
        {
            if (seed == 0) return 0;
            double result = keyHash / ((double)seed * (segmentId + 1)) * 100.0;
            return (long)result;
        }

        private static double ComputeKeyHash(byte[] key)
        {
            uint hash = 1;
            foreach (byte b in key)
            {
                if (b == 0) continue;
                uint next = hash * (uint)b;
                if (next <= hash) break;
                hash = next;
            }
            return hash;
        }
    }

    internal static class Qmc2Factory
    {
        public static IQmc2Decryptor? Create(string ekey)
        {
            byte[]? key = EKeyDecryptor.Decrypt(ekey);
            if (key == null || key.Length == 0)
                return null;

            return key.Length < 300
                ? new Qmc2MapDecryptor(key)
                : new Qmc2Rc4Decryptor(key);
        }
    }
}
