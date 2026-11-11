using System.IO.Compression;
using System.Text;

namespace Pulse.Core.Games.Brawlhalla;

/// <summary>A picture to put in a game file: width, height and straight (not premultiplied) RGBA bytes.</summary>
public sealed record SwfImage(int Width, int Height, byte[] Rgba);

/// <summary>A named part of a skin ("a_Hair_Mikasa") and its size at the game's base scale, in pixels.</summary>
public sealed record SwfPart(string Name, int Id, double Width, double Height);

/// <summary>
/// Just enough of the Flash file format to customize Brawlhalla's art: reads the parts a skin file names, puts a
/// picture in place of a part, and changes colors in the vector art. Everything is done on bytes in memory; the
/// caller decides where they're written. Only compressed (CWS) and plain (FWS) files: Brawlhalla's are CWS.
/// </summary>
public sealed class SwfFile
{
    sealed record Tag(int Code, int Offset, int Length);

    readonly byte _version;
    byte[] _body;
    int _tagsStart;
    List<Tag> _tags = new();
    readonly Dictionary<int, Tag> _defs = new();
    readonly Dictionary<string, int> _symbols = new(StringComparer.Ordinal);

    static readonly HashSet<int> ShapeCodes = new() { 2, 22, 32, 83 };
    // Tags that define a character: their first two bytes are its id.
    static readonly HashSet<int> DefineCodes = new() { 2, 6, 7, 10, 11, 13, 14, 20, 21, 22, 32, 33, 34, 35, 36, 37, 39, 46, 48, 60, 75, 83, 84, 87, 90, 91 };

    SwfFile(byte version, byte[] body) { _version = version; _body = body; Index(); }

    public static SwfFile Load(byte[] file)
    {
        if (file == null || file.Length < 8) throw new InvalidDataException("Not a Flash file.");
        var sig = Encoding.ASCII.GetString(file, 0, 3);
        byte[] body;
        if (sig == "FWS") body = file[8..];
        else if (sig == "CWS")
        {
            using var z = new ZLibStream(new MemoryStream(file, 8, file.Length - 8), CompressionMode.Decompress);
            using var ms = new MemoryStream();
            z.CopyTo(ms);
            body = ms.ToArray();
        }
        else throw new InvalidDataException("This kind of Flash file isn't supported.");
        return new SwfFile(file[3], body);
    }

    public byte[] Save()
    {
        using var ms = new MemoryStream();
        ms.Write("CWS"u8);
        ms.WriteByte(_version);
        ms.Write(BitConverter.GetBytes(_body.Length + 8));
        using (var z = new ZLibStream(ms, CompressionLevel.Optimal, leaveOpen: true)) z.Write(_body);
        return ms.ToArray();
    }

    void Index()
    {
        _tags.Clear(); _defs.Clear(); _symbols.Clear();
        var r = new BitReader(_body, 0);
        r.Rect();
        _tagsStart = r.BytePos + 4;
        _tags = ReadTags(_tagsStart, _body.Length);
        foreach (var t in _tags)
        {
            if (DefineCodes.Contains(t.Code) && t.Length >= 2) _defs[U16(t.Offset)] = t;
            if (t.Code == 76) // SymbolClass: id → class name
            {
                int n = U16(t.Offset), q = t.Offset + 2;
                for (int i = 0; i < n && q < t.Offset + t.Length; i++)
                {
                    int id = U16(q); q += 2;
                    int end = Array.IndexOf(_body, (byte)0, q);
                    if (end < 0) break;
                    _symbols[Encoding.UTF8.GetString(_body, q, end - q)] = id;
                    q = end + 1;
                }
            }
        }
    }

    List<Tag> ReadTags(int pos, int end)
    {
        var list = new List<Tag>();
        while (pos + 2 <= end)
        {
            int h = U16(pos); pos += 2;
            int code = h >> 6, len = h & 63;
            if (len == 63) { len = (int)BitConverter.ToUInt32(_body, pos); pos += 4; }
            if (pos + len > end) break;
            list.Add(new Tag(code, pos, len));
            pos += len;
            if (code == 0) break;
        }
        return list;
    }

