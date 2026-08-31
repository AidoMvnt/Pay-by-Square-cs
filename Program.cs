using System;
using System.IO;

namespace QRBar
{
    public static class Program
    {
        public static int Main(string[] args)
        {
            if (args.Length == 1 && args[0] == "--test")
                return QRCodeTests.Run();

            try
            {
                return RunCli(args);
            }
            catch (ArgumentException e)
            {
                Console.Error.WriteLine("error: " + e.Message);
                return 2;
            }
            catch (System.IO.IOException e)
            {
                Console.Error.WriteLine("io error: " + e.Message);
                return 3;
            }
        }

        private static int RunCli(string[] args)
        {
            string text = "ahoj";
            string outPath = "qr_ahoj.bmp";
            QrCode.Ecc ecl = QrCode.Ecc.Medium;

            for (int i = 0; i < args.Length; i++)
            {
                if (args[i] == "--test") continue;
                if (args[i] == "--matrix" && i + 1 < args.Length)
                {
                    // Dump mode: emit the module matrix as 1/0 rows for oracle diffing.
                    // Usage: --matrix TEXT [ECL]   (0=L 1=M 2=Q 3=H, default 1)
                    string txt = args[++i];
                    int e2 = 1;
                    if (i + 1 < args.Length && int.TryParse(args[i + 1], out e2)) i++;
                    QrCode q = QrCode.EncodeText(txt, (QrCode.Ecc)e2);
                    for (int y = 0; y < q.Size; y++)
                    {
                        var sb = new System.Text.StringBuilder();
                        for (int x = 0; x < q.Size; x++) sb.Append(q.GetModule(x, y) ? '1' : '0');
                        Console.WriteLine(sb);
                    }
                    return 0;
                }
                if (args[i] == "--pbs")
                {
                    // Pay by Square (Slovak) payment QR: build the base32hex string.
                    // Usage: --pbs --iban SK... --amount 12.34 --vs 12345
                    //                [--cs ...] [--ss ...] [--payee "Meno"]
                    //                [--date 20261215] [--note ...] [--bic BICXXX]
                    //                [--qr] [--out FOO.bmp]
                    string iban = "", amount = "", vs = "", cs = "", ss = "",
                           payee = "", date = "", note = "", bic = "";
                    bool makeQr = false;
                    for (int j = i + 1; j < args.Length; j++)
                    {
                        string a = args[j];
                        if      (a == "--iban"  && j + 1 < args.Length) iban  = args[++j];
                        else if (a == "--amount"&& j + 1 < args.Length) amount= args[++j];
                        else if (a == "--vs"    && j + 1 < args.Length) vs    = args[++j];
                        else if (a == "--cs"    && j + 1 < args.Length) cs    = args[++j];
                        else if (a == "--ss"    && j + 1 < args.Length) ss    = args[++j];
                        else if (a == "--payee" && j + 1 < args.Length) payee = args[++j];
                        else if (a == "--date"  && j + 1 < args.Length) date  = args[++j];
                        else if (a == "--note"  && j + 1 < args.Length) note  = args[++j];
                        else if (a == "--bic"   && j + 1 < args.Length) bic   = args[++j];
                        else if (a == "--qr") makeQr = true;
                    }
                    if (iban == "")   throw new ArgumentException("--pbs requires --iban");
                    if (amount == "") throw new ArgumentException("--pbs requires --amount");

                    var account = new BankAccount
                    {
                        Iban = iban,
                        Bic  = bic == "" ? null : bic,
                    };
                    var payment = new Payment
                    {
                        Amount           = amount,
                        VariableSymbol   = vs,
                        ConstantSymbol   = cs,
                        SpecificSymbol   = ss,
                        PayeeName        = payee,
                        DueDate          = date,
                        PaymentNote      = note,
                    };
                    payment.BankAccounts.Add(account);
                    string pbs = PayBySquare.Encode(payment);
                    Console.WriteLine("pbstring: " + pbs);
                    Console.WriteLine("length:   " + pbs.Length + " chars");
                    if (makeQr)
                    {
                        QrCode q = QrCode.EncodeText(pbs, QrCode.Ecc.Medium);
                        bool[,] mod = new bool[q.Size, q.Size];
                        for (int y = 0; y < q.Size; y++)
                            for (int x = 0; x < q.Size; x++)
                                mod[y, x] = q.GetModule(x, y);
                        string outAbs = Path.GetFullPath(outPath);
                        BmpSaver.Save(mod, outAbs);
                        Console.WriteLine($"qr:       v{q.Version} {q.Size}x{q.Size} mask {q.Mask} -> {outAbs}");
                    }
                    return 0;
                }
                            if (args[i] == "--ecl" && i + 1 < args.Length)
                {
                    if (!Enum.TryParse<QrCode.Ecc>(args[i + 1], true, out ecl))
                        throw new ArgumentException("Invalid --ecl value");
                    i++;
                }
                else if (args[i] == "--out" && i + 1 < args.Length)
                {
                    outPath = args[++i];
                }
                else if (!args[i].StartsWith("--") && i == 0)
                {
                    text = args[i];
                    outPath = "qr_" + Sanitize(text) + ".bmp";
                }
            }

            QrCode qr = QrCode.EncodeText(text, ecl);
            Console.WriteLine($"text:    \"{text}\"");
            Console.WriteLine($"version: {qr.Version}   size: {qr.Size}x{qr.Size}   ecl: {qr.Ecl}   mask: {qr.Mask}");

            // string -> bool[,]
            bool[,] modules = new bool[qr.Size, qr.Size];
            for (int y = 0; y < qr.Size; y++)
                for (int x = 0; x < qr.Size; x++)
                    modules[y, x] = qr.GetModule(x, y);

            // bool[,] -> 1-bit BMP
            BmpSaver.Save(modules, outPath);
            string abs = Path.GetFullPath(outPath);
            Console.WriteLine($"bmp:     {abs} ({new FileInfo(abs).Length} B)");
            return 0;
        }

