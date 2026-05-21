using System;
using System.Collections.Generic;
using UnityEngine;

namespace MakeGamesPlay.WebBuildHost.Editor
{
    /// <summary>
    /// Minimal, dependency-free QR Code generator - just enough to turn a
    /// short URL into a scannable code for the Host Build window so a phone
    /// can scan straight to the hosted tunnel. Editor-only convenience: no
    /// third-party API, no network, works offline.
    ///
    /// Deliberately small scope to stay correct and maintainable:
    ///   - Byte mode only (URLs are ASCII / UTF-8 bytes).
    ///   - Error-correction level L (max data capacity → lowest version).
    ///   - Versions 1-5, which at level L are all SINGLE-BLOCK (no codeword
    ///     interleaving) and hold up to ~106 bytes - more than any
    ///     trycloudflare / LAN URL. <see cref="Generate"/> returns null for
    ///     longer input so the caller can fall back to showing the URL text.
    ///   - Full 8-mask evaluation with the four standard penalty rules so the
    ///     chosen mask reads cleanly off a screen.
    ///
    /// Algorithm per ISO/IEC 18004; structure mirrors the widely-used
    /// Project Nayuki reference implementation (public domain), reduced to
    /// the level-L single-block subset.
    /// </summary>
    public static class WebQrCode
    {
        // ─── Per-version tables (level L, single block) ────────────
        // Index by version (1..5); element 0 unused.
        static readonly int[] EcCodewordsL   = { 0, 7, 10, 15, 20, 26 };
        static readonly int[] TotalCodewords = { 0, 26, 44, 70, 100, 134 };
        // Alignment-pattern centre coordinates per version.
        static readonly int[][] AlignPositions =
        {
            null,
            new int[] { },          // V1: none
            new int[] { 6, 18 },    // V2
            new int[] { 6, 22 },    // V3
            new int[] { 6, 26 },    // V4
            new int[] { 6, 30 },    // V5
        };
        const int MaxVersion = 5;

        // ─── GF(256) tables ────────────────────────────────────────
        static readonly int[] GfExp = new int[512];
        static readonly int[] GfLog = new int[256];

        static WebQrCode()
        {
            int x = 1;
            for (int i = 0; i < 255; i++)
            {
                GfExp[i] = x;
                GfLog[x] = i;
                x <<= 1;
                if ((x & 0x100) != 0) x ^= 0x11D; // primitive polynomial
            }
            for (int i = 255; i < 512; i++) GfExp[i] = GfExp[i - 255];
        }

        static int GfMul(int a, int b)
        {
            if (a == 0 || b == 0) return 0;
            return GfExp[GfLog[a] + GfLog[b]];
        }

        // ─── Public entry ──────────────────────────────────────────

        /// <summary>
        /// Build a QR texture for <paramref name="text"/>, or null if the
        /// text is empty or too long for version 5 (≈106 bytes). The texture
        /// is 1 texel per module plus a 4-module quiet-zone border, point-
        /// filtered - let the GUI scale it up so it stays crisp.
        /// </summary>
        public static Texture2D Generate(string text, int quietZone = 4)
        {
            if (string.IsNullOrEmpty(text)) return null;
            var bytes = System.Text.Encoding.UTF8.GetBytes(text);
            int version = SelectVersion(bytes.Length);
            if (version < 0) return null;

            int[] dataCw = EncodeData(bytes, version);
            int[] ecCw   = ReedSolomon(dataCw, EcCodewordsL[version]);
            // Single block: codewords are simply data followed by EC.
            int[] all = new int[dataCw.Length + ecCw.Length];
            Array.Copy(dataCw, all, dataCw.Length);
            Array.Copy(ecCw, 0, all, dataCw.Length, ecCw.Length);

            bool[,] matrix = BuildMatrix(version, all);
            return ToTexture(matrix, quietZone);
        }

        // ─── Version + data encoding ───────────────────────────────

        static int SelectVersion(int byteLen)
        {
            for (int v = 1; v <= MaxVersion; v++)
            {
                int dcw = TotalCodewords[v] - EcCodewordsL[v];
                // 4-bit mode indicator + 8-bit char count = 12 bits overhead.
                int maxBytes = (dcw * 8 - 12) / 8;
                if (byteLen <= maxBytes) return v;
            }
            return -1;
        }