    int U16(int at) => _body[at] | (_body[at + 1] << 8);

    /// <summary>Every exported name in the file ("a_Hair_Mikasa").</summary>
    public IReadOnlyCollection<string> Symbols => _symbols.Keys;

    /// <summary>The parts a skin file names (exported sprites starting "a_"), with their sizes.</summary>
    public List<SwfPart> Parts()
    {
        var list = new List<SwfPart>();
        foreach (var (name, id) in _symbols)
        {
            if (!name.StartsWith("a_", StringComparison.Ordinal) || !_defs.TryGetValue(id, out var t) || t.Code != 39) continue;
            var b = Bounds(id, Matrix.Identity, 0);
            if (b == null) continue;
            list.Add(new SwfPart(name, id, (b.Value.X1 - b.Value.X0) / 20.0, (b.Value.Y1 - b.Value.Y0) / 20.0));
        }
        return list.OrderBy(p => p.Name, StringComparer.Ordinal).ToList();
    }

    // ───────── geometry ─────────

    readonly record struct Box(double X0, double X1, double Y0, double Y1)
    {
        public Box Union(Box o) => new(Math.Min(X0, o.X0), Math.Max(X1, o.X1), Math.Min(Y0, o.Y0), Math.Max(Y1, o.Y1));
    }

    readonly record struct Matrix(double Sx, double R0, double R1, double Sy, double Tx, double Ty)
    {
        public static readonly Matrix Identity = new(1, 0, 0, 1, 0, 0);
        public (double X, double Y) Apply(double x, double y) => (Sx * x + R1 * y + Tx, R0 * x + Sy * y + Ty);
        /// <summary>This, then <paramref name="b"/>.</summary>
        public Matrix Then(Matrix b) => new(b.Sx * Sx + b.R1 * R0, b.R0 * Sx + b.Sy * R0, b.Sx * R1 + b.R1 * Sy, b.R0 * R1 + b.Sy * Sy, b.Sx * Tx + b.R1 * Ty + b.Tx, b.R0 * Tx + b.Sy * Ty + b.Ty);
    }

    /// <summary>What a sprite shows on its first frame, and where, in twips; null when nothing.</summary>
    Box? Bounds(int id, Matrix m, int depth)
    {
        if (depth > 12 || !_defs.TryGetValue(id, out var t)) return null;
        if (ShapeCodes.Contains(t.Code))
        {
            var r = new BitReader(_body, t.Offset + 2);
            var (x0, x1, y0, y1) = r.Rect();
            Box? box = null;
            foreach (var (x, y) in new[] { (x0, y0), (x1, y0), (x0, y1), (x1, y1) })
            {
                var (px, py) = m.Apply(x, y);
                var p = new Box(px, px, py, py);
                box = box == null ? p : box.Value.Union(p);
            }
            return box;
        }
        if (t.Code != 39) return null;
        var placed = new Dictionary<int, (int Char, Matrix M)>();
        foreach (var c in ReadTags(t.Offset + 4, t.Offset + t.Length))
        {
            if (c.Code == 1) break;
            if (Place(c) is { } p)
            {
                if (p.Char >= 0) placed[p.Depth] = (p.Char, p.M ?? Matrix.Identity);
                else if (p.M is { } mm && placed.TryGetValue(p.Depth, out var old)) placed[p.Depth] = (old.Char, mm);
            }
        }
        Box? all = null;
        foreach (var (ch, mm) in placed.Values)
            if (Bounds(ch, mm.Then(m), depth + 1) is { } b) all = all == null ? b : all.Value.Union(b);
        return all;
    }

    /// <summary>A PlaceObject2/3 tag's depth, character (-1 when it only moves one) and matrix.</summary>
    (int Depth, int Char, Matrix? M)? Place(Tag c)
    {
        if (c.Code is not (26 or 70)) return null;
        int q = c.Offset;
        byte f = _body[q++], f2 = 0;
        if (c.Code == 70) f2 = _body[q++];
        int depth = U16(q); q += 2;
        if (c.Code == 70 && ((f2 & 0x08) != 0 || ((f2 & 0x10) != 0 && (f & 0x02) != 0))) q = Array.IndexOf(_body, (byte)0, q) + 1;
        int ch = -1;
        if ((f & 0x02) != 0) { ch = U16(q); q += 2; }
        Matrix? m = null;
        if ((f & 0x04) != 0) { var r = new BitReader(_body, q); m = r.Matrix(); }
        return (depth, ch, m);
    }

