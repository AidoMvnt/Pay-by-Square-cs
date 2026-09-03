using System;
using System.Collections.Generic;

/// <summary>
/// Lzma1Encoder — pure C# LZMA1 compressor.
/// AI-generated with Claude Code, as part of a multi-agent project
/// (Claude Code, Hermes Agent, Qwen 3.8-128K, Gemma 4-128K) — see README.
///
/// Two modes:
///   Compress(data)     -> standard 13-byte-header .lzma stream
///                          (decodes with `xz --format=lzma -d`, 7-Zip, etc.)
///   CompressRaw(data)  -> headerless raw stream, equivalent to:
///                          xz --format=raw --lzma1=lc=3,lp=0,pb=2,dict=128KiB -c -
///                          (parameters must be supplied out-of-band to the
///                          decoder, exactly as in that command)
///
/// This is a *correct*, not *optimal*, encoder: hash-chain match finding
/// with 1-step lazy matching and rep-distance preference, not full optimal
/// parsing. Compression ratio is behind 7-Zip/xz, but every byte round-trips.
/// </summary>
public sealed class Lzma1Encoder
{
    private const int NumBitModelTotalBits = 11;
    private const int BitModelTotal = 1 << NumBitModelTotalBits; // 2048
    private const int NumMoveBits = 5;
    private const int ProbInit = BitModelTotal >> 1; // 1024
    private const uint TopValue = 1u << 24;

    private const int NumStates = 12;
    private const int NumLenToPosStates = 4;
    private const int NumAlignBits = 4;
    private const int EndPosModelIndex = 14;
    private const int NumFullDistances = 128; // 1 << (EndPosModelIndex >> 1)
    private const int MatchMinLen = 2;
    private const int MaxLen = 273; // MatchMinLen(2) + 8 + 8 + 256 - 1

    private readonly int[] data;
    private readonly int n;
    private readonly int lc, lp, pb;
    private readonly long dictSize;

    // range coder
    private ulong low = 0;
    private uint range = 0xFFFFFFFFu;
    private byte cache = 0;
    private long cacheSize = 1;
    private readonly List<byte> outBytes = new List<byte>();

    private int state = 0;
    private int rep0 = 0, rep1 = 0, rep2 = 0, rep3 = 0;
    private int pos = 0;

    private readonly int[][] isMatch;      // [state][posState]
    private readonly int[] isRep;          // [state]
    private readonly int[] isRepG0;
    private readonly int[] isRepG1;
    private readonly int[] isRepG2;
    private readonly int[][] isRep0Long;   // [state][posState]

    private readonly int[][] posSlot;      // [lenState][0..63]
    private readonly int[] alignProbs;     // [0..15]
    private readonly int[] posDecoders;    // shared reverse-bit-tree probs

    private readonly int[] lenChoice;
    private readonly int[][] lenLow, lenMid;
    private readonly int[] lenHigh;
    private readonly int[] repLenChoice;
    private readonly int[][] repLenLow, repLenMid;
    private readonly int[] repLenHigh;

    private readonly int[] litProbs;

    private Lzma1Encoder(int[] dataBytes, int lc, int lp, int pb, long? dictSize)
    {
        this.data = dataBytes;
        this.n = dataBytes.Length;
        this.lc = lc;
        this.lp = lp;
        this.pb = pb;
        this.dictSize = dictSize ?? long.MaxValue;

        int pbn = 1 << pb;

        isMatch = new int[NumStates][];
        isRep0Long = new int[NumStates][];
        for (int s = 0; s < NumStates; s++)
        {
            isMatch[s] = Fill(pbn, ProbInit);
            isRep0Long[s] = Fill(pbn, ProbInit);
        }
        isRep = Fill(NumStates, ProbInit);
        isRepG0 = Fill(NumStates, ProbInit);
        isRepG1 = Fill(NumStates, ProbInit);
        isRepG2 = Fill(NumStates, ProbInit);

        posSlot = new int[NumLenToPosStates][];
        for (int i = 0; i < NumLenToPosStates; i++)
        {
            posSlot[i] = Fill(64, ProbInit);
        }
        alignProbs = Fill(16, ProbInit);
        posDecoders = Fill(1 + NumFullDistances - EndPosModelIndex, ProbInit);

        lenChoice = Fill(2, ProbInit);
        lenLow = new int[pbn][];
        lenMid = new int[pbn][];
        for (int i = 0; i < pbn; i++)
        {
            lenLow[i] = Fill(8, ProbInit);
            lenMid[i] = Fill(8, ProbInit);
        }
        lenHigh = Fill(256, ProbInit);

        repLenChoice = Fill(2, ProbInit);
        repLenLow = new int[pbn][];
        repLenMid = new int[pbn][];
        for (int i = 0; i < pbn; i++)
        {
            repLenLow[i] = Fill(8, ProbInit);
            repLenMid[i] = Fill(8, ProbInit);
        }
        repLenHigh = Fill(256, ProbInit);

        int numLitStates = 1 << (lc + lp);
        litProbs = Fill(0x300 * numLitStates, ProbInit);
    }