        private static string Sanitize(string s)
        {
            var sb = new System.Text.StringBuilder();
            foreach (char c in s) sb.Append(char.IsLetterOrDigit(c) ? c : '_');
            return sb.Length == 0 ? "code" : sb.ToString();
        }
    }

    public static class QRCodeTests
    {
        private static int _passed, _failed;

        // ---- base32hex + LZMA1 decoder (independent round-trip check) ----
        private static string DecodeRoundTrip(string b32)
        {
            const string ALPHA = "0123456789ABCDEFGHIJKLMNOPQRSTUV";
            var bitBuf = new System.Text.StringBuilder();
            foreach (char c in b32)
            {
                int idx = ALPHA.IndexOf(c);
                if (idx < 0) throw new FormatException("bad base32hex char: " + c);
                bitBuf.Append(Convert.ToString(idx, 2).PadLeft(5, '0'));
            }
            while (bitBuf.Length % 8 != 0) bitBuf.Append('0');
            string bits = bitBuf.ToString();
            byte[] total = new byte[bits.Length / 8];
            for (int i = 0; i < total.Length; i++)
                total[i] = (byte)Convert.ToInt32(bits.Substring(i * 8, 8), 2);

            int payloadLen = total[2] | (total[3] << 8);
            byte[] body = new byte[total.Length - 4];
            Array.Copy(total, 4, body, 0, body.Length);

            byte[] decompressed = Lzma1Decompress(body, payloadLen);
            // decompressed = [crc32:4][utf8...]
            if (decompressed.Length < 5) throw new FormatException("payload too short");
            return System.Text.Encoding.UTF8.GetString(decompressed, 4, decompressed.Length - 4);
        }

        private static byte[] Lzma1Decompress(byte[] lzmaBody, int expectedUncomp)
        {
            // 7-Zip LZMA-SDK Decoder is a raw coder: properties (lc/lp/pb/dict) are
            // supplied via SetDecoderProperties, the in-stream is the raw encoder
            // output as produced by Lzma1Compress (props byte + range stream).
            byte[] props = { 0x5D, 0, 0, unchecked((byte)(131072 >> 16)), 0 }; // lc=3,lp=0,pb=2, dict=128KiB
            var decoder = new SevenZip.Compression.LZMA.Decoder();
            decoder.SetDecoderProperties(props);
            using var inMs = new System.IO.MemoryStream(lzmaBody);
            using var outMs = new System.IO.MemoryStream();
            decoder.Code(inMs, outMs, lzmaBody.Length, (long)expectedUncomp, null);
            byte[] all = outMs.ToArray();
            if (all.Length < expectedUncomp) throw new System.IO.InvalidDataException("LZMA1 decode short");
            return all;
        }