    /// <summary>Every shape a sprite draws, on any frame, at any depth.</summary>
    HashSet<int> ShapesOf(int id)
    {
        var found = new HashSet<int>();
        var seen = new HashSet<int>();
        void Walk(int cid)
        {
            if (!seen.Add(cid) || !_defs.TryGetValue(cid, out var t)) return;
            if (ShapeCodes.Contains(t.Code)) { found.Add(cid); return; }
            if (t.Code != 39) return;
            foreach (var c in ReadTags(t.Offset + 4, t.Offset + t.Length))
                if (Place(c) is { Char: >= 0 } p) Walk(p.Char);
        }
        Walk(id);
        return found;
    }

    // ───────── pictures ─────────

    /// <summary>
    /// Puts pictures in place of parts (by name). Each picture fills the part's space on its first frame, keeping its
    /// own shape (centred) unless <paramref name="stretch"/>; the part keeps its name and frame count, so the game
    /// animates it exactly as before. Returns how many parts changed.
    /// </summary>
    public int ReplaceParts(IReadOnlyDictionary<string, SwfImage> pictures, bool stretch = false)
    {
        var plan = new Dictionary<int, (SwfImage Img, Box Box)>();
        foreach (var (name, img) in pictures)
        {
            if (img == null || img.Width <= 0 || img.Height <= 0 || !_symbols.TryGetValue(name, out var id)) continue;
            if (!_defs.TryGetValue(id, out var t) || t.Code != 39) continue;
            if (Bounds(id, Matrix.Identity, 0) is not { } box || box.X1 - box.X0 < 1 || box.Y1 - box.Y0 < 1) continue;
            plan[id] = (img, box);
        }
        if (plan.Count == 0) return 0;

        int next = _defs.Keys.DefaultIfEmpty(0).Max() + 1;
        var output = new MemoryStream();
        output.Write(_body, 0, _tagsStart);
        foreach (var t in _tags)
        {
            if (t.Code == 39 && plan.TryGetValue(U16(t.Offset), out var job))
            {
                int spriteId = U16(t.Offset), frames = U16(t.Offset + 2);
                int bmpId = next++, shapeId = next++;
                WriteTag(output, 36, BitmapTag(bmpId, job.Img));
                WriteTag(output, 32, ShapeTag(shapeId, bmpId, job.Img, job.Box, stretch));
                WriteTag(output, 39, SpriteTag(spriteId, Math.Max(1, frames), shapeId));
                continue;
            }
            WriteTag(output, t.Code, _body.AsSpan(t.Offset, t.Length));
        }
        _body = output.ToArray();
        Index();
        return plan.Count;
    }

    static void WriteTag(Stream s, int code, ReadOnlySpan<byte> data)
    {
        // Long headers for anything 63 bytes or more, and always for bitmaps (some players expect it).
        bool longForm = data.Length >= 63 || code is 6 or 20 or 21 or 35 or 36 or 90;
        if (longForm)
        {
            s.Write(BitConverter.GetBytes((ushort)((code << 6) | 63)));
            s.Write(BitConverter.GetBytes(data.Length));
        }
        else s.Write(BitConverter.GetBytes((ushort)((code << 6) | data.Length)));
        s.Write(data);
    }

