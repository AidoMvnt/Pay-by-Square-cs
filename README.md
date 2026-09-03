# Pay by Square — C# (QR generator)

> ⚠️ **Disclaimer:** This code is **AI-generated** and provided **without any
> warranty whatsoever**. Use it **at your own risk** only — not intended for
> real financial transactions without thorough verification.

A small, dependency-light **QR Code generator** with a dedicated **Pay by Square
(SK) payment layer** on top. Given a payment (IBAN, amount, variable/constant/
specific symbol, due date, parties), it produces the exact **base32hex `pbstring`**
that Slovak banks expect, and renders it as a scannable QR Code (BMP/PNG).

## What it does

- **QR engine** — Reed–Solomon error correction, all mask patterns, versions 1–40.
  A verbatim C# port of Project Nayuki's `qrcodegen` (see license notes below).
- **Pay by Square layer** — builds the tab-separated field record, prepends a
  little-endian CRC32, compresses with **LZMA1** (`lc=3, lp=0, pb=2`, dictionary
  128 KiB, end-marker on) using a **pure-C# LZMA1 encoder with zero dependencies**
  (`src/Lzma1Encoder.cs`), wraps in the 4-byte header, and encodes to **Base32Hex**.
  Output is byte-compatible with the official `bysquare` bank decoder (verified by
  decode round-trip against the independent 7‑Zip `LZMA-SDK` reference decoder).
- **OS independent** — pure managed code on .NET, no GDI/system calls, and
  **no NuGet packages at all** in the generator. Runs on Windows, Linux and macOS.
- **Tests** — 64 unit tests in a dedicated `test/` project, including field-level
  decode round-trip (independent `LZMA-SDK` decoder) and symbol validation.

## Quick start

```
# unit tests (separate test project; may use libraries for independent verification)
dotnet run --project test

# Pay by Square QR (Slovakia)
dotnet run --project src -- --pbs --iban SK6807200002891987426353 --amount 42.50 \
             --vs 2026090114 --cs 12345 --payee "Mainvent s.r.o." \
             --date 20260930 --note "Faktura 14/26" --qr
```

CLI options: `--help`, and the `--pbs` mode (`--iban`, `--amount`,
`--vs`, `--cs`, `--ss`, `--payee`, `--street`, `--city`, `--payer`, `--date`,
`--note`, `--bic`, `--ecl`, `--qr`). Variable/constant/specific symbols are
validated as digits-only (length-limited) per the SK spec.

## Sources / credit

- **QR Code generator:** port of **Project Nayuki — QR Code generator**
  (https://github.com/nayuki/QR-Code-generator, MIT License), originally from the
  C reference implementation, adapted to C#.
- **LZMA1 compression:** `src/Lzma1Encoder.cs` — a self-contained pure-C# LZMA1
  range coder (hash-chain match finding, lc/lp/pb-configurable). **AI-generated:
  written with Claude Code** (Anthropic's coding agent). Follows the LZMA
  algorithm authored by Igor Pavlov (7‑Zip, public-domain-style license);
  no NuGet package required. Verified byte-by-byte against the independent
  `LZMA-SDK` reference decoder in the test project.
- **Pay by Square wire format:** the field ordering, CRC32, LZMA1 parameters and
  Base32Hex layout follow the **bysquare.sk** official reference
  implementation; the BIC dictionary is derived from that same reference
  (Slovak bank code → BIC).

## How it was generated

This implementation was **AI-generated: written with Claude Code**
(Anthropic's coding agent) while assisting Martin. The agent drew on: the
Nayuki QR reference, the `bysquare` (npm) + `skqr` (PHP) reference
implementations for the Pay by Square wire format and BIC dictionary, and a
pure-C# LZMA1 encoder. Golden test vectors and decode round-trips (verified
against the independent LZMA-SDK reference decoder and the `bysquare` npm
decoder) were used to validate byte-compatibility. No proprietary payment data
has been baked in.

## License

**MIT License** — free and open for **any** use, including commercial use.
Anyone may use, copy, modify, and redistribute this code.

The bundled/port of Project Nayuki QR-Code-generator code additionally carries
its own **MIT** license (see `src/QrCode.cs` header). LZMA/7‑Zip components are
public-domain-style.

Permission is hereby granted, free of charge, to any person obtaining a copy of
this software and associated documentation files (the "Software"), to deal in the
Software without restriction, including without limitation the rights to use, copy,
modify, merge, publish, distribute, sublicense, and/or sell copies of the Software,
and to permit persons to whom the Software is furnished to do so, subject to the
following conditions:

The above copyright notice and this permission notice shall be included in all
copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR IMPLIED,
INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY, FITNESS FOR A
PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE AUTHORS OR COPYRIGHT
HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER LIABILITY, WHETHER IN AN ACTION OF
CONTRACT, TORT OR OTHERWISE, ARISING FROM, OUT OF OR IN CONNECTION WITH THE SOFTWARE
OR THE USE OR OTHER DEALINGS IN THE SOFTWARE.

Copyright © 2026 Martin Bujnak (Mainvent s.r.o.). All rights free as per MIT.