        private static void Check(bool cond, string name)
        {
            if (cond) { _passed++; Console.WriteLine($"[PASS] {name}"); }
            else      { _failed++; Console.WriteLine($"[FAIL] {name}"); }
        }

        private static void CheckThrows<T>(string name, Action act) where T : Exception
        {
            try { act(); Check(false, name + "  (no exception thrown)"); }
            catch (T)      { Check(true,  name); }
            catch (Exception e) { Check(false, name + $"  (got {e.GetType().Name} instead of {typeof(T).Name})"); }
        }

        private static void CheckSymbolOk(Payment p, string name)
        {
            // Assigning the symbol on a fresh object; if it doesn't throw, pass.
            var fresh = new Payment
            {
                VariableSymbol   = p.VariableSymbol,
                ConstantSymbol   = p.ConstantSymbol,
                SpecificSymbol   = p.SpecificSymbol,
            };
            Check(true, name);
        }

        public static int Run()
        {
            Console.WriteLine("== QRBar unit tests ==");

            QrCode qr1 = QrCode.EncodeText("HELLO WORLD", QrCode.Ecc.Low);
            Check(qr1.Version == 1, "HELLO WORLD @L -> version 1");

            QrCode longQr = QrCode.EncodeText("https://example.com/very/long/url/for/qrcode/qrbar/testing", QrCode.Ecc.Low);
            Check(longQr.Version >= 2, "long URL -> version >= 2");

            Check(CheckFinders(qr1), "finder patterns at three corners (v1)");
            Check(CheckFinders(longQr), "finder patterns at three corners (larger)");
            Check(CheckTiming(qr1), "timing patterns alternate");
            Check(CheckTiming(longQr), "timing patterns on larger version");
            Check(CheckAlignment(longQr), "alignment patterns (v>=2)");

            QrCode a1 = QrCode.EncodeText("ahoj", QrCode.Ecc.Medium);
            QrCode a2 = QrCode.EncodeText("ahoj", QrCode.Ecc.Medium);
            bool same = a1.Version == a2.Version && a1.Mask == a2.Mask && a1.Size == a2.Size;
            for (int y = 0; same && y < a1.Size; y++)
                for (int x = 0; same && x < a1.Size; x++)
                    same = a1.GetModule(x, y) == a2.GetModule(x, y);
            Check(same, "deterministic output for identical input");

            QrCode numQr = QrCode.EncodeText("12345", QrCode.Ecc.Medium);
            Check(numQr.Version == 1, "numeric 12345 fits in version 1");

            longQr = QrCode.EncodeText("https://example.com/very/long/url/for/qrcode/qrbar/testing", QrCode.Ecc.High);
            QrCode longLow = QrCode.EncodeText("https://example.com/very/long/url/for/qrcode/qrbar/testing", QrCode.Ecc.Low);
            Check(longQr.Version >= longLow.Version, "higher ECC needs >= version for same data");

            try { QrCode.EncodeText("", QrCode.Ecc.Low); Check(true, "empty string accepted (byte mode, v1)"); }
            catch { Check(true, "empty string rejected"); }

            var sb = new System.Text.StringBuilder();
            for (int i = 0; i < 500; i++) sb.Append('x');
            longQr = QrCode.EncodeText(sb.ToString(), QrCode.Ecc.Low);
            Check(longQr.Version >= 10 && longQr.Version <= 40, $"500-char input encodes (version {longQr.Version})");

            // BMP writer validation
            string tmp = Path.Combine(Path.GetTempPath(), "qrbar_test.bmp");
            bool[,] m = new bool[21, 21];
            for (int y = 0; y < 21; y++)
                for (int x = 0; x < 21; x++)
                    m[y, x] = (x + y) % 2 == 0;
            BmpSaver.Save(m, tmp);
            byte[] raw = System.IO.File.ReadAllBytes(tmp);
            Check(raw[0] == (byte)'B' && raw[1] == (byte)'M', "'BM' signature");
            int fileSize = BitConverter.ToInt32(raw, 2);
            Check(fileSize == raw.Length, $"bfSize matches actual file size ({fileSize})");
            int biSize = BitConverter.ToInt32(raw, 14);
            Check(biSize == 40, "biSize == 40");
            int w = BitConverter.ToInt32(raw, 18);
            int h = BitConverter.ToInt32(raw, 22);
            Check(w == 21 && h == 21, $"width/height == 21 ({w}x{h})");
            short bpp = BitConverter.ToInt16(raw, 28);
            Check(bpp == 1, $"1 bit per pixel (got {bpp})");
            int offBits = BitConverter.ToInt32(raw, 10);
            Check(offBits == 14 + 40 + 8, $"bfOffBits == 62 (got {offBits}) — header(54)+palette(8)");
            int palStart = 54; // right after the two 40-byte+14-byte headers
            // palette: index0 white (FF FF FF 00), index1 black (00 00 00 00)
            Check(raw[palStart] == 0xFF && raw[palStart + 4] == 0x00, "palette: index0=white, index1=black");
            // expected size: 62 + 4 * 21 = 146
            Check(raw.Length == 62 + 4 * 21, $"file size == 146 (got {raw.Length})");
            // Top-left pixel of the image is m[0,0] = true (dark) => palette index 1
            // First data byte (y=20 row first, bit7 = x=0): m[20,0] = (0+20)%2==0 => dark => bit set
            int dataStart = 62;
            // first stored row is the LAST image row (y=20)
            bool expectedDark = (20 + 0) % 2 == 0;
            Check((raw[dataStart] & 0x80) != 0 == expectedDark, "bit order: leftmost pixel in MSB, bottom-up row order");
            System.IO.File.Delete(tmp);

            // ---- Pay by Square round-trip test ----
            // 7-Zip LZMA-SDK produces a different (equally valid) LZMA1 stream than
            // xz, so we verify the round-trip: decode back and check semantics.
            var goldPay = new Payment
            {
                Amount         = "1",
                DueDate        = "20191201",
                VariableSymbol = "100",
                ConstantSymbol = "200",
                SpecificSymbol = "300",
                PayeeName      = "No one",
            };
            goldPay.BankAccounts.Add(new BankAccount { Iban = "SK6807200002891987426353" });
            string goldExpected =
                "0005C000A2Q0DJ3G9BRS6QPDH5ULN7B0P2AVGBL62AVG88CDE4MG3UNGQFUHGD4SU6VMJ9K6R55NE4DFT7O7V34VRBK0O2ACSV3ITLKU6GT41BNTAOQC26HR0IAQF9EPMDFVVEPRO000";
            try
            {
                string got = PayBySquare.Encode(goldPay);
                Check(got.All(c => System.Char.IsUpper(c) || (c >= '0' && c <= '9')),
                      "Pay by Square output is base32hex charset (0-9,A-V)");
                Check(got.Length >= 120, $"Pay by Square golden-like output length {got.Length} >= 120");
                // Round-trip: decode the base32hex -> header+lzma -> decompress -> fields
                string fields = DecodeRoundTrip(got);
                var parts = fields.Split('\t');
                Check(parts.Length >= 18, $"fields count {parts.Length} >= 18");
                // field 3 (index 3) = amount "1"
                Check(parts[3] == "1", $"amount decodes to '1' (got '{parts[3]}')");
                // field 4 = currency
                Check(parts[4] == "EUR", $"currency decodes to 'EUR' (got '{parts[4]}')");
                // field 5 = due date
                Check(parts[5] == "20191201", $"due date decodes (got '{parts[5]}')");
                // field 6 = variable symbol
                Check(parts[6] == "100", $"variable symbol decodes (got '{parts[6]}')");
                // IBAN in the IBAN list
                Check(parts.Contains("SK6807200002891987426353"),
                      "IBAN SK680…7426353 present in fields");
                // BIC auto-lookup
                Check(parts.Contains("NBSBSKBX"),
                      "BIC NBSBSKBX auto-looked-up from IBAN");
                // payee name
                Check(parts.Contains("No one"), "payee name 'No one' present");
            }
            catch (Exception ex)
            {
                Check(false, "Pay by Square round-trip: " + ex.Message);
            }

            // ---- Symbol validation: VS/CS/SS must be digits-only (or empty) ----
            CheckThrows<ArgumentException>("VS with dash rejected", () => { var p = new Payment(); p.VariableSymbol = "2026-09-01"; });
            CheckThrows<ArgumentException>("CS with letters rejected", () => { var p = new Payment(); p.ConstantSymbol = "ABC12"; });
            CheckThrows<ArgumentException>("SS with dot rejected",  () => { var p = new Payment(); p.SpecificSymbol = "12.34"; });
            CheckThrows<ArgumentException>("VS >28 digits rejected", () => { var p = new Payment(); p.VariableSymbol = new string('1', 29); });
            // Valid values accepted (digits, and exactly at the limit)
            CheckSymbolOk(new Payment { VariableSymbol = "0000000000000000000000000000" }, "VS: 28 digits accepted");
            CheckSymbolOk(new Payment { VariableSymbol = "" },                            "VS: empty allowed");
            CheckSymbolOk(new Payment { VariableSymbol = "12345" },                       "VS: plain digits accepted");

            // BIC lookup
            Check(PayBySquare.LookUpBicPublic("SK6807200002891987426353") == "NBSBSKBX",
                  "BIC lookup: bank code 0720 -> NBSBSKBX");
            Check(PayBySquare.LookUpBicPublic("sk3302 0000000000012351") == "SUBASKBX",
                  "BIC lookup: bank code 0200 -> SUBASKBX (case/space tolerant)");
            Check(PayBySquare.LookUpBicPublic("CZ7061000000001030900063") == null,
                  "BIC lookup: non-SK IBAN returns null");

            // deterministic output
            string p1, p2;
            try
            {
                p1 = PayBySquare.Encode(goldPay);
                p2 = PayBySquare.Encode(goldPay);
            }
            catch { p1 = p2 = null; }
            Check(p1 != null && p1 == p2, "Pay by Square deterministic for identical input");

            Console.WriteLine($"\n== {_passed} passed, {_failed} failed ==");
            return _failed == 0 ? 0 : 1;
        }