        static int[] EncodeData(byte[] bytes, int version)
        {
            int dataCodewords = TotalCodewords[version] - EcCodewordsL[version];
            int capacityBits = dataCodewords * 8;
            var bits = new List<bool>(capacityBits);

            AppendBits(bits, 0b0100, 4);        // byte mode
            AppendBits(bits, bytes.Length, 8);  // char count (8 bits, versions 1-9)
            foreach (var b in bytes) AppendBits(bits, b, 8);

            // Terminator (up to 4 zero bits) + pad to byte boundary.
            int term = Math.Min(4, capacityBits - bits.Count);
            for (int i = 0; i < term; i++) bits.Add(false);
            while (bits.Count % 8 != 0) bits.Add(false);

            // Pad bytes 0xEC / 0x11 alternating to fill capacity.
            int toggle = 0;
            while (bits.Count < capacityBits)
            {
                AppendBits(bits, toggle == 0 ? 0xEC : 0x11, 8);
                toggle ^= 1;
            }

            int[] cw = new int[dataCodewords];
            for (int i = 0; i < dataCodewords; i++)
            {
                int v = 0;
                for (int b = 0; b < 8; b++) v = (v << 1) | (bits[i * 8 + b] ? 1 : 0);
                cw[i] = v;
            }
            return cw;
        }

        static void AppendBits(List<bool> bits, int value, int count)
        {
            for (int i = count - 1; i >= 0; i--) bits.Add(((value >> i) & 1) != 0);
        }

        // ─── Reed-Solomon ──────────────────────────────────────────

        static int[] RsGenPoly(int degree)
        {
            int[] g = { 1 };
            for (int i = 0; i < degree; i++)
            {
                int[] ng = new int[g.Length + 1];
                Array.Copy(g, ng, g.Length);
                for (int j = 0; j < g.Length; j++)
                    ng[j + 1] ^= GfMul(g[j], GfExp[i]);
                g = ng;
            }
            return g; // length degree+1, leading coeff (g[0]) == 1
        }

        static int[] ReedSolomon(int[] data, int nEc)
        {
            int[] gen = RsGenPoly(nEc);
            int[] res = new int[data.Length + nEc];
            Array.Copy(data, res, data.Length);
            for (int i = 0; i < data.Length; i++)
            {
                int coef = res[i];
                if (coef != 0)
                    for (int j = 0; j < gen.Length; j++)
                        res[i + j] ^= GfMul(gen[j], coef);
            }
            int[] ec = new int[nEc];
            Array.Copy(res, data.Length, ec, 0, nEc);
            return ec;
        }

        // ─── Matrix construction ───────────────────────────────────

        static int Size(int version) => 17 + 4 * version;

        static bool[,] BuildMatrix(int version, int[] codewords)
        {
            int size = Size(version);
            var mod  = new bool[size, size]; // true = dark
            var func = new bool[size, size]; // true = function module (reserved)

            DrawFinder(mod, func, 0, 0);
            DrawFinder(mod, func, 0, size - 7);
            DrawFinder(mod, func, size - 7, 0);
            DrawSeparators(func, size);
            DrawTiming(mod, func, size);
            DrawAlignment(mod, func, version);
            ReserveFormat(func, size);
            // Dark module - always set, and reserved.
            mod[size - 8, 8] = true;
            func[size - 8, 8] = true;

            PlaceData(mod, func, size, codewords);

            // Evaluate all 8 masks, keep the lowest-penalty one.
            int bestMask = 0;
            int bestPenalty = int.MaxValue;
            for (int mask = 0; mask < 8; mask++)
            {
                ApplyMask(mod, func, size, mask);
                DrawFormat(mod, size, mask);
                int p = Penalty(mod, size);
                if (p < bestPenalty) { bestPenalty = p; bestMask = mask; }
                ApplyMask(mod, func, size, mask); // XOR is its own inverse - undo
            }
            ApplyMask(mod, func, size, bestMask);
            DrawFormat(mod, size, bestMask);
            return mod;
        }

        static void DrawFinder(bool[,] mod, bool[,] func, int r0, int c0)
        {
            for (int dr = 0; dr < 7; dr++)
                for (int dc = 0; dc < 7; dc++)
                {
                    bool dark = (dr == 0 || dr == 6 || dc == 0 || dc == 6) ||
                                (dr >= 2 && dr <= 4 && dc >= 2 && dc <= 4);
                    mod[r0 + dr, c0 + dc] = dark;
                    func[r0 + dr, c0 + dc] = true;
                }
        }

        static void DrawSeparators(bool[,] func, int size)
        {
            // 1-module light border around each finder (mod stays false; just
            // mark as function so data placement skips it).
            for (int i = 0; i < 8; i++)
            {
                func[7, i] = true; func[i, 7] = true;                       // top-left
                func[7, size - 1 - i] = true; func[i, size - 8] = true;     // top-right
                func[size - 8, i] = true; func[size - 1 - i, 7] = true;     // bottom-left
            }
        }