    /// <summary>DefineBitsLossless2: premultiplied ARGB, zlib-compressed.</summary>
    static byte[] BitmapTag(int id, SwfImage img)
    {
        var argb = new byte[img.Width * img.Height * 4];
        for (int i = 0, n = img.Width * img.Height; i < n; i++)
        {
            byte r = img.Rgba[i * 4], g = img.Rgba[i * 4 + 1], b = img.Rgba[i * 4 + 2], a = img.Rgba[i * 4 + 3];
            argb[i * 4] = a;
            argb[i * 4 + 1] = (byte)(r * a / 255);
            argb[i * 4 + 2] = (byte)(g * a / 255);
            argb[i * 4 + 3] = (byte)(b * a / 255);
        }
        using var ms = new MemoryStream();
        ms.Write(BitConverter.GetBytes((ushort)id));
        ms.WriteByte(5); // 32-bit ARGB
        ms.Write(BitConverter.GetBytes((ushort)img.Width));
        ms.Write(BitConverter.GetBytes((ushort)img.Height));
        using (var z = new ZLibStream(ms, CompressionLevel.Optimal, leaveOpen: true)) z.Write(argb);
        return ms.ToArray();
    }

    /// <summary>DefineShape3: a rectangle filled with the picture (clipped bitmap fill), placed over the part's space.</summary>
    static byte[] ShapeTag(int id, int bmpId, SwfImage img, Box box, bool stretch)
    {
        double bw = box.X1 - box.X0, bh = box.Y1 - box.Y0;
        double dw = bw, dh = bh;
        if (!stretch) { double k = Math.Min(bw / img.Width, bh / img.Height); dw = img.Width * k; dh = img.Height * k; }
        double ox = box.X0 + (bw - dw) / 2, oy = box.Y0 + (bh - dh) / 2;
        int x0 = (int)Math.Round(ox), y0 = (int)Math.Round(oy), x1 = (int)Math.Round(ox + dw), y1 = (int)Math.Round(oy + dh);
        var w = new BitWriter();
        w.U(id & 0xFF, 8); w.U(id >> 8, 8);
        w.Rect(x0, x1, y0, y1);
        w.U(1, 8);              // one fill style
        w.U(0x41, 8);           // clipped bitmap
        w.U(bmpId & 0xFF, 8); w.U(bmpId >> 8, 8);
        w.Matrix(dw / img.Width, dh / img.Height, x0, y0);
        w.U(0, 8);              // no line styles
        w.U(1, 4); w.U(0, 4);   // fill bits, line bits
        // Move to the corner with fill style 1, then four edges.
        w.U(0, 1); w.U(0, 1); w.U(0, 1); w.U(0, 1); w.U(1, 1); w.U(1, 1);
        int n = BitWriter.SignedBits(x0, y0);
        w.U(n, 5); w.S(x0, n); w.S(y0, n);
        w.U(1, 1);
        void Line(int dx, int dy)
        {
            w.U(1, 1); w.U(1, 1);
            int nb = Math.Max(2, BitWriter.SignedBits(dx, dy));
            w.U(nb - 2, 4);
            if (dx != 0 && dy != 0) { w.U(1, 1); w.S(dx, nb); w.S(dy, nb); }
            else if (dx != 0) { w.U(0, 1); w.U(0, 1); w.S(dx, nb); }
            else { w.U(0, 1); w.U(1, 1); w.S(dy, nb); }
        }
        Line(x1 - x0, 0); Line(0, y1 - y0); Line(x0 - x1, 0); Line(0, y0 - y1);
        w.U(0, 6);
        return w.ToArray();
    }

    /// <summary>The part, rebuilt: our picture on every one of its frames.</summary>
    static byte[] SpriteTag(int id, int frames, int shapeId)
    {
        var ms = new MemoryStream();
        ms.Write(BitConverter.GetBytes((ushort)id));
        ms.Write(BitConverter.GetBytes((ushort)frames));
        var place = new byte[] { 0x06, 1, 0, (byte)(shapeId & 0xFF), (byte)(shapeId >> 8), 0x00 };
        WriteTag(ms, 26, place);
        for (int i = 0; i < frames; i++) WriteTag(ms, 1, ReadOnlySpan<byte>.Empty);
        WriteTag(ms, 0, ReadOnlySpan<byte>.Empty);
        return ms.ToArray();
    }

    // ───────── colors ─────────

