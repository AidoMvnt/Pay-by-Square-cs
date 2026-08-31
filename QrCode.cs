using System;
using System.Text;

namespace QRBar
{
    /// <summary>
    /// QR Code generator — verbatim C# port of Project Nayuki's qrcodegen (MIT License).
    /// Reference: https://github.com/nayuki/QR-Code-generator/blob/master/c/qrcodegen.c
    /// Zero external dependencies. Implements versions 1-40, 4 ECC levels,
    /// numeric/alphanumeric/byte modes, Reed-Solomon ECC and automatic mask selection.
    /// </summary>
    public sealed class QrCode
    {
        public enum Ecc { Low, Medium, Quartile, High }

        private const int VERSION_MIN = 1, VERSION_MAX = 40;
        private const int MASK_AUTO = -1, MASK_MIN = 0, MASK_MAX = 7;
        private const int LENGTH_OVERFLOW = -1;
        private const int RS_DEGREE_MAX = 30;
        private const int MODE_NUMERIC = 1, MODE_ALPHANUMERIC = 2, MODE_BYTE = 4;
        private const int PENALTY_N1 = 3, PENALTY_N2 = 3, PENALTY_N3 = 40, PENALTY_N4 = 10;
        private const string ALPHANUMERIC_CHARSET = "0123456789ABCDEFGHIJKLMNOPQRSTUVWXYZ $%*+-./:";

        // ECC codewords per block [ecclevel][version] (index 0 = padding, set to -1)
        private static readonly int[,] ECC_CODEWORDS_PER_BLOCK =
        {
            // Version: 0,  1,  2,  3,  4,  5,  6,  7,  8,  9, 10, 11, 12, 13, 14, 15, 16, 17, 18, 19, 20, 21, 22, 23, 24, 25, 26, 27, 28, 29, 30, 31, 32, 33, 34, 35, 36, 37, 38, 39, 40
            {-1,  7, 10, 15, 20, 26, 18, 20, 24, 30, 18, 20, 24, 26, 30, 22, 24, 28, 30, 28, 28, 28, 28, 30, 30, 26, 28, 30, 30, 30, 30, 30, 30, 30, 30, 30, 30, 30, 30, 30, 30}, // Low
            {-1, 10, 16, 26, 18, 24, 16, 18, 22, 22, 26, 30, 22, 22, 24, 24, 28, 28, 26, 26, 26, 26, 28, 28, 28, 28, 28, 28, 28, 28, 28, 28, 28, 28, 28, 28, 28, 28, 28, 28, 28}, // Medium
            {-1, 13, 22, 18, 26, 18, 24, 18, 22, 20, 24, 28, 26, 24, 20, 30, 24, 28, 28, 26, 30, 28, 30, 30, 30, 30, 28, 30, 30, 30, 30, 30, 30, 30, 30, 30, 30, 30, 30, 30, 30}, // Quartile
            {-1, 17, 28, 22, 16, 22, 28, 26, 26, 24, 28, 24, 28, 22, 24, 24, 30, 28, 28, 26, 28, 30, 24, 30, 30, 30, 30, 30, 30, 30, 30, 30, 30, 30, 30, 30, 30, 30, 30, 30, 30}, // High
        };

        // Number of EC blocks [ecclevel][version]
        private static readonly int[,] NUM_ERROR_CORRECTION_BLOCKS =
        {
            {-1, 1, 1, 1, 1, 1, 2, 2, 2, 2, 4,  4,  4,  4,  4,  6,  6,  6,  6,  7,  8,  8,  9,  9, 10, 12, 12, 12, 13, 14, 15, 16, 17, 18, 19, 19, 20, 21, 22, 24, 25}, // Low
            {-1, 1, 1, 1, 2, 2, 4, 4, 4, 5, 5,  5,  8,  9,  9, 10, 10, 11, 13, 14, 16, 17, 17, 18, 20, 21, 23, 25, 26, 28, 29, 31, 33, 35, 37, 38, 40, 43, 45, 47, 49}, // Medium
            {-1, 1, 1, 2, 2, 4, 4, 6, 6, 8, 8,  8, 10, 12, 16, 12, 17, 16, 18, 21, 20, 23, 23, 25, 27, 29, 34, 34, 35, 38, 40, 43, 45, 48, 51, 53, 56, 59, 62, 65, 68}, // Quartile
            {-1, 1, 1, 2, 4, 4, 4, 5, 6, 8, 8, 11, 11, 16, 16, 18, 16, 19, 21, 25, 25, 25, 34, 30, 32, 35, 37, 40, 42, 45, 48, 51, 54, 57, 60, 63, 66, 70, 74, 77, 81}, // High
        };