        static void DrawTiming(bool[,] mod, bool[,] func, int size)
        {
            for (int i = 8; i < size - 8; i++)
            {
                bool dark = (i % 2) == 0;
                mod[6, i] = dark; func[6, i] = true; // horizontal
                mod[i, 6] = dark; func[i, 6] = true; // vertical
            }
        }

        static void DrawAlignment(bool[,] mod, bool[,] func, int version)
        {
            var pos = AlignPositions[version];
            if (pos == null) return;
            foreach (int r in pos)
                foreach (int c in pos)
                {
                    // Skip the three that overlap the finder patterns.
                    if (func[r, c]) continue;
                    for (int dr = -2; dr <= 2; dr++)
                        for (int dc = -2; dc <= 2; dc++)
                        {
                            bool dark = Mathf.Max(Mathf.Abs(dr), Mathf.Abs(dc)) != 1;
                            mod[r + dr, c + dc] = dark;
                            func[r + dr, c + dc] = true;
                        }
                }
        }

        static void ReserveFormat(bool[,] func, int size)
        {
            for (int i = 0; i < 9; i++)
            {
                func[8, i] = true; func[i, 8] = true;
            }
            for (int i = 0; i < 8; i++)
            {
                func[8, size - 1 - i] = true; func[size - 1 - i, 8] = true;
            }
        }

        static void PlaceData(bool[,] mod, bool[,] func, int size, int[] codewords)
        {
            int bit = 0;
            int total = codewords.Length * 8;
            for (int right = size - 1; right >= 1; right -= 2)
            {
                int rcol = right;
                // Every column at or left of the vertical timing line (col 6)
                // shifts left by one, which skips col 6 entirely AND keeps the
                // remaining left columns correctly paired down to col 0. (Only
                // remapping the exact col 6 would drop col 0 and double-write
                // col 4.)
                if (rcol <= 6) rcol -= 1;
                for (int v = 0; v < size; v++)
                {
                    for (int j = 0; j < 2; j++)
                    {
                        int col = rcol - j;
                        bool upward = (((rcol + 1) & 2) == 0);
                        int row = upward ? (size - 1 - v) : v;
                        if (!func[row, col] && bit < total)
                        {
                            int cw = codewords[bit >> 3];
                            bool dark = ((cw >> (7 - (bit & 7))) & 1) != 0;
                            mod[row, col] = dark;
                            bit++;
                        }
                    }
                }
            }
        }

        static bool MaskBit(int mask, int r, int c)
        {
            switch (mask)
            {
                case 0: return (r + c) % 2 == 0;
                case 1: return r % 2 == 0;
                case 2: return c % 3 == 0;
                case 3: return (r + c) % 3 == 0;
                case 4: return (r / 2 + c / 3) % 2 == 0;
                case 5: return (r * c) % 2 + (r * c) % 3 == 0;
                case 6: return ((r * c) % 2 + (r * c) % 3) % 2 == 0;
                case 7: return ((r + c) % 2 + (r * c) % 3) % 2 == 0;
                default: return false;
            }
        }

        static void ApplyMask(bool[,] mod, bool[,] func, int size, int mask)
        {
            for (int r = 0; r < size; r++)
                for (int c = 0; c < size; c++)
                    if (!func[r, c] && MaskBit(mask, r, c))
                        mod[r, c] = !mod[r, c];
        }

        static void DrawFormat(bool[,] mod, int size, int mask)
        {
            int data = (1 << 3) | mask; // EC level L = 0b01
            int rem = data;
            for (int i = 0; i < 10; i++) rem = (rem << 1) ^ ((rem >> 9) * 0x537);
            int bits = ((data << 10) | rem) ^ 0x5412; // 15 bits

            // Copy 1 - around the top-left finder. Indices are [row, col]; the
            // first 6 bits run DOWN column 8, the rest run LEFT along row 8.
            for (int i = 0; i <= 5; i++) mod[i, 8] = GetBit(bits, i);
            mod[7, 8] = GetBit(bits, 6);
            mod[8, 8] = GetBit(bits, 7);
            mod[8, 7] = GetBit(bits, 8);
            for (int i = 9; i < 15; i++) mod[8, 14 - i] = GetBit(bits, i);

            // Copy 2 - split across the top-right and bottom-left finders.
            for (int i = 0; i < 8; i++) mod[8, size - 1 - i] = GetBit(bits, i);
            for (int i = 8; i < 15; i++) mod[size - 15 + i, 8] = GetBit(bits, i);
        }