    /// <summary>Where each color sits in the vector art (byte offsets), and whether it has an alpha byte.</summary>
    List<(int Offset, bool Alpha)> ColorSpots(IEnumerable<int> shapeIds = null)
    {
        var spots = new List<(int, bool)>();
        var only = shapeIds?.ToHashSet();
        foreach (var t in _tags)
        {
            if (!ShapeCodes.Contains(t.Code)) continue;
            if (only != null && !only.Contains(U16(t.Offset))) continue;
            try { ShapeColors(t, spots); } catch { /* a shape Pulse can't read is left alone */ }
        }
        return spots;
    }

    void ShapeColors(Tag t, List<(int, bool)> spots)
    {
        int version = t.Code switch { 2 => 1, 22 => 2, 32 => 3, _ => 4 };
        bool alpha = version >= 3;
        var r = new BitReader(_body, t.Offset + 2);
        r.Rect();
        if (version == 4) { r.Rect(); r.Align(); r.Skip(8); }
        int end = t.Offset + t.Length;

        void FillStyles()
        {
            r.Align();
            int n = r.U(8);
            if (n == 0xFF && version >= 2) n = r.U(8) | (r.U(8) << 8);
            for (int i = 0; i < n; i++) FillStyle();
        }
        void FillStyle()
        {
            r.Align();
            int type = r.U(8);
            if (type == 0x00) { spots.Add((r.BytePos, alpha)); r.Skip(alpha ? 32 : 24); }
            else if (type is 0x10 or 0x12 or 0x13)
            {
                r.Matrix();
                int count = r.U(8) & 0x0F;
                for (int g = 0; g < count; g++) { r.Skip(8); spots.Add((r.BytePos, alpha)); r.Skip(alpha ? 32 : 24); }
                if (type == 0x13) r.Skip(16);
            }
            else if (type is >= 0x40 and <= 0x43) { r.Skip(16); r.Matrix(); }
            else throw new InvalidDataException("Unknown fill.");
        }
        void LineStyles()
        {
            r.Align();
            int n = r.U(8);
            if (n == 0xFF && version >= 2) n = r.U(8) | (r.U(8) << 8);
            for (int i = 0; i < n; i++)
            {
                r.Skip(16); // width
                if (version < 4) { spots.Add((r.BytePos, alpha)); r.Skip(alpha ? 32 : 24); continue; }
                r.U(2); int join = r.U(2); int hasFill = r.U(1); r.U(3); r.U(5); r.U(1); r.U(2);
                if (join == 2) r.Skip(16);
                if (hasFill == 1) FillStyle();
                else { spots.Add((r.BytePos, true)); r.Skip(32); }
            }
        }

        FillStyles();
        LineStyles();
        int fillBits = r.U(4), lineBits = r.U(4);
        while (r.BytePos < end)
        {
            if (r.U(1) == 0)
            {
                int flags = r.U(5);
                if (flags == 0) break;
                if ((flags & 1) != 0) { int nb = r.U(5); r.U(nb); r.U(nb); }
                if ((flags & 2) != 0) r.U(fillBits);
                if ((flags & 4) != 0) r.U(fillBits);
                if ((flags & 8) != 0) r.U(lineBits);
                if ((flags & 16) != 0)
                {
                    FillStyles(); LineStyles();
                    fillBits = r.U(4); lineBits = r.U(4);
                }
            }
            else
            {
                int straight = r.U(1), nb = r.U(4) + 2;
                if (straight == 1)
                {
                    if (r.U(1) == 1) { r.U(nb); r.U(nb); }
                    else { r.U(1); r.U(nb); }
                }
                else { r.U(nb); r.U(nb); r.U(nb); r.U(nb); }
            }
        }
    }

    /// <summary>The colors used (0xRRGGBB), most used first. Only in the named parts when given.</summary>
    public List<(int Rgb, int Count)> Palette(IEnumerable<string> parts = null)
    {
        var spots = ColorSpots(ShapeScope(parts));
        return spots.GroupBy(s => (_body[s.Offset] << 16) | (_body[s.Offset + 1] << 8) | _body[s.Offset + 2])
            .Select(g => (g.Key, g.Count())).OrderByDescending(x => x.Item2).ToList();
    }