        public int Version { get; }   // in the range 1 to 40
        public int Size { get; }      // width and height, in the range 21 to 177
        public Ecc Ecl { get; }       // the ECC level of this QR code (final, after boosting)
        public int Mask { get; }      // the mask pattern ID in the range 0 to 7 (inclusive)
        public int Width => Size;

        private readonly bool[] _dark;      // color of modules; row-major, index = y * Size + x
        private readonly bool[] _isFunc;    // function-module map

        private QrCode(int version, Ecc ecl, int mask, bool[] dark, bool[] isFunc)
        {
            Version = version;
            Size = version * 4 + 17;
            Ecl = ecl;
            Mask = mask;
            _dark = dark;
            _isFunc = isFunc;
        }

        /// <summary>Gets the color of the module (pixel) at the given coordinates, which is black iff true.</summary>
        public bool GetModule(int x, int y)
            => (uint)x < (uint)Size && (uint)y < (uint)Size && _dark[y * Size + x];

        // ========================================
        // High-level QR Code encoding functions
        // ========================================

        /// <summary>
        /// Encodes the given text to a new QR Code instance.
        /// The result finds the smallest version number (1 to 40) that can contain all the characters.
        /// </summary>
        public static QrCode EncodeText(string text, Ecc ecl,
                                        int minVersion = VERSION_MIN, int maxVersion = VERSION_MAX,
                                        int mask = MASK_AUTO, bool boostEcl = true)
        {
            if (text == null) text = "";
            if (minVersion < VERSION_MIN || minVersion > maxVersion)
                throw new ArgumentException("Invalid value for minVersion");
            if (maxVersion < VERSION_MIN || maxVersion > VERSION_MAX)
                throw new ArgumentException("Invalid value for maxVersion");
            if ((int)ecl < 0 || (int)ecl > 3)
                throw new ArgumentException("Invalid ECC level");
            if (mask < MASK_AUTO || mask > MASK_MAX)
                throw new ArgumentException("Invalid mask");

            // Choose the most compact segment mode
            Segment seg = null;
            if (IsNumeric(text))
                seg = MakeNumeric(text);
            else if (IsAlphanumeric(text))
                seg = MakeAlphanumeric(text);
            else
                seg = MakeBytes(Encoding.UTF8.GetBytes(text));

            // Find the minimal version number to use
            int version, dataUsedBits;
            for (version = minVersion; ; version++)
            {
                int capBits = GetNumDataCodewords(version, (int)ecl) * 8;
                dataUsedBits = seg == null ? 0 : GetTotalBits(seg, version);
                if (dataUsedBits != LENGTH_OVERFLOW && dataUsedBits <= capBits)
                    break;
                if (version >= maxVersion)
                    throw new ArgumentException("Data too long to fit in a QR Code");
            }

            // Increase the error correction level while the data still fits
            Ecc finalEcl = ecl;
            for (int i = (int)Ecc.Medium; i <= (int)Ecc.High; i++)
            {
                if (boostEcl && dataUsedBits <= GetNumDataCodewords(version, i) * 8)
                    finalEcl = (Ecc)i;
            }

            // Concatenate all segments to create the data bit string
            int dataCapacityBits = GetNumDataCodewords(version, (int)finalEcl) * 8;
            byte[] data = new byte[(dataCapacityBits + 7) / 8];
            int bitLen = 0;
            if (seg != null)
            {
                AppendBits((uint)seg.Mode, 4, data, ref bitLen);
                AppendBits((uint)seg.NumChars, NumCharCountBits(seg.Mode, version), data, ref bitLen);
                for (int j = 0; j < seg.BitLength; j++)
                {
                    int bit = (seg.Data[j >> 3] >> (7 - (j & 7))) & 1;
                    AppendBits((uint)bit, 1, data, ref bitLen);
                }
                if (bitLen != dataUsedBits)
                    throw new InvalidOperationException("Bit length mismatch");
            }

            // Add terminator and pad up to a byte if applicable
            int terminatorBits = dataCapacityBits - bitLen;
            if (terminatorBits > 4) terminatorBits = 4;
            AppendBits(0, terminatorBits, data, ref bitLen);
            AppendBits(0, (8 - bitLen % 8) % 8, data, ref bitLen);
            if (bitLen % 8 != 0) throw new InvalidOperationException("Not byte aligned");

            // Pad with alternating bytes until data capacity is reached
            for (int padByte = 0xEC; bitLen < dataCapacityBits; padByte ^= 0xEC ^ 0x11)
                AppendBits((uint)padByte, 8, data, ref bitLen);

            // Compute ECC, draw modules
            byte[] allCodewords = new byte[GetNumRawDataModules(version) / 8];
            AddEccAndInterleave(data, version, (int)finalEcl, allCodewords);

            int size = 4 * version + 17;
            bool[] dark = new bool[size * size];
            bool[] func = new bool[size * size];
            InitializeFunctionModules(version, dark, func, size);
            DrawCodewords(allCodewords, dark, size);
            DrawLightFunctionModules(version, dark, size);
            bool[] funcMask = new bool[size * size];
            InitializeFunctionModules(version, funcMask, null, size);

            // Do masking
            int chosenMask = mask;
            if (mask == MASK_AUTO)
            {
                long minPenalty = long.MaxValue;
                for (int i = 0; i < 8; i++)
                {
                    int msk = i;
                    ApplyMask(funcMask, dark, msk);
                    DrawFormatBits((int)finalEcl, msk, dark, size);
                    long penalty = GetPenaltyScore(dark, size);
                    if (penalty < minPenalty)
                    {
                        chosenMask = msk;
                        minPenalty = penalty;
                    }
                    ApplyMask(funcMask, dark, msk); // Undoes the mask due to XOR
                }
            }
            if (chosenMask < 0 || chosenMask > 7)
                throw new InvalidOperationException("Invalid mask");
            ApplyMask(funcMask, dark, chosenMask);        // Apply the final choice of mask
            DrawFormatBits((int)finalEcl, chosenMask, dark, size); // Overwrite old format bits

            return new QrCode(version, finalEcl, chosenMask, dark, null);
        }

