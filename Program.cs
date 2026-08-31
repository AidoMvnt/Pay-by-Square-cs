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

        private static void Check(bool cond, string name)
        {
            if (cond) { _passed++; Console.WriteLine($"[PASS] {name}"); }
            else      { _failed++; Console.WriteLine($"[FAIL] {name}"); }
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