        static bool GetBit(int value, int i) => ((value >> i) & 1) != 0;

        // ─── Mask penalty scoring (4 standard rules) ───────────────

        static int Penalty(bool[,] m, int size)
        {
            int penalty = 0;

            // Rule 1: runs of 5+ same-colour modules in each row and column.
            for (int r = 0; r < size; r++)
            {
                penalty += RunPenalty(m, size, r, true);
                penalty += RunPenalty(m, size, r, false);
            }

            // Rule 2: 2x2 same-colour blocks.
            for (int r = 0; r < size - 1; r++)
                for (int c = 0; c < size - 1; c++)
                {
                    bool v = m[r, c];
                    if (v == m[r, c + 1] && v == m[r + 1, c] && v == m[r + 1, c + 1])
                        penalty += 3;
                }

            // Rule 3: finder-like 1:1:3:1:1 pattern with 4 light modules on a
            // side, in rows and columns.
            for (int r = 0; r < size; r++)
                for (int c = 0; c < size; c++)
                {
                    if (HasFinderLike(m, size, r, c, true)) penalty += 40;
                    if (HasFinderLike(m, size, r, c, false)) penalty += 40;
                }

            // Rule 4: deviation of the dark-module proportion from 50%.
            int dark = 0;
            for (int r = 0; r < size; r++)
                for (int c = 0; c < size; c++)
                    if (m[r, c]) dark++;
            int total = size * size;
            int percent = dark * 100 / total;
            int k = Mathf.Abs(percent - 50) / 5;
            penalty += k * 10;

            return penalty;
        }

        static int RunPenalty(bool[,] m, int size, int line, bool row)
        {
            int penalty = 0;
            int runColor = -1;
            int runLen = 0;
            for (int i = 0; i < size; i++)
            {
                bool v = row ? m[line, i] : m[i, line];
                int color = v ? 1 : 0;
                if (color == runColor)
                {
                    runLen++;
                    if (runLen == 5) penalty += 3;
                    else if (runLen > 5) penalty += 1;
                }
                else { runColor = color; runLen = 1; }
            }
            return penalty;
        }

        // The 11-module finder-like signature: dark light dark dark dark
        // light dark, preceded or followed by 4 light modules.
        static readonly bool[] FinderSig =
            { true, false, true, true, true, false, true };

        static bool HasFinderLike(bool[,] m, int size, int r, int c, bool row)
        {
            // Need 11 modules from (r,c) along the line.
            int len = row ? size - c : size - r;
            if (len < 11) return false;

            bool Get(int k) => row ? m[r, c + k] : m[r + k, c];

            // Core 7-module pattern at offset 0..6.
            for (int k = 0; k < 7; k++)
                if (Get(k) != FinderSig[k]) return false;

            // 4 light modules before OR after the core.
            bool lightAfter = true;
            for (int k = 7; k < 11; k++) if (Get(k)) { lightAfter = false; break; }
            if (lightAfter) return true;

            // Before: 4 light modules preceding the pattern.
            int start = row ? c : r;
            if (start >= 4)
            {
                bool lightBefore = true;
                for (int k = 1; k <= 4; k++)
                {
                    bool v = row ? m[r, c - k] : m[r - k, c];
                    if (v) { lightBefore = false; break; }
                }
                if (lightBefore) return true;
            }
            return false;
        }

        // ─── Rendering ─────────────────────────────────────────────

        static Texture2D ToTexture(bool[,] matrix, int quiet)
        {
            int size = matrix.GetLength(0);
            int dim = size + quiet * 2;
            var tex = new Texture2D(dim, dim, TextureFormat.RGBA32, false)
            {
                filterMode = FilterMode.Point,
                wrapMode = TextureWrapMode.Clamp,
            };
            var px = new Color32[dim * dim];
            var white = new Color32(255, 255, 255, 255);
            var black = new Color32(0, 0, 0, 255);
            for (int i = 0; i < px.Length; i++) px[i] = white;
            for (int r = 0; r < size; r++)
                for (int c = 0; c < size; c++)
                    if (matrix[r, c])
                    {
                        // Texture origin is bottom-left; QR row 0 is the top,
                        // so flip vertically when writing.
                        int tx = c + quiet;
                        int ty = (size - 1 - r) + quiet;
                        px[ty * dim + tx] = black;
                    }
            tex.SetPixels32(px);
            tex.Apply(false, false);
            return tex;
        }
    }
}