        // ========================================
        // Bit buffer
        // ========================================

        // Appends the given number of low-order bits of the given value to the given byte buffer.
        // Requires 0 <= numBits <= 16 and val < 2^numBits.
        private static void AppendBits(uint val, int numBits, byte[] buffer, ref int bitLen)
        {
            if (numBits < 0 || numBits > 16 || val >> numBits != 0)
                throw new InvalidOperationException("Invalid bit append");
            for (int i = numBits - 1; i >= 0; i--, bitLen++)
                buffer[bitLen >> 3] |= (byte)((((uint)(val >> i)) & 1) << (7 - (bitLen & 7)));
        }

        // ========================================
        // Error correction code generation functions
        // ========================================

        // Appends ECC codewords to each block of the given data, then interleaves
        // bytes from the blocks and stores them in result.
        private static void AddEccAndInterleave(byte[] data, int version, int ecl, byte[] result)
        {
            int numBlocks = NUM_ERROR_CORRECTION_BLOCKS[ecl, version];
            int blockEccLen = ECC_CODEWORDS_PER_BLOCK[ecl, version];
            int rawCodewords = GetNumRawDataModules(version) / 8;
            int dataLen = GetNumDataCodewords(version, ecl);
            int numShortBlocks = numBlocks - rawCodewords % numBlocks;
            int shortBlockDataLen = rawCodewords / numBlocks - blockEccLen;

            // Split data into blocks, calculate ECC, and interleave the bytes into a single sequence
            byte[] rsdiv = new byte[RS_DEGREE_MAX];
            ReedSolomonComputeDivisor(blockEccLen, rsdiv);
            int datPos = 0;
            for (int i = 0; i < numBlocks; i++)
            {
                int datLen = shortBlockDataLen + (i < numShortBlocks ? 0 : 1);
                byte[] ecc = new byte[blockEccLen];
                ReedSolomonComputeRemainder(data, datPos, datLen, rsdiv, blockEccLen, ecc);
                for (int j = 0, k = i; j < datLen; j++, k += numBlocks) // Copy data
                {
                    if (j == shortBlockDataLen)
                        k -= numShortBlocks;
                    result[k] = data[datPos + j];
                }
                for (int j = 0, k = dataLen + i; j < blockEccLen; j++, k += numBlocks) // Copy ECC
                    result[k] = ecc[j];
                datPos += datLen;
            }
        }