    private static int[] Fill(int count, int value)
    {
        var a = new int[count];
        for (int i = 0; i < count; i++) a[i] = value;
        return a;
    }

    // ---------------- range coder ----------------

    private void ShiftLow()
    {
        if (low < 0xFF000000u || low > 0xFFFFFFFFu)
        {
            byte temp = cache;
            uint carry = (uint)(low >> 32);
            do
            {
                outBytes.Add((byte)((temp + carry) & 0xFF));
                temp = 0xFF;
                cacheSize--;
            } while (cacheSize != 0);
            cache = (byte)((low >> 24) & 0xFF);
        }
        cacheSize++;
        low = (uint)low << 8; // truncate to 32 bits first, then shift
    }

    private void EncodeBit(int[] probs, int idx, int bit)
    {
        int v = probs[idx];
        uint bound = (range >> NumBitModelTotalBits) * (uint)v;
        if (bit == 0)
        {
            range = bound;
            v += (BitModelTotal - v) >> NumMoveBits;
        }
        else
        {
            low += bound;
            range -= bound;
            v -= v >> NumMoveBits;
        }
        probs[idx] = v;
        if (range < TopValue)
        {
            range <<= 8;
            ShiftLow();
        }
    }

    private void EncodeDirectBits(uint v, int numBits)
    {
        for (int i = numBits - 1; i >= 0; i--)
        {
            range >>= 1;
            uint bit = (v >> i) & 1;
            if (bit != 0)
            {
                low += range;
            }
            if (range < TopValue)
            {
                range <<= 8;
                ShiftLow();
            }
        }
    }

    private void FlushRange()
    {
        for (int i = 0; i < 5; i++) ShiftLow();
    }

    private void EncodeBitTree(int[] probs, int baseIdx, int numBits, int symbol)
    {
        int m = 1;
        for (int i = numBits - 1; i >= 0; i--)
        {
            int bit = (symbol >> i) & 1;
            EncodeBit(probs, baseIdx + m, bit);
            m = (m << 1) | bit;
        }
    }

    private void EncodeBitTreeReverse(int[] probs, int baseIdx, int numBits, int symbol)
    {
        int m = 1;
        int sym = symbol;
        for (int i = 0; i < numBits; i++)
        {
            int bit = sym & 1;
            sym >>= 1;
            EncodeBit(probs, baseIdx + m, bit);
            m = (m << 1) | bit;
        }
    }

    // ---------------- length coder ----------------

    private void EncodeLen(int[] choiceArr, int[][] lowArr, int[][] midArr, int[] highArr, int posState, int sym)
    {
        if (sym < 8)
        {
            EncodeBit(choiceArr, 0, 0);
            EncodeBitTree(lowArr[posState], 0, 3, sym);
        }
        else
        {
            EncodeBit(choiceArr, 0, 1);
            sym -= 8;
            if (sym < 8)
            {
                EncodeBit(choiceArr, 1, 0);
                EncodeBitTree(midArr[posState], 0, 3, sym);
            }
            else
            {
                EncodeBit(choiceArr, 1, 1);
                sym -= 8;
                EncodeBitTree(highArr, 0, 8, sym);
            }
        }
    }

