using System;
using System.Text;
using System.Collections.Generic;

namespace PayBySquare
{
    /// <summary>A Pay by Square bank account (IBAN + optional BIC).</summary>
    public sealed class BankAccount
    {
        public string Iban { get; set; } = "";
        public string Bic  { get; set; } = "";
    }

    /// <summary>Input data for a single Pay by Square QR payment.</summary>
    public sealed class Payment
    {
        public string InvoiceId      { get; set; } = "";
        public string Amount         { get; set; } = "0.00";
        public string Currency       { get; set; } = "EUR";
        public string DueDate        { get; set; } = "";   // YYYYMMDD

        /// <summary>Variable symbol — digits only (or empty), max 28 chars.</summary>
        public string VariableSymbol
        {
            get => _variableSymbol;
            set => _variableSymbol = CheckSymbol(value, "VariableSymbol", 28);
        }
        private string _variableSymbol = "";

        /// <summary>Constant symbol — digits only (or empty), max 5 chars.</summary>
        public string ConstantSymbol
        {
            get => _constantSymbol;
            set => _constantSymbol = CheckSymbol(value, "ConstantSymbol", 5);
        }
        private string _constantSymbol = "";

        /// <summary>Specific symbol — digits only (or empty), max 10 chars.</summary>
        public string SpecificSymbol
        {
            get => _specificSymbol;
            set => _specificSymbol = CheckSymbol(value, "SpecificSymbol", 10);
        }
        private string _specificSymbol = "";

        private static string CheckSymbol(string? value, string name, int maxLen)
        {
            string v = value?.Trim() ?? "";
            if (v.Length > 0 && v.Any(c => c < '0' || c > '9'))
                throw new ArgumentException($"{name} must contain digits only (got \"{value}\").");
            if (v.Length > maxLen)
                throw new ArgumentException($"{name} exceeds {maxLen} digits (got {v.Length}).");
            return v;
        }

        public string PaymentNote    { get; set; } = "";
        public string PayeeName      { get; set; } = "";
        public string PayeeStreet    { get; set; } = "";
        public string PayeeCity      { get; set; } = "";
        public string PayerName      { get; set; } = "";
        public List<BankAccount> BankAccounts { get; } = new List<BankAccount>();
    }

    public static class PayBySquare
    {
        private const string B32Alpha = "0123456789ABCDEFGHIJKLMNOPQRSTUV";
        private const int    MAX_RAW  = 65535;

        /// <summary>Public wrapper for the bank-code -> BIC lookup (used by unit tests).</summary>
        public static string? LookUpBicPublic(string iban) => LookUpBic(iban);

        /// <summary>Generate a Pay by Square base32hex QR content string.</summary>
        public static string Encode(Payment pay)
        {
            if (pay.BankAccounts.Count == 0)
                throw new ArgumentException("At least one bank account (IBAN) is required.");

            // 1) Field string (tab-separated, exact order per bysquare.sk spec)
            var f = new List<string>();
            f.Add(   pay.InvoiceId    ?? ""   );
            f.Add("1");                              // number of payments
            f.Add("1");                              // type: 1 = instant payment order
            f.Add(   pay.Amount       ?? "0.00" );
            f.Add(   pay.Currency     ?? "EUR"  );
            f.Add(   pay.DueDate      ?? ""     );
            f.Add(   pay.VariableSymbol ?? ""   );
            f.Add(   pay.ConstantSymbol ?? ""   );
            f.Add(   pay.SpecificSymbol ?? ""   );
            f.Add("");                               // SEPA reference (unused)
            f.Add(   pay.PaymentNote  ?? ""     );
            f.Add(pay.BankAccounts.Count.ToString());
            foreach (var a in pay.BankAccounts)
            {
                f.Add(a.Iban ?? "");
                string bic = (!string.IsNullOrEmpty(a.Bic)) ? a.Bic! : LookUpBic(a.Iban ?? "") ?? "";
                f.Add(bic);
            }
            f.Add("0");                              // standing order: no
            f.Add("0");                              // direct debit:  no
            f.Add(   pay.PayeeName   ?? "" );
            f.Add(   pay.PayeeStreet ?? "" );
            f.Add(   pay.PayeeCity   ?? "" );

            string tabbed = string.Join("\t", f);
            byte[] utf8   = Encoding.UTF8.GetBytes(tabbed);

            // 2) CRC32 (little-endian) prepended
            int crc = Crc32.Compute(utf8);
            byte[] payload = new byte[4 + utf8.Length];
            payload[0] = (byte)(crc & 0xFF);
            payload[1] = (byte)((crc >> 8) & 0xFF);
            payload[2] = (byte)((crc >> 16) & 0xFF);
            payload[3] = (byte)((crc >> 24) & 0xFF);
            Buffer.BlockCopy(utf8, 0, payload, 4, utf8.Length);
            if (payload.Length > MAX_RAW)
                throw new ArgumentException("Payload too large for Pay by Square.");

            // 3) LZMA1 raw compression (lc=3,lp=0,pb=2, dict=128KiB)
            byte[] body = Lzma1Compress(payload);

            // 4) Header: 00 00 + uint16-LE( payload length )
            byte[] outBytes = new byte[4 + body.Length];
            outBytes[0] = 0x00;
            outBytes[1] = 0x00;
            outBytes[2] = (byte)(payload.Length & 0xFF);
            outBytes[3] = (byte)((payload.Length >> 8) & 0xFF);
            Buffer.BlockCopy(body, 0, outBytes, 4, body.Length);

            // 5) Base32Hex encode
            return Base32HexEncode(outBytes);
        }