        // Returns the number of 8-bit codewords that can be used for storing data (not ECC).
        private static int GetNumDataCodewords(int version, int ecl)
        {
            return GetNumRawDataModules(version) / 8
                - ECC_CODEWORDS_PER_BLOCK[ecl, version]
                * NUM_ERROR_CORRECTION_BLOCKS[ecl, version];
        }

        // Returns the number of data bits that can be stored in a QR Code of the given version,
        // after all function modules are excluded. Includes remainder bits.
        private static int GetNumRawDataModules(int ver)
        {
            if (ver < VERSION_MIN || ver > VERSION_MAX)
                throw new ArgumentException("Invalid QR Code version");
            int result = (16 * ver + 128) * ver + 64;
            if (ver >= 2)
            {
                int numAlign = ver / 7 + 2;
                result -= (25 * numAlign - 10) * numAlign - 55;
                if (ver >= 7)
                    result -= 36;
            }
            if (result < 208 || result > 29648)
                throw new InvalidOperationException("Assertion failed");
            return result;
        }

        // ========================================
        // Reed-Solomon ECC generator functions
        // ========================================

        // Computes a Reed-Solomon ECC generator polynomial for the given degree, in result[0 : degree].
        private static void ReedSolomonComputeDivisor(int degree, byte[] result)
        {
            if (degree < 1 || degree > RS_DEGREE_MAX)
                throw new ArgumentException("Invalid degree");
            // Polynomial coefficients stored from highest to lowest power (leading 1 term excluded).
            Array.Clear(result, 0, degree);
            result[degree - 1] = 1; // Start with the monomial x^0

            // Compute the product (x - r^0) * (x - r^1) * ... * (x - r^{degree-1}),
            // dropping the highest monomial term. r = 0x02, a generator of GF(2^8/0x11D).
            byte root = 1;
            for (int i = 0; i < degree; i++)
            {
                for (int j = 0; j < degree; j++)
                {
                    result[j] = (byte)ReedSolomonMultiply(result[j], root);
                    if (j + 1 < degree)
                        result[j] ^= result[j + 1];
                }
                root = (byte)ReedSolomonMultiply(root, 0x02);
            }
        }

        // Computes the Reed-Solomon ECC codeword (remainder of data / generator).
        private static void ReedSolomonComputeRemainder(byte[] data, int dataPos, int dataLen,
                byte[] generator, int degree, byte[] result)
        {
            if (degree < 1 || degree > RS_DEGREE_MAX)
                throw new ArgumentException("Invalid degree");
            Array.Clear(result, 0, degree);
            for (int i = 0; i < dataLen; i++) // Polynomial division
            {
                byte factor = (byte)(data[dataPos + i] ^ result[0]);
                Array.Copy(result, 1, result, 0, degree - 1);
                result[degree - 1] = 0;
                for (int j = 0; j < degree; j++)
                    result[j] ^= (byte)ReedSolomonMultiply(generator[j], factor);
            }
        }

        // Product of two GF(2^8/0x11D) elements (Russian peasant multiplication).
        private static int ReedSolomonMultiply(byte x, byte y)
        {
            int z = 0;
            for (int i = 7; i >= 0; i--)
            {
                z = (z << 1) ^ ((z >> 7) * 0x11D);
                z ^= ((y >> i) & 1) * (x & 0xFF);
            }
            return z & 0xFF;
        }

        // ========================================
        // Drawing function modules
        // ========================================

