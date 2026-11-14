// SPDX-License-Identifier: LGPL-3.0-or-later
// C# port of edge_tts/drm.py (edge-tts 7.3.1, https://github.com/rany2/edge-tts, LGPL-3.0).
// Generates the Sec-MS-GEC token the Microsoft Edge read-aloud service requires.
using System.Buffers.Binary;
using System.Numerics;
using System.Text;

namespace TruckingAdvisor.EdgeTts;

public static class EdgeTtsToken
{
    public const string TrustedClientToken = "6A5AA1D4EAFF4E9FB37E23D68491D6F4";
    private const long WinEpoch = 11644473600;

    private static readonly uint[] Sha256Initial = { 0x6A09E667, 0xBB67AE85, 0x3C6EF372, 0xA54FF53A, 0x510E527F, 0x9B05688C, 0x1F83D9AB, 0x5BE0CD19 };
    private static readonly uint[] Sha256State = { 0xFD9FBEA2, 0x91243CE7, 0xBE499094, 0xCD2E1E0A, 0x6C006F49, 0x04BD35AB, 0xFEB0A7E4, 0x03EFFBD2 };
    private static readonly uint[] Sha256Schedule =
    {
        0xEB24BF9E, 0x1558691F, 0xA0C09EE2, 0x0CFA32BA, 0xA622EE8C, 0xD1DD9491, 0xC0DD25A0,
        0xF9E70AB6, 0xF0C31573, 0x7058CE4E, 0x7188E606, 0x00E5AB33, 0xD258B060, 0x72766564
    };
    private static readonly uint[] RoundConstants =
    {
        0x428A2F98, 0x71374491, 0xB5C0FBCF, 0xE9B5DBA5, 0x3956C25B, 0x59F111F1, 0x923F82A4, 0xAB1C5ED5,
        0xD807AA98, 0x12835B01, 0x243185BE, 0x550C7DC3, 0x72BE5D74, 0x80DEB1FE, 0x9BDC06A7, 0xC19BF174,
        0xE49B69C1, 0xEFBE4786, 0x0FC19DC6, 0x240CA1CC, 0x2DE92C6F, 0x4A7484AA, 0x5CB0A9DC, 0x76F988DA,
        0x983E5152, 0xA831C66D, 0xB00327C8, 0xBF597FC7, 0xC6E00BF3, 0xD5A79147, 0x06CA6351, 0x14292967,
        0x27B70A85, 0x2E1B2138, 0x4D2C6DFC, 0x53380D13, 0x650A7354, 0x766A0ABB, 0x81C2C92E, 0x92722C85,
        0xA2BFE8A1, 0xA81A664B, 0xC24B8B70, 0xC76C51A3, 0xD192E819, 0xD6990624, 0xF40E3585, 0x106AA070,
        0x19A4C116, 0x1E376C08, 0x2748774C, 0x34B0BCB5, 0x391C0CB3, 0x4ED8AA4A, 0x5B9CCA4F, 0x682E6FF3,
        0x748F82EE, 0x78A5636F, 0x84C87814, 0x8CC70208, 0x90BEFFFA, 0xA4506CEB, 0xBEF9A3F7, 0xC67178F2
    };
    private const uint Sha256Adjustment = 0xC9DC06A7;
    private static readonly uint[] BlockConstants = BuildBlockConstants();
    private const uint Sigma0Adjustment = 0x059C0B80, Sigma1Adjustment = 0x000B92C0;

    private static readonly (uint Coef, uint Offset)[] Cs64Coefficients =
    {
        (0xE82E91B1, 0xB32700D0), (0x44DE24C6, 0x88DFF51D), (0x3BEB65BC, 0x29514038), (0xC89C5933, 0xB328CBDF),
        (0x8D1EC211, 0xD405B4D5), (0x314EF5D5, 0x26E55F3C), (0xE92491B1, 0x607801C6), (0xF0A5B9D5, 0x82B2C1FB),
        (0x06064CF3, 0xFA51A3DA), (0xDA79FA60, 0x207CC464), (0xBED20560, 0x3C3C74F3), (0x89F9C9F8, 0xD2D14B61),
        (0x35FA38C6, 0xBB04A2C1), (0xCB49E3C6, 0xC5626167)
    };
    private const uint Cs64Factor = 0x82893933, Cs64Adjustment0 = 0xF2F90B1A, Cs64Adjustment1 = 0x0B1A0000;