    // ---------------- literal coder ----------------

    private int GetByteBefore(int dist)
    {
        return data[pos - dist];
    }

    private void EncodeLiteral(int byteVal)
    {
        int totalPos = pos;
        int prevByte = totalPos > 0 ? data[totalPos - 1] : 0;
        int litState = ((totalPos & ((1 << lp) - 1)) << lc) + (prevByte >> (8 - lc));
        int baseIdx = 0x300 * litState;
        int symbol = 1;
        int b = byteVal;

        if (state >= 7)
        {
            int matchByte = GetByteBefore(rep0 + 1);
            while (symbol < 0x100)
            {
                int matchBit = (matchByte >> 7) & 1;
                matchByte = (matchByte << 1) & 0xFF;
                int bit = (b >> 7) & 1;
                b = (b << 1) & 0xFF;
                int idx = baseIdx + ((1 + matchBit) << 8) + symbol;
                EncodeBit(litProbs, idx, bit);
                symbol = (symbol << 1) | bit;
                if (matchBit != bit) break;
            }
        }
        while (symbol < 0x100)
        {
            int bit = (b >> 7) & 1;
            b = (b << 1) & 0xFF;
            EncodeBit(litProbs, baseIdx + symbol, bit);
            symbol = (symbol << 1) | bit;
        }
    }

    // ---------------- distance coder ----------------

    private static int GetPosSlot(long dist)
    {
        if (dist < 4) return (int)dist;
        int nbits = BitLength(dist) - 1;
        return (int)((nbits << 1) | ((dist >> (nbits - 1)) & 1));
    }

    private static int BitLength(long x)
    {
        int n = 0;
        while (x > 0) { x >>= 1; n++; }
        return n;
    }

    private void EncodeDistance(int length0, long dist)
    {
        int lenState = Math.Min(length0, NumLenToPosStates - 1);
        int slot = GetPosSlot(dist);
        EncodeBitTree(posSlot[lenState], 0, 6, slot);
        if (slot >= 4)
        {
            int numDirectBits = (slot >> 1) - 1;
            long baseDist = (long)(2 | (slot & 1)) << numDirectBits;
            if (slot < EndPosModelIndex)
            {
                EncodeBitTreeReverse(posDecoders, (int)(baseDist - slot), numDirectBits, (int)(dist - baseDist));
            }
            else
            {
                EncodeDirectBits((uint)(dist >> NumAlignBits), numDirectBits - NumAlignBits);
                EncodeBitTreeReverse(alignProbs, 0, NumAlignBits, (int)(dist & 0xF));
            }
        }
    }

    // ---------------- top-level symbol emitters ----------------

    private void EmitLiteral(int byteVal)
    {
        int posState = pos & ((1 << pb) - 1);
        EncodeBit(isMatch[state], posState, 0);
        EncodeLiteral(byteVal);
        pos++;
        state = StateLit(state);
    }

    private void EmitShortRep()
    {
        int posState = pos & ((1 << pb) - 1);
        EncodeBit(isMatch[state], posState, 1);
        EncodeBit(isRep, state, 1);
        EncodeBit(isRepG0, state, 0);
        EncodeBit(isRep0Long[state], posState, 0);
        pos++;
        state = StateShortRep(state);
    }

    private void EmitRepMatch(int repIndex, int length)
    {
        int posState = pos & ((1 << pb) - 1);
        EncodeBit(isMatch[state], posState, 1);
        EncodeBit(isRep, state, 1);

        if (repIndex == 0)
        {
            EncodeBit(isRepG0, state, 0);
            EncodeBit(isRep0Long[state], posState, 1);
        }
        else
        {
            EncodeBit(isRepG0, state, 1);
            if (repIndex == 1)
            {
                EncodeBit(isRepG1, state, 0);
                int dist = rep1;
                rep1 = rep0;
                rep0 = dist;
            }
            else
            {
                EncodeBit(isRepG1, state, 1);
                if (repIndex == 2)
                {
                    EncodeBit(isRepG2, state, 0);
                    int dist = rep2;
                    rep2 = rep1;
                    rep1 = rep0;
                    rep0 = dist;
                }
                else
                {
                    EncodeBit(isRepG2, state, 1);
                    int dist = rep3;
                    rep3 = rep2;
                    rep2 = rep1;
                    rep1 = rep0;
                    rep0 = dist;
                }
            }
        }

        int length0 = length - MatchMinLen;
        EncodeLen(repLenChoice, repLenLow, repLenMid, repLenHigh, posState, length0);
        state = StateRep(state);
        pos += length;
    }