        private static void InitializeFunctionModules(int version, bool[] dark, bool[] func, int qrsize)
        {
            // Fill horizontal and vertical timing patterns
            FillRectangle(dark, func, qrsize, 6, 0, 1, qrsize);
            FillRectangle(dark, func, qrsize, 0, 6, qrsize, 1);

            // Fill 3 finder patterns and format bit areas
            FillRectangle(dark, func, qrsize, 0, 0, 9, 9);
            FillRectangle(dark, func, qrsize, qrsize - 8, 0, 8, 9);
            FillRectangle(dark, func, qrsize, 0, qrsize - 8, 9, 8);

            // Fill alignment patterns
            int[] aline = GetAlignmentPatternPositions(version);
            for (int i = 0; i < aline.Length; i++)
            {
                for (int j = 0; j < aline.Length; j++)
                {
                    if (!((i == 0 && j == 0) || (i == 0 && j == aline.Length - 1) ||
                          (i == aline.Length - 1 && j == 0)))
                        FillRectangle(dark, func, qrsize, aline[i] - 2, aline[j] - 2, 5, 5);
                }
            }

            // Fill version blocks
            if (version >= 7)
            {
                FillRectangle(dark, func, qrsize, qrsize - 11, 0, 3, 6);
                FillRectangle(dark, func, qrsize, 0, qrsize - 11, 6, 3);
            }
        }

        // Sets every module in [left : left+width] x [top : top+height] to dark.
        private static void FillRectangle(bool[] dark, bool[] func, int qrsize, int left, int top, int width, int height)
        {
            for (int dy = 0; dy < height; dy++)
                for (int dx = 0; dx < width; dx++)
                {
                    dark[(top + dy) * qrsize + (left + dx)] = true;
                    if (func != null) func[(top + dy) * qrsize + (left + dx)] = true;
                }
        }

        private static void DrawLightFunctionModules(int version, bool[] dark, int qrsize)
        {
            // Timing patterns
            for (int i = 7; i < qrsize - 7; i += 2)
            {
                SetModule(dark, qrsize, 6, i, false);
                SetModule(dark, qrsize, i, 6, false);
            }

            // 3 finder patterns
            for (int dy = -4; dy <= 4; dy++)
            {
                for (int dx = -4; dx <= 4; dx++)
                {
                    int dist = Math.Abs(dx);
                    if (Math.Abs(dy) > dist) dist = Math.Abs(dy);
                    if (dist == 2 || dist == 4)
                    {
                        SetModuleUnbounded(dark, qrsize, 3 + dx, 3 + dy, false);
                        SetModuleUnbounded(dark, qrsize, qrsize - 4 + dx, 3 + dy, false);
                        SetModuleUnbounded(dark, qrsize, 3 + dx, qrsize - 4 + dy, false);
                    }
                }
            }

            // Alignment patterns
            int[] aline = GetAlignmentPatternPositions(version);
            for (int i = 0; i < aline.Length; i++)
            {
                for (int j = 0; j < aline.Length; j++)
                {
                    if ((i == 0 && j == 0) || (i == 0 && j == aline.Length - 1) ||
                        (i == aline.Length - 1 && j == 0))
                        continue;
                    for (int dy = -1; dy <= 1; dy++)
                        for (int dx = -1; dx <= 1; dx++)
                            SetModule(dark, qrsize, aline[i] + dx, aline[j] + dy, dx == 0 && dy == 0);
                }
            }

            // Version blocks
            if (version >= 7)
            {
                int rem = version;
                for (int i = 0; i < 12; i++)
                    rem = (rem << 1) ^ ((rem >> 11) * 0x1F25);
                long bits = ((long)version << 12) | rem;
                for (int i = 0; i < 6; i++)
                {
                    for (int j = 0; j < 3; j++)
                    {
                        int k = qrsize - 11 + j;
                        SetModule(dark, qrsize, k, i, (bits & 1) != 0);
                        SetModule(dark, qrsize, i, k, (bits & 1) != 0);
                        bits >>= 1;
                    }
                }
            }
        }