    private static uint[] BuildBlockConstants()
    {
        var c = (uint[])RoundConstants.Clone();
        c[14] = Sha256Adjustment;
        return c;
    }

    /// <summary>Clock correction (seconds) learned from the server's Date header after a 403.</summary>
    public static double ClockSkewSeconds { get; set; }

    /// <summary>The Sec-MS-GEC value for now (or for a given Unix time, for tests).</summary>
    public static string Generate(double? unixTime = null)
    {
        double now = unixTime ?? DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() / 1000.0 + ClockSkewSeconds;
        // Same arithmetic as Python's float maths: whole seconds since 1601, rounded down to 5 minutes, in 100 ns ticks.
        double ticks = now + WinEpoch;
        ticks -= ticks % 300;
        ticks *= 1e9 / 100;
        string toHash = ((ulong)ticks).ToString(System.Globalization.CultureInfo.InvariantCulture) + TrustedClientToken;
        var (cs64, sha) = TokenHash(Encoding.ASCII.GetBytes(toHash));
        return $"{cs64}-{sha}";
    }

    private static (string Cs64, string Sha256) TokenHash(byte[] data)
    {
        byte[] digest = Sha256Digest(data);
        var (w0, w1) = Cs64Digest(data, digest);
        var buf = new byte[8];
        BinaryPrimitives.WriteUInt32LittleEndian(buf, w0);
        BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(4), w1);
        return (Convert.ToHexString(buf), Convert.ToHexString(digest));
    }

    private static uint Rol16(uint v) => (v << 16) | (v >> 16);
    private static uint Sigma0(uint v) => BitOperations.RotateRight(v, 7) ^ BitOperations.RotateRight(v, 18) ^ (v >> 3);
    private static uint Sigma1(uint v) => BitOperations.RotateRight(v, 17) ^ BitOperations.RotateRight(v, 19) ^ (v >> 10);
    private static uint BigSigma0(uint a) => BitOperations.RotateRight(a, 2) ^ BitOperations.RotateRight(a, 13) ^ BitOperations.RotateRight(a, 22);
    private static uint BigSigma1(uint e) => BitOperations.RotateRight(e, 6) ^ BitOperations.RotateRight(e, 11) ^ BitOperations.RotateRight(e, 25);

    private static uint[] Rounds(uint[] state, uint[] words, int firstRound = 0)
    {
        uint a = state[0], b = state[1], c = state[2], d = state[3], e = state[4], f = state[5], g = state[6], h = state[7];
        var k = firstRound == 14 ? BlockConstants : RoundConstants;
        for (int i = firstRound; i < 64; i++)
        {
            uint t1 = h + BigSigma1(e) + (g ^ (e & (f ^ g))) + k[i] + words[i];
            uint t2 = BigSigma0(a) + ((a & b) | (c & (a | b)));
            h = g; g = f; f = e; e = d + t1; d = c; c = b; b = a; a = t1 + t2;
        }
        return new[] { a, b, c, d, e, f, g, h };
    }

    private static void Expand(List<uint> words, int firstWord = 16)
    {
        for (int i = firstWord; i < 64; i++)
            words.Add(Sigma1(words[i - 2]) + words[i - 7] + Sigma0(words[i - 15]) + words[i - 16]);
    }

    private static uint[] Init(byte[] data7)
    {
        var words = new List<uint>(new uint[14])
        {
            ((uint)data7[0] << 16) | ((uint)data7[1] << 8) | data7[2],
            BinaryPrimitives.ReadUInt32BigEndian(data7.AsSpan(3, 4))
        };
        for (int index = 16; index < 30; index++)
        {
            uint value = Sha256Schedule[index - 16];
            if (index - 2 >= 14) value += index == 16 ? Sigma1(words[index - 2]) ^ Sigma1Adjustment : Sigma1(words[index - 2]);
            if (index - 7 >= 14) value += words[index - 7];
            if (index - 15 >= 14) value += index == 29 ? Sigma0(words[index - 15]) ^ Sigma0Adjustment : Sigma0(words[index - 15]);
            words.Add(value);
        }
        words.Add(Sigma1(words[28]) + words[23] + Sigma0(words[15]) + words[14] + Sha256Adjustment - RoundConstants[14]);
        Expand(words, 31);
        var working = Rounds(Sha256State, words.ToArray(), 14);
        var result = new uint[8];
        for (int i = 0; i < 8; i++) result[i] = Sha256Initial[i] + working[i];
        return result;
    }

    private static byte[] Sha256Digest(byte[] data)
    {
        if (data.Length < 7) throw new ArgumentException("Token salt must contain at least seven bytes");
        int length = 57 + data.Length;
        int pad = ((55 - length) % 64 + 64) % 64;
        var padded = new List<byte>(data.Skip(7)) { 0x80 };
        padded.AddRange(new byte[pad]);
        var lenBytes = new byte[8];
        BinaryPrimitives.WriteUInt64BigEndian(lenBytes, (ulong)length * 8);
        padded.AddRange(lenBytes);
        var state = Init(data.Take(7).ToArray());
        var bytes = padded.ToArray();
        for (int off = 0; off < bytes.Length; off += 64)
        {
            var words = new List<uint>(64);
            for (int i = 0; i < 16; i++) words.Add(BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(off + i * 4, 4)));
            Expand(words);
            var working = Rounds(state, words.ToArray());
            for (int i = 0; i < 8; i++) state[i] += working[i];
        }
        var outBytes = new byte[32];
        for (int i = 0; i < 8; i++) BinaryPrimitives.WriteUInt32BigEndian(outBytes.AsSpan(i * 4), state[i]);
        return outBytes;
    }

    private static List<uint> WordsLe4Even(byte[] data)
    {
        int padLen = ((-data.Length) % 4 + 4) % 4;
        var padded = data.Concat(new byte[padLen]).ToArray();
        var words = new List<uint>();
        for (int i = 0; i < padded.Length; i += 4) words.Add(BinaryPrimitives.ReadUInt32LittleEndian(padded.AsSpan(i, 4)));
        if (words.Count % 2 == 1) words.Add(0);
        return words;
    }

    private static (uint, uint) Cs64Digest(byte[] data, byte[] digest)
    {
        uint keyA = BinaryPrimitives.ReadUInt32LittleEndian(digest.AsSpan(0, 4)) | 1;
        uint keyB = BinaryPrimitives.ReadUInt32LittleEndian(digest.AsSpan(4, 4)) | 1;
        uint[] factors = { keyA * Cs64Factor, keyB * Cs64Factor };
        var terms = new List<uint>();
        var offsets = new List<uint>();
        for (int i = 0; i < Cs64Coefficients.Length; i++)
        {
            terms.Add(Cs64Coefficients[i].Coef * factors[i % 2]);
            offsets.Add(Cs64Coefficients[i].Offset);
        }
        var words = WordsLe4Even(new byte[] { 0 }.Concat(data).ToArray());
        for (int i = 0; i < words.Count; i++)
        {
            uint word = words[i];
            uint term = word * (i % 2 == 0 ? keyA : keyB);
            uint offset = Rol16(word) * (i % 2 == 0 ? 0xEF0569FBu : 0xC31713DBu);
            if (i == 0)
            {
                term += Cs64Adjustment0 * factors[0];
                offset += Cs64Adjustment1;
            }
            terms.Add(term);
            offsets.Add(offset);
        }
        uint swapAcc = 0, swapLast = 0, state = 0, acc = 0;
        for (int i = 0; i < terms.Count; i += 2)
        {
            uint x = terms[i], y = terms[i + 1];
            uint t = Rol16(offsets[i] + x);
            uint q = t * 0x76F50F34;
            swapAcc += Rol16(q) * 0xC3EFEA97 + q;
            t = Rol16(offsets[i + 1] + y);
            q = t * 0x1881CE3C;
            swapLast = Rol16(q) * 0x35BD1EC9 + q;
            swapAcc += swapLast;

            state = state * keyA + x;
            state = Rol16(state) * 0xCF98B111;
            state = Rol16(state) * 0x87085B9F;
            state = Rol16(state) * 0x12CEB96D;
            state = Rol16(state) * 0x257E1D83;
            acc += state;
            t = state * keyB + y;
            t = Rol16(t) * 0xA27416F5;
            t = Rol16(t) * 0xD38396FF;
            state = Rol16(Rol16(t) * 0x7C932B89) * 0xBFA49F69;
            acc += state;
        }
        return (swapLast ^ state, swapAcc ^ acc);
    }
}