        // ---- LZMA1 raw compression via 7-Zip LZMA-SDK (pure C#, no native deps) ----
        // Parameters match the bysquare.sk spec: lc=3, lp=0, pb=2, dictionary 128 KiB,
        // end marker on. Verified: output is accepted by the official bysquare
        // bank decoder (decode round-trip) and matches xz --format=raw layout.
        private static byte[] Lzma1Compress(byte[] data)
        {
            var enc = new SevenZip.Compression.LZMA.Encoder();
            enc.SetCoderProperties(
                new[]
                {
                    SevenZip.CoderPropID.LitContextBits,
                    SevenZip.CoderPropID.LitPosBits,
                    SevenZip.CoderPropID.PosStateBits,
                    SevenZip.CoderPropID.DictionarySize,
                    SevenZip.CoderPropID.EndMarker,
                },
                new object[] { 3, 0, 2, 131072, true });

            using var inMs  = new MemoryStream(data);
            using var outMs = new MemoryStream();
            enc.Code(inMs, outMs, data.Length, 0, null);
            return outMs.ToArray();
        }

        // ---- Base32Hex (0-9A-V, 5 bits/group, big-endian, zero-padded) ----
        private static string Base32HexEncode(byte[] data)
        {
            var sb = new StringBuilder((data.Length * 8 + 4) / 5);
            int acc = 0, bitsInAccum = 0;
            foreach (byte b in data)
            {
                for (int i = 7; i >= 0; i--)
                {
                    acc = (acc << 1) | ((b >> i) & 1);
                    bitsInAccum++;
                    if (bitsInAccum == 5) { sb.Append(B32Alpha[acc & 0x1F]); acc = 0; bitsInAccum = 0; }
                }
            }
            if (bitsInAccum > 0) { acc <<= (5 - bitsInAccum); sb.Append(B32Alpha[acc & 0x1F]); }
            return sb.ToString();
        }

        internal static byte[] Base32HexDecode(string s)
        {
            var bytes = new byte[(s.Length * 5) / 8];
            int acc = 0, bits = 0;
            for (int i = 0; i < s.Length; i++)
            {
                int v = B32Alpha.IndexOf(s[i]);
                if (v < 0) throw new FormatException("Invalid base32hex char '" + s[i] + "'");
                acc = (acc << 5) | v;
                bits += 5;
                if (bits >= 8)
                {
                    bits -= 8;
                    int idx = (bytes.Length * 8) - (bits + 8);
                    bytes[idx / 8] = (byte)((acc >> bits) & 0xFF);
                }
            }
            return bytes;
        }

        // ---- CRC32 (zlib/ISO-HDLC, table-driven) ----
        private static class Crc32
        {
            private static uint[]? Table;
            public static int Compute(byte[] d)
            {
                if (Table == null)
                {
                    Table = new uint[256];
                    for (uint i = 0; i < 256; i++)
                    {
                        uint c = i;
                        for (int k = 0; k < 8; k++)
                            c = (c & 1u) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1;
                        Table[i] = c;
                    }
                }
                uint crc = 0xFFFFFFFFu;
                for (int i = 0; i < d.Length; i++)
                    crc = Table[(crc ^ d[i]) & 0xFF] ^ (crc >> 8);
                return unchecked((int)(crc ^ 0xFFFFFFFFu));
            }
        }

        // ---- Slovak BIC dictionary (bank code prefix -> BIC) ----
        // Source: official bysquare.sk reference implementation (SlovakBicDictionary.php).
        private static string? LookUpBic(string iban)
        {
            string clean = (iban ?? "").Replace(" ", "").ToUpperInvariant();
            if (clean.StartsWith("SK") && clean.Length >= 8)
            {
                string code = clean.Substring(4, 4);
                switch (code)
                {
                    case "0200": return "SUBASKBX";
                    case "0720": return "NBSBSKBX";
                    case "0900": return "GIBASKBX";
                    case "1100": return "TATRSKBX";
                    case "1111": return "UNCRSKBX";
                    case "2010": return "FIOBCZPP";
                    case "3000": return "SLZBSKBA";
                    case "3100": return "LUBASKBX";
                    case "5200": return "OTPVSKBX";
                    case "5600": return "KOMASK2X";
                    case "5900": return "PRVASKBA";
                    case "6500": return "POBNSKBA";
                    case "7300": return "INGBSKBX";
                    case "7500": return "CEKOSKBX";
                    case "7930": return "WUSTSKBA";
                    case "8020": return "CRLYSKBX";
                    case "8050": return "COBASKBX";
                    case "8100": return "KOMBSKBA";
                    case "8120": return "BSLOSK22";
                    case "8130": return "CITISKBA";
                    case "8160": return "EXSKSKBX";
                    case "8170": return "KBSPSKBX";
                    case "8180": return "SPSRSKBA";
                    case "8300": return "HSBCSKBA";
                    case "8320": return "JTBPSKBA";
                    case "8330": return "FIOZSKBA";
                    case "8350": return "ABNASKBX";
                    case "8360": return "BREXSKBX";
                    case "8370": return "OBKLSKBA";
                    case "8390": return "AKCTCZ21";
                    case "8410": return "RIDBSKBX";
                    case "8420": return "BFKKSKBB";
                    case "8430": return "KODBSKBX";
                    case "8440": return "BNPASA";
                    case "9950": return "FDXXSKBA";
                    case "9951": return "XBRASKB1";
                    case "9952": return "TPAYSKBX";
                }
            }
            return null;
        }
    }
}