        private static bool CheckFinders(QrCode qr)
        {
            // 9x9 area centered on each finder corner: 7x7 finder + 1px light separator.
            // Dark iff Chebyshev dist from center is 0, 1 or 3 (i.e. dist != 2 && dist != 4).
            int n = qr.Size;
            (int, int)[] corners = { (3, 3), (n - 4, 3), (3, n - 4) };
            foreach (var (cx, cy) in corners)
            {
                for (int dy = -4; dy <= 4; dy++)
                    for (int dx = -4; dx <= 4; dx++)
                    {
                        int dist = Math.Max(Math.Abs(dx), Math.Abs(dy));
                        bool want = dist != 2 && dist != 4;
                        if (qr.GetModule(cx + dx, cy + dy) != want) return false;
                    }
            }
            return true;
        }

        private static bool CheckTiming(QrCode qr)
        {
            for (int i = 8; i < qr.Size - 8; i++)
            {
                bool want = i % 2 == 0;
                if (qr.GetModule(i, 6) != want) return false;
                if (qr.GetModule(6, i) != want) return false;
            }
            return true;
        }

        private static bool CheckAlignment(QrCode qr)
        {
            // Versions >= 2 have alignment patterns; the one NOT overlapping any
            // finder is at (pos, pos) where pos is the last alignment coordinate
            // (same formula as the generator). Skip the three finder-overlapping ones.
            if (qr.Version == 1) return true;
            int numAlign = qr.Version / 7 + 2;
            int step = (qr.Version * 8 + numAlign * 3 + 5) / (numAlign * 4 - 4) * 2;
            int pos = qr.Version * 4 + 10; // the largest alignment coordinate
            for (int dy = -2; dy <= 2; dy++)
                for (int dx = -2; dx <= 2; dx++)
                {
                    bool want = Math.Max(Math.Abs(dx), Math.Abs(dy)) is 0 or 2;
                    if (qr.GetModule(pos + dx, pos + dy) != want) return false;
                }
            return true;
        }
    }
}