    private void EmitMatch(long dist, int length)
    {
        int posState = pos & ((1 << pb) - 1);
        EncodeBit(isMatch[state], posState, 1);
        EncodeBit(isRep, state, 0);
        state = StateMatch(state);

        rep3 = rep2;
        rep2 = rep1;
        rep1 = rep0;
        rep0 = (int)(dist - 1); // model works with 0-based distance

        int length0 = length - MatchMinLen;
        EncodeLen(lenChoice, lenLow, lenMid, lenHigh, posState, length0);
        EncodeDistance(length0, rep0);
        pos += length;
    }

    private void EmitEndMarker()
    {
        // A "match" whose decoded distance is 0xFFFFFFFF signals end-of-stream.
        // Required for raw headerless streams, which carry no size field.
        int posState = pos & ((1 << pb) - 1);
        EncodeBit(isMatch[state], posState, 1);
        EncodeBit(isRep, state, 0);
        state = StateMatch(state);
        int length0 = 0; // len = 2, value itself is irrelevant for the marker
        EncodeLen(lenChoice, lenLow, lenMid, lenHigh, posState, length0);
        EncodeDistance(length0, 0xFFFFFFFFL);
    }

    // ---------------- state transition helpers ----------------

    private static int StateLit(int s)
    {
        if (s < 4) return 0;
        if (s < 10) return s - 3;
        return s - 6;
    }

    private static int StateMatch(int s) => s < 7 ? 7 : 10;
    private static int StateRep(int s) => s < 7 ? 8 : 11;
    private static int StateShortRep(int s) => s < 7 ? 9 : 11;

    // ---------------- match finder ----------------

    private int HashAt(int p)
    {
        if (p + 2 < n) return (data[p] | (data[p + 1] << 8) | (data[p + 2] << 16)) & 0xFFFFF;
        if (p + 1 < n) return (data[p] | (data[p + 1] << 8)) & 0xFFFFF;
        return data[p] & 0xFFFFF;
    }

    private void FindMatch(Dictionary<int, int> hashTable, Dictionary<int, int> chain, int p, int maxLenCap, out int bestLen, out int bestDist, int maxChain = 64)
    {
        bestLen = 0;
        bestDist = 0;
        if (p + 2 > n) return;
        int h = HashAt(p);
        int cand = hashTable.TryGetValue(h, out var hv) ? hv : -1;
        int tries = 0;
        int limit = Math.Min(n - p, maxLenCap);
        while (cand != -1 && tries < maxChain)
        {
            tries++;
            int l = 0;
            while (l < limit && data[cand + l] == data[p + l]) l++;
            if (l > bestLen)
            {
                bestLen = l;
                bestDist = p - cand;
                if (l >= limit) break;
            }
            cand = chain.TryGetValue(cand, out var cv) ? cv : -1;
        }
    }

    private int RepLenAt(int p, int dist, int remaining)
    {
        if (dist + 1 > p) return 0;
        int l = 0;
        int cap = Math.Min(remaining, MaxLen);
        while (l < cap && data[p - dist - 1 + l] == data[p + l]) l++;
        return l;
    }

    // ---------------- main driver ----------------