        private static void DrawFormatBits(int ecl, int mask, bool[] dark, int qrsize)
        {
            // Calculate ECC and pack bits
            int[] table = { 1, 0, 3, 2 };
            int data = table[ecl] << 3 | mask;
            int rem = data;
            for (int i = 0; i < 10; i++)
                rem = (rem << 1) ^ ((rem >> 9) * 0x537);
            int bits = (data << 10 | rem) ^ 0x5412;

            // First copy
            for (int i = 0; i <= 5; i++)
                SetModule(dark, qrsize, 8, i, GetBit(bits, i));
            SetModule(dark, qrsize, 8, 7, GetBit(bits, 6));
            SetModule(dark, qrsize, 8, 8, GetBit(bits, 7));
            SetModule(dark, qrsize, 7, 8, GetBit(bits, 8));
            for (int i = 9; i < 15; i++)
                SetModule(dark, qrsize, 14 - i, 8, GetBit(bits, i));

            // Second copy
            for (int i = 0; i < 8; i++)
                SetModule(dark, qrsize, qrsize - 1 - i, 8, GetBit(bits, i));
            for (int i = 8; i < 15; i++)
                SetModule(dark, qrsize, 8, qrsize - 15 + i, GetBit(bits, i));
            SetModule(dark, qrsize, 8, qrsize - 8, true); // Always dark
        }

        private static bool GetBit(int x, int i) => ((x >> i) & 1) != 0;

        private static bool[] DarkOf(bool[] dark, int qrsize) => dark;

        private static void SetModule(bool[] dark, int qrsize, int x, int y, bool isDark)
            => dark[y * qrsize + x] = isDark;

        private static void SetModuleUnbounded(bool[] dark, int qrsize, int x, int y, bool isDark)
        {
            if (0 <= x && x < qrsize && 0 <= y && y < qrsize)
                dark[y * qrsize + x] = isDark;
        }

        // Ascending list of alignment pattern positions for a version (0 for v1).
        private static int[] GetAlignmentPatternPositions(int version)
        {
            if (version == 1)
                return new int[0];
            int numAlign = version / 7 + 2;
            int step = (version * 8 + numAlign * 3 + 5) / (numAlign * 4 - 4) * 2;
            int[] result = new int[numAlign];
            for (int i = numAlign - 1, pos = version * 4 + 10; i >= 1; i--, pos -= step)
                result[i] = pos;
            result[0] = 6;
            return result;
        }

        // ========================================
        // Drawing data modules and masking
        // ========================================

        private static void DrawCodewords(byte[] data, bool[] dark, int qrsize)
        {
            int i = 0; // Bit index into data
            for (int right = qrsize - 1; right >= 1; right -= 2)
            {
                if (right == 6) right = 5;
                for (int vert = 0; vert < qrsize; vert++)
                {
                    for (int j = 0; j < 2; j++)
                    {
                        int x = right - j;
                        bool upward = ((right + 1) & 2) == 0;
                        int y = upward ? qrsize - 1 - vert : vert;
                        if (!dark[y * qrsize + x] && i < data.Length * 8)
                        {
                            bool isDark = GetBit(data[i >> 3], 7 - (i & 7));
                            dark[y * qrsize + x] = isDark;
                            i++;
                        }
                    }
                }
            }
            if (i != data.Length * 8)
                throw new InvalidOperationException("Assertion i == dataLen * 8 failed");
        }

        private static void ApplyMask(bool[] func, bool[] dark, int mask)
        {
            int qrsize = (int)Math.Sqrt(dark.Length);
            for (int y = 0; y < qrsize; y++)
            {
                for (int x = 0; x < qrsize; x++)
                {
                    if (func[y * qrsize + x])
                        continue;
                    bool invert;
                    switch (mask)
                    {
                        case 0:  invert = (x + y) % 2 == 0;                    break;
                        case 1:  invert = y % 2 == 0;                          break;
                        case 2:  invert = x % 3 == 0;                          break;
                        case 3:  invert = (x + y) % 3 == 0;                    break;
                        case 4:  invert = (x / 3 + y / 2) % 2 == 0;            break;
                        case 5:  invert = x * y % 2 + x * y % 3 == 0;          break;
                        case 6:  invert = (x * y % 2 + x * y % 3) % 2 == 0;    break;
                        case 7:  invert = ((x + y) % 2 + x * y % 3) % 2 == 0;  break;
                        default: throw new InvalidOperationException("Invalid mask");
                    }
                    bool val = dark[y * qrsize + x];
                    dark[y * qrsize + x] = val ^ invert;
                }
            }
        }

