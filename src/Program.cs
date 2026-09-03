using System;
using System.IO;

namespace PayBySquare
{
    public static class Program
    {
        public static int Main(string[] args)
        {
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
            bool pbsHandled = false;

            for (int i = 0; i < args.Length; i++)
            {
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
                    bool makeTile = false, do1bitTile = false, doSvg = false;
                    int? qrPixel = null;
                    int[]? pageBg = null;
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
                        else if (a == "--out"   && j + 1 < args.Length) outPath = args[++j];
                        else if (a == "--qr")    makeQr = true;
                        else if (a == "--tile")  { makeQr = true; makeTile = true; }
                        else if (a == "--tile1") { makeQr = true; makeTile = true; do1bitTile = true; }
                        else if (a == "--tilesvg") { makeQr = true; makeTile = true; doSvg = true; }
                        else if (a == "--qrp"   && j + 1 < args.Length) qrPixel = int.Parse(args[++j]);
                        else if (a == "--qrpix" && j + 1 < args.Length) qrPixel = int.Parse(args[++j]);
                        else if (a == "--bg"    && j + 1 < args.Length)
                        {
                            var parts = args[++j].Split(',');
                            if (parts.Length != 3) throw new ArgumentException("--bg expects R,G,B (0..255)");
                            pageBg = new int[] { int.Parse(parts[0]), int.Parse(parts[1]), int.Parse(parts[2]) };
                        }
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
                        if (makeTile)
                        {
                            // Decorated tile (frame + "PAY by square" caption + icon).
                            // --tile   = 24-bit RGB   (default)
                            // --tile1  = 1-bit        (only this)
                            // --tilesvg= vector SVG
                            var deco = new QrTileDecorator();
                            if (qrPixel.HasValue) deco.ModuleScale = qrPixel.Value;
                            if (pageBg != null) deco.Page = (pageBg[0], pageBg[1], pageBg[2]);
                            if (do1bitTile) deco.Mono = true;   // print path: flat ink, no colored glyphs
                            var (w, h, rgb) = deco.RenderTile(mod);
                            string tileAbs = Path.GetFullPath(outPath);
                            if (!do1bitTile)
                            {
                                new Bmp24Saver().Save(w, h, rgb, tileAbs);           // lossless RGB
                                Console.WriteLine($"tile(24): {w}x{h} -> {tileAbs} ({new FileInfo(tileAbs).Length} B)");
                            }
                            if (do1bitTile)
                            {
                                // --tile1: the 1-bit tile IS the requested output -> write it to outPath
                                new BmpSaver().Save(w, h, rgb, tileAbs);             // 1-bit (R&G&B > T)
                                Console.WriteLine($"tile(1):  {w}x{h} -> {tileAbs} ({new FileInfo(tileAbs).Length} B)");
                            }
                            if (doSvg)
                            {
                                // --tilesvg: the SVG IS the requested output -> write it to outPath
                                File.WriteAllText(tileAbs, deco.ToSvg(mod));
                                Console.WriteLine($"tile(svg): {w}x{h} -> {tileAbs}");
                            }
                        }
                        else
                        {
                            string outAbs = Path.GetFullPath(outPath);
                            new BmpSaver().Save(mod, outAbs);
                            Console.WriteLine($"qr:       v{q.Version} {q.Size}x{q.Size} mask {q.Mask} -> {outAbs}");
                        }
                    }
                    pbsHandled = true;   // --pbs owns its own output; skip the "ahoj" text path
                    continue;   // don't exit the arg loop — allow more flags after --pbs
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

            // Text-based QR path ("--text" / positional / --ecl / --out).
            // When --pbs already produced its own output, skip this entirely.
            if (!pbsHandled)
            {
                QrCode qr = QrCode.EncodeText(text, ecl);
                Console.WriteLine($"text:    \"{text}\"");
                Console.WriteLine($"version: {qr.Version}   size: {qr.Size}x{qr.Size}   ecl: {qr.Ecl}   mask: {qr.Mask}");

                // string -> bool[,]
                bool[,] modules = new bool[qr.Size, qr.Size];
                for (int y = 0; y < qr.Size; y++)
                    for (int x = 0; x < qr.Size; x++)
                        modules[y, x] = qr.GetModule(x, y);

                // bool[,] -> 1-bit BMP
                new BmpSaver().Save(modules, outPath);
                string abs = Path.GetFullPath(outPath);
                Console.WriteLine($"bmp:     {abs} ({new FileInfo(abs).Length} B)");
            }
            return 0;
        }

        private static string Sanitize(string s)
        {
            var sb = new System.Text.StringBuilder();
            foreach (char c in s) sb.Append(char.IsLetterOrDigit(c) ? c : '_');
            return sb.Length == 0 ? "code" : sb.ToString();
        }
    }

    

}