    /// <summary>
    /// Changes colors: <paramref name="map"/> gets each color (0xRRGGBB) and returns the new one. Only in the named
    /// parts when given, so effects in a file that also holds body parts change alone. Returns how many changed.
    /// </summary>
    public int Recolor(Func<int, int> map, IEnumerable<string> parts = null)
    {
        int changed = 0;
        foreach (var (at, _) in ColorSpots(ShapeScope(parts)))
        {
            int rgb = (_body[at] << 16) | (_body[at + 1] << 8) | _body[at + 2];
            int to = map(rgb) & 0xFFFFFF;
            if (to == rgb) continue;
            _body[at] = (byte)(to >> 16); _body[at + 1] = (byte)(to >> 8); _body[at + 2] = (byte)to;
            changed++;
        }
        return changed;
    }

    HashSet<int> ShapeScope(IEnumerable<string> parts)
    {
        if (parts == null) return null;
        var set = new HashSet<int>();
        foreach (var p in parts)
            if (_symbols.TryGetValue(p, out var id)) set.UnionWith(ShapesOf(id));
        return set;
    }

    // ───────── bits ─────────

    sealed class BitReader
    {
        readonly byte[] _b; int _p;
        public BitReader(byte[] b, int bytePos) { _b = b; _p = bytePos * 8; }
        public int BytePos => (_p + 7) >> 3;
        public void Align() => _p = (_p + 7) & ~7;
        public void Skip(int bits) => _p += bits;
        public int U(int n)
        {
            int v = 0;
            for (int i = 0; i < n; i++) { v = (v << 1) | ((_b[_p >> 3] >> (7 - (_p & 7))) & 1); _p++; }
            return v;
        }
        public int S(int n)
        {
            if (n == 0) return 0;
            int v = U(n);
            return (v & (1 << (n - 1))) != 0 ? v - (1 << n) : v;
        }
        public (int X0, int X1, int Y0, int Y1) Rect()
        {
            Align();
            int n = U(5);
            var r = (S(n), S(n), S(n), S(n));
            Align();
            return r;
        }
        public Matrix Matrix()
        {
            Align();
            double sx = 1, sy = 1, r0 = 0, r1 = 0;
            if (U(1) == 1) { int n = U(5); sx = S(n) / 65536.0; sy = S(n) / 65536.0; }
            if (U(1) == 1) { int n = U(5); r0 = S(n) / 65536.0; r1 = S(n) / 65536.0; }
            int t = U(5);
            int tx = S(t), ty = S(t);
            Align();
            return new Matrix(sx, r0, r1, sy, tx, ty);
        }
    }

    sealed class BitWriter
    {
        readonly List<byte> _out = new();
        int _acc, _n;
        public void U(int v, int n)
        {
            for (int i = n - 1; i >= 0; i--)
            {
                _acc = (_acc << 1) | ((v >> i) & 1);
                if (++_n == 8) { _out.Add((byte)_acc); _acc = 0; _n = 0; }
            }
        }
        public void S(int v, int n) => U(v & ((1 << n) - 1), n);
        public void Align() { if (_n > 0) { _out.Add((byte)(_acc << (8 - _n))); _acc = 0; _n = 0; } }
        public static int SignedBits(params int[] values)
        {
            int n = 1;
            foreach (var v in values) while (v < -(1 << (n - 1)) || v >= (1 << (n - 1))) n++;
            return n;
        }
        public void Rect(int x0, int x1, int y0, int y1)
        {
            Align();
            int n = SignedBits(x0, x1, y0, y1);
            U(n, 5); S(x0, n); S(x1, n); S(y0, n); S(y1, n);
            Align();
        }
        public void Matrix(double sx, double sy, int tx, int ty)
        {
            Align();
            int a = (int)Math.Round(sx * 65536), b = (int)Math.Round(sy * 65536);
            U(1, 1); int n = SignedBits(a, b); U(n, 5); S(a, n); S(b, n);
            U(0, 1);
            int t = SignedBits(tx, ty); U(t, 5); S(tx, t); S(ty, t);
            Align();
        }
        public byte[] ToArray() { Align(); return _out.ToArray(); }
    }
}