        private static long GetPenaltyScore(bool[] dark, int qrsize)
        {
            long result = 0;

            for (int y = 0; y < qrsize; y++)
            {
                bool runColor = false;
                int runX = 0;
                int[] runHistory = new int[7];
                for (int x = 0; x < qrsize; x++)
                {
                    if (dark[y * qrsize + x] == runColor)
                    {
                        runX++;
                        if (runX == 5) result += PENALTY_N1;
                        else if (runX > 5) result++;
                    }
                    else
                    {
                        FinderPenaltyAddHistory(runX, runHistory, qrsize);
                        if (!runColor)
                            result += FinderPenaltyCountPatterns(runHistory, qrsize) * (long)PENALTY_N3;
                        runColor = dark[y * qrsize + x];
                        runX = 1;
                    }
                }
                result += FinderPenaltyTerminateAndCount(runColor, runX, runHistory, qrsize) * (long)PENALTY_N3;
            }
            for (int x = 0; x < qrsize; x++)
            {
                bool runColor = false;
                int runY = 0;
                int[] runHistory = new int[7];
                for (int y = 0; y < qrsize; y++)
                {
                    if (dark[y * qrsize + x] == runColor)
                    {
                        runY++;
                        if (runY == 5) result += PENALTY_N1;
                        else if (runY > 5) result++;
                    }
                    else
                    {
                        FinderPenaltyAddHistory(runY, runHistory, qrsize);
                        if (!runColor)
                            result += FinderPenaltyCountPatterns(runHistory, qrsize) * (long)PENALTY_N3;
                        runColor = dark[y * qrsize + x];
                        runY = 1;
                    }
                }
                result += FinderPenaltyTerminateAndCount(runColor, runY, runHistory, qrsize) * (long)PENALTY_N3;
            }

            // 2x2 blocks of same color
            for (int y = 0; y < qrsize - 1; y++)
            {
                for (int x = 0; x < qrsize - 1; x++)
                {
                    bool color = dark[y * qrsize + x];
                    if (color == dark[y * qrsize + x + 1] &&
                        color == dark[(y + 1) * qrsize + x] &&
                        color == dark[(y + 1) * qrsize + x + 1])
                        result += PENALTY_N2;
                }
            }

            // Dark/light balance
            int darkCount = 0;
            for (int i = 0; i < dark.Length; i++)
                if (dark[i]) darkCount++;
            int total = qrsize * qrsize;
            int k = (int)((Math.Abs((long)darkCount * 20 - (long)total * 10) + total - 1) / total) - 1;
            if (k < 0 || k > 9) throw new InvalidOperationException("Assertion failed");
            result += k * (long)PENALTY_N4;
            return result;
        }

        private static int FinderPenaltyCountPatterns(int[] runHistory, int qrsize)
        {
            int n = runHistory[1];
            if (n > qrsize * 3) throw new InvalidOperationException("Assertion failed");
            bool core = n > 0 && runHistory[2] == n && runHistory[3] == n * 3 && runHistory[4] == n && runHistory[5] == n;
            return (int)(
                (core && runHistory[0] >= n * 4 && runHistory[6] >= n ? 1 : 0) +
                (core && runHistory[6] >= n * 4 && runHistory[0] >= n ? 1 : 0));
        }

        private static int FinderPenaltyTerminateAndCount(bool currentRunColor, int currentRunLength, int[] runHistory, int qrsize)
        {
            if (currentRunColor)
            {
                FinderPenaltyAddHistory(currentRunLength, runHistory, qrsize);
                currentRunLength = 0;
            }
            currentRunLength += qrsize;
            FinderPenaltyAddHistory(currentRunLength, runHistory, qrsize);
            return FinderPenaltyCountPatterns(runHistory, qrsize);
        }

        private static void FinderPenaltyAddHistory(int currentRunLength, int[] runHistory, int qrsize)
        {
            if (runHistory[0] == 0)
                currentRunLength += qrsize;
            for (int i = 6; i > 0; i--)
                runHistory[i] = runHistory[i - 1];
            runHistory[0] = currentRunLength;
        }

        // ========================================
        // Segment handling
        // ========================================

        private sealed class Segment
        {
            public int Mode;
            public int BitLength;
            public int NumChars;
            public byte[] Data;
        }

        private static bool IsNumeric(string text)
        {
            if (text.Length == 0) return true;
            foreach (char c in text)
                if (c < '0' || c > '9') return false;
            return true;
        }