    private void Run(bool appendEndMarker)
    {
        var hashTable = new Dictionary<int, int>();
        var chain = new Dictionary<int, int>();

        void Insert(int p)
        {
            if (p >= n) return;
            int h = HashAt(p);
            chain[p] = hashTable.TryGetValue(h, out var hv) ? hv : -1;
            hashTable[h] = p;
        }

        while (pos < n)
        {
            int curPos = pos;
            int remaining = n - curPos;

            int bestRepLen = 0, bestRepIdx = -1;
            int[] reps = { rep0, rep1, rep2, rep3 };
            for (int idx = 0; idx < 4; idx++)
            {
                int rl = RepLenAt(curPos, reps[idx], remaining);
                if (rl > bestRepLen) { bestRepLen = rl; bestRepIdx = idx; }
            }

            FindMatch(hashTable, chain, curPos, Math.Min(remaining, MaxLen), out int matchLen, out int matchDist);
            if (matchLen > 0 && matchDist > dictSize)
            {
                matchLen = 0;
                matchDist = 0;
            }

            bool useRep = bestRepIdx != -1 && bestRepLen >= 2 && (bestRepLen + 1 >= matchLen || matchLen < 2);
            bool useMatch = !useRep && matchLen >= 2;

            if (useRep)
            {
                if (bestRepIdx == 0 && bestRepLen == 1)
                {
                    EmitShortRep();
                    Insert(curPos);
                }
                else
                {
                    int length = bestRepLen;
                    for (int k = 0; k < length; k++) Insert(curPos + k);
                    EmitRepMatch(bestRepIdx, length);
                }
            }
            else if (useMatch)
            {
                bool doMatch = true;
                Insert(curPos);
                if (curPos + 1 < n)
                {
                    FindMatch(hashTable, chain, curPos + 1, Math.Min(n - curPos - 1, MaxLen), out int nxtLen, out int nxtDist);
                    if (nxtLen > 0 && nxtDist > dictSize) nxtLen = 0;
                    if (nxtLen > matchLen) doMatch = false;
                }
                if (doMatch)
                {
                    int length = matchLen;
                    for (int k = 1; k < length; k++) Insert(curPos + k);
                    EmitMatch(matchDist, length);
                }
                else
                {
                    EmitLiteral(data[curPos]);
                }
            }
            else
            {
                Insert(curPos);
                EmitLiteral(data[curPos]);
            }
        }

        if (appendEndMarker) EmitEndMarker();
        FlushRange();
    }

    // ---------------- public API ----------------

    private static int[] ToByteInts(byte[] data)
    {
        var arr = new int[data.Length];
        for (int i = 0; i < data.Length; i++) arr[i] = data[i];
        return arr;
    }

    /// <summary>
    /// Compress into a standard 13-byte-header .lzma stream.
    /// </summary>
    public static byte[] Compress(byte[] data, int lc = 3, int lp = 0, int pb = 2)
    {
        int n = data.Length;
        long dictSize = 1 << 12;
        while (dictSize < n) dictSize <<= 1;

        var enc = new Lzma1Encoder(ToByteInts(data), lc, lp, pb, dictSize);
        enc.Run(false);

        int propByte = (pb * 5 + lp) * 9 + lc;
        var result = new List<byte>();
        result.Add((byte)propByte);
        result.AddRange(PackUInt32LE((uint)dictSize));
        result.AddRange(PackUInt64LE((ulong)n));
        result.AddRange(enc.outBytes);
        return result.ToArray();
    }

    /// <summary>
    /// Compress into a headerless RAW LZMA1 stream, equivalent to:
    ///   xz --format=raw --lzma1=lc=3,lp=0,pb=2,dict=128KiB -c -
    ///
    /// The decoder must be told lc/lp/pb/dict out of band (exactly as in
    /// the xz command above) — nothing is stored in the stream itself.
    /// </summary>
    public static byte[] CompressRaw(byte[] data, int lc = 3, int lp = 0, int pb = 2, long dictSize = 131072)
    {
        var enc = new Lzma1Encoder(ToByteInts(data), lc, lp, pb, dictSize);
        enc.Run(true);
        return enc.outBytes.ToArray();
    }

    private static byte[] PackUInt32LE(uint v)
    {
        return new byte[] { (byte)(v & 0xFF), (byte)((v >> 8) & 0xFF), (byte)((v >> 16) & 0xFF), (byte)((v >> 24) & 0xFF) };
    }

    private static byte[] PackUInt64LE(ulong v)
    {
        var b = new byte[8];
        for (int i = 0; i < 8; i++) { b[i] = (byte)(v & 0xFF); v >>= 8; }
        return b;
    }
}