        private static bool IsAlphanumeric(string text)
        {
            foreach (char c in text)
                if (ALPHANUMERIC_CHARSET.IndexOf(c) == -1) return false;
            return true;
        }

        // Returns the number of data bits needed for a segment, or LENGTH_OVERFLOW.
        private static int CalcSegmentBitLength(int mode, int numChars)
        {
            if (numChars > 0xFFFF) return LENGTH_OVERFLOW;
            long result = numChars;
            if (mode == MODE_NUMERIC) result = (result * 10 + 2) / 3;
            else if (mode == MODE_ALPHANUMERIC) result = (result * 11 + 1) / 2;
            else if (mode == MODE_BYTE) result *= 8;
            else return LENGTH_OVERFLOW;
            if (result > 0xFFFF) return LENGTH_OVERFLOW;
            return (int)result;
        }

        private static Segment MakeBytes(byte[] bytes)
        {
            int bitLength = CalcSegmentBitLength(MODE_BYTE, bytes.Length);
            if (bitLength == LENGTH_OVERFLOW) throw new ArgumentException("Data too long");
            return new Segment
            {
                Mode = MODE_BYTE,
                BitLength = bitLength,
                NumChars = bytes.Length,
                Data = bytes,
            };
        }

        private static Segment MakeNumeric(string digits)
        {
            int len = digits.Length;
            int bitLen = CalcSegmentBitLength(MODE_NUMERIC, len);
            if (bitLen == LENGTH_OVERFLOW) throw new ArgumentException("Data too long");
            byte[] buf = new byte[(bitLen + 7) / 8];
            int bitLength = 0;
            uint accumData = 0;
            int accumCount = 0;
            for (int i = 0; i < len; i++)
            {
                char c = digits[i];
                accumData = accumData * 10 + (uint)(c - '0');
                accumCount++;
                if (accumCount == 3)
                {
                    AppendBits(accumData, 10, buf, ref bitLength);
                    accumData = 0;
                    accumCount = 0;
                }
            }
            if (accumCount > 0) // 1 or 2 digits remaining
                AppendBits(accumData, accumCount * 3 + 1, buf, ref bitLength);
            if (bitLength != bitLen) throw new InvalidOperationException("Assertion failed");
            return new Segment { Mode = MODE_NUMERIC, NumChars = len, BitLength = bitLen, Data = buf };
        }

        private static Segment MakeAlphanumeric(string text)
        {
            int len = text.Length;
            int bitLen = CalcSegmentBitLength(MODE_ALPHANUMERIC, len);
            if (bitLen == LENGTH_OVERFLOW) throw new ArgumentException("Data too long");
            byte[] buf = new byte[(bitLen + 7) / 8];
            int bitLength = 0;
            uint accumData = 0;
            int accumCount = 0;
            for (int i = 0; i < len; i++)
            {
                int idx = ALPHANUMERIC_CHARSET.IndexOf(text[i]);
                accumData = accumData * 45 + (uint)idx;
                accumCount++;
                if (accumCount == 2)
                {
                    AppendBits(accumData, 11, buf, ref bitLength);
                    accumData = 0;
                    accumCount = 0;
                }
            }
            if (accumCount > 0)
                AppendBits(accumData, 6, buf, ref bitLength);
            if (bitLength != bitLen) throw new InvalidOperationException("Assertion failed");
            return new Segment { Mode = MODE_ALPHANUMERIC, NumChars = len, BitLength = bitLen, Data = buf };
        }

        private static int GetTotalBits(Segment seg, int version)
        {
            int ccbits = NumCharCountBits(seg.Mode, version);
            if (seg.NumChars >= (1L << ccbits))
                return LENGTH_OVERFLOW;
            long result = 4L + ccbits + seg.BitLength;
            if (result > 0xFFFF) return LENGTH_OVERFLOW;
            return (int)result;
        }

        private static int NumCharCountBits(int mode, int version)
        {
            int i = (version + 7) / 17;
            switch (mode)
            {
                case MODE_NUMERIC:      return new int[] { 10, 12, 14 }[i];
                case MODE_ALPHANUMERIC: return new int[] { 9, 11, 13 }[i];
                case MODE_BYTE:         return new int[] { 8, 16, 16 }[i];
                default: throw new InvalidOperationException("Invalid mode");
            }
        }
    }
}
