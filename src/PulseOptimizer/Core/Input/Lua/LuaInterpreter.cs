using System.Globalization;
using System.Text;

namespace Pulse.Core.Input.Lua;

/// <summary>A Lua table: hash part for everything, with number keys normalised so 1 and 1.0 are the same key.</summary>
public sealed class LuaTable
{
    readonly Dictionary<object, object> _map = new();
    int _border; // a known length hint for #

    public object Get(object key) => key != null && _map.TryGetValue(Norm(key), out var v) ? v : null;

    public void Set(object key, object value)
    {
        if (key == null) throw new LuaException("table index is nil");
        if (key is double d && double.IsNaN(d)) throw new LuaException("table index is NaN");
        key = Norm(key);
        if (value == null) _map.Remove(key); else _map[key] = value;
    }

    public int Length
    {
        get
        {
            int n = _border;
            while (n > 0 && !_map.ContainsKey((double)n)) n--;
            while (_map.ContainsKey((double)(n + 1))) n++;
            _border = n;
            return n;
        }
    }

    public IEnumerable<KeyValuePair<object, object>> Pairs => _map.ToList();

    /// <summary>The key after <paramref name="key"/> (for next/pairs), array part first.</summary>
    public (object Key, object Value) Next(object key)
    {
        var keys = _map.Keys.ToList();
        int i = key == null ? 0 : keys.IndexOf(Norm(key)) + 1;
        if (i <= 0 && key != null) return (null, null);
        return i < keys.Count ? (keys[i], _map[keys[i]]) : (null, null);
    }

    static object Norm(object k) => k is int i ? (double)i : k is long l ? (double)l : k;
}

/// <summary>Anything callable from Lua.</summary>
public abstract class LuaFunction
{
    public abstract object[] Call(LuaInterpreter vm, object[] args);
}

public sealed class Builtin : LuaFunction
{
    readonly Func<object[], object[]> _f;
    public readonly string Name;
    public Builtin(string name, Func<object[], object[]> f) { Name = name; _f = f; }
    public override object[] Call(LuaInterpreter vm, object[] args) => _f(args);
}

sealed class Closure : LuaFunction
{
    public readonly FuncE Def;
    public readonly Scope Env;
    public Closure(FuncE def, Scope env) { Def = def; Env = env; }

    public override object[] Call(LuaInterpreter vm, object[] args)
    {
        var scope = new Scope(Env);
        for (int i = 0; i < Def.Params.Count; i++) scope.Declare(Def.Params[i], i < args.Length ? args[i] : null);
        if (Def.Vararg) scope.Varargs = args.Length > Def.Params.Count ? args[Def.Params.Count..] : Array.Empty<object>();
        if (++vm.Depth > 180) { vm.Depth--; throw new LuaException("stack overflow (a function calls itself without stopping)", Def.Line); }
        try { vm.ExecBlock(Def.Body, scope); return Array.Empty<object>(); }
        catch (ReturnSignal r) { return r.Values; }
        finally { vm.Depth--; }
    }
}

sealed class Ref { public object Value; }

sealed class Scope
{
    readonly Dictionary<string, Ref> _vars = new();
    public readonly Scope Parent;
    public object[] Varargs;
    public Scope(Scope parent) { Parent = parent; Varargs = parent?.Varargs; }
    public void Declare(string name, object value) => _vars[name] = new Ref { Value = value };
    public Ref Find(string name)
    {
        for (var s = this; s != null; s = s.Parent) if (s._vars.TryGetValue(name, out var r)) return r;
        return null;
    }
}

sealed class BreakSignal : Exception { }
sealed class ReturnSignal : Exception { public readonly object[] Values; public ReturnSignal(object[] v) { Values = v; } }

/// <summary>Thrown inside a running script when it's stopped (Esc, macro turned off). Never shown as an error.</summary>
public sealed class LuaStopped : Exception { }

/// <summary>
/// Runs Lua 5.1 scripts by walking the syntax tree. Small and safe: no file, OS or network access, only what's
/// registered in <see cref="Globals"/>. Stops at once when <see cref="Stop"/> is called (checked on every statement).
/// </summary>
public sealed class LuaInterpreter
{
    public readonly LuaTable Globals = new();
    public int Depth;
    volatile bool _stop;
    readonly Random _rng = new();
    /// <summary>Where print() and errors go (the macro's log).</summary>
    public Action<string> Log = _ => { };

    public LuaInterpreter() { OpenLibraries(); }

    public void Stop() => _stop = true;
    public bool Stopped => _stop;

    public void CheckStop() { if (_stop) throw new LuaStopped(); }

    /// <summary>Runs a whole script (defining its functions). Throws LuaException with the line on errors.</summary>
    public void Run(string source)
    {
        var block = LuaParser.Parse(source);
        try { ExecBlock(block, new Scope(null) { Varargs = Array.Empty<object>() }); }
        catch (ReturnSignal) { }
        catch (BreakSignal) { throw new LuaException("'break' outside a loop"); }
    }

    /// <summary>Calls a global function if the script defined it (like OnEvent). Returns false if it doesn't exist.</summary>
    public bool CallGlobal(string name, params object[] args)
    {
        if (Globals.Get(name) is not LuaFunction f) return false;
        try { f.Call(this, args); }
        catch (BreakSignal) { throw new LuaException("'break' outside a loop"); }
        return true;
    }

    public void Register(string name, Func<object[], object[]> f) => Globals.Set(name, new Builtin(name, f));

    // ───────── statements ─────────

    internal void ExecBlock(List<Stat> block, Scope parent)
    {
        var scope = new Scope(parent);
        foreach (var s in block) Exec(s, scope);
    }

    void Exec(Stat s, Scope sc)
    {
        if (_stop) throw new LuaStopped();
        switch (s)
        {
            case LocalS l:
            {
                var vals = EvalList(l.Values, sc, l.Names.Count);
                for (int i = 0; i < l.Names.Count; i++) sc.Declare(l.Names[i], vals[i]);
                break;
            }
            case LocalFuncS lf:
            {
                sc.Declare(lf.Name, null);
                sc.Find(lf.Name).Value = new Closure(lf.Func, sc);
                break;
            }
            case AssignS a:
            {
                var vals = EvalList(a.Values, sc, a.Targets.Count);
                for (int i = 0; i < a.Targets.Count; i++) AssignTo(a.Targets[i], vals[i], sc);
                break;
            }
            case CallS c: EvalMulti(c.Call, sc); break;
            case IfS f:
            {
                foreach (var (cond, body) in f.Branches)
                    if (Truthy(Eval(cond, sc))) { ExecBlock(body, sc); return; }
                if (f.Else != null) ExecBlock(f.Else, sc);
                break;
            }
            case WhileS w:
                try { while (Truthy(Eval(w.Cond, sc))) { CheckStop(); ExecBlock(w.Body, sc); } }
                catch (BreakSignal) { }
                break;
            case RepeatS r:
                try
                {
                    while (true)
                    {
                        CheckStop();
                        // "until" sees the body's locals
                        var inner = new Scope(sc);
                        foreach (var st in r.Body) Exec(st, inner);
                        if (Truthy(Eval(r.Cond, inner))) break;
                    }
                }
                catch (BreakSignal) { }
                break;
            case NumForS n:
            {
                double start = ToNum(Eval(n.Start, sc), n.Line, "'for' initial value"),
                       stop = ToNum(Eval(n.Stop, sc), n.Line, "'for' limit"),
                       step = ToNum(Eval(n.Step, sc), n.Line, "'for' step");
                if (step == 0) throw new LuaException("'for' step is zero", n.Line);
                try
                {
                    for (double v = start; step > 0 ? v <= stop : v >= stop; v += step)
                    {
                        CheckStop();
                        var inner = new Scope(sc);
                        inner.Declare(n.Var, v);
                        foreach (var st in n.Body) Exec(st, inner);
                    }
                }
                catch (BreakSignal) { }
                break;
            }
            case GenForS g:
            {
                var init = EvalList(g.Exprs, sc, 3);
                var f = init[0]; var state = init[1]; var control = init[2];
                try
                {
                    while (true)
                    {
                        CheckStop();
                        var res = CallValue(f, new[] { state, control }, g.Line);
                        var first = res.Length > 0 ? res[0] : null;
                        if (first == null) break;
                        control = first;
                        var inner = new Scope(sc);
                        for (int i = 0; i < g.Vars.Count; i++) inner.Declare(g.Vars[i], i < res.Length ? res[i] : null);
                        foreach (var st in g.Body) Exec(st, inner);
                    }
                }
                catch (BreakSignal) { }
                break;
            }
            case DoS d: ExecBlock(d.Body, sc); break;
            case ReturnS rt: throw new ReturnSignal(EvalList(rt.Values, sc, -1));
            case BreakS: throw new BreakSignal();
        }
    }

    void AssignTo(Expr target, object value, Scope sc)
    {
        if (target is NameE n)
        {
            var r = sc.Find(n.Name);
            if (r != null) r.Value = value; else Globals.Set(n.Name, value);
        }
        else if (target is IndexE ix)
        {
            var obj = Eval(ix.Obj, sc);
            if (obj is not LuaTable t) throw new LuaException($"attempt to index a {TypeName(obj)} value", ix.Line);
            t.Set(Eval(ix.Key, sc), value);
        }
    }

    // ───────── expressions ─────────

    /// <summary>Evaluates a list; the last expression expands to all its values. want = -1 keeps everything.</summary>
    object[] EvalList(List<Expr> exprs, Scope sc, int want)
    {
        var vals = new List<object>();
        for (int i = 0; i < exprs.Count; i++)
        {
            if (i == exprs.Count - 1) vals.AddRange(EvalMulti(exprs[i], sc));
            else vals.Add(Eval(exprs[i], sc));
        }
        if (want < 0) return vals.ToArray();
        while (vals.Count < want) vals.Add(null);
        return vals.Take(Math.Max(want, 0)).ToArray();
    }

    object[] EvalMulti(Expr e, Scope sc)
    {
        switch (e)
        {
            case CallE c: return CallValue(Eval(c.Fn, sc), EvalList(c.Args, sc, -1), c.Line, c.Fn);
            case MethodE m:
            {
                var obj = Eval(m.Obj, sc);
                object fn = obj is string ? (Globals.Get("string") as LuaTable)?.Get(m.Name) : obj is LuaTable t ? t.Get(m.Name) : null;
                if (fn == null) throw new LuaException($"attempt to call method '{m.Name}' (a nil value)", m.Line);
                var args = new List<object> { obj };
                args.AddRange(EvalList(m.Args, sc, -1));
                return CallValue(fn, args.ToArray(), m.Line);
            }
            case VarargE: return sc.Varargs ?? Array.Empty<object>();
            default: return new[] { Eval(e, sc) };
        }
    }

    internal object[] CallValue(object fn, object[] args, int line, Expr what = null)
    {
        if (fn is LuaFunction f)
        {
            try { return f.Call(this, args); }
            catch (LuaException ex) when (ex.Line == 0 && fn is Builtin b) { throw new LuaException(b.Name == "error" ? ex.Message : $"{b.Name}: {ex.Message}", line); }
        }
        string name = what is NameE n ? $" '{n.Name}'" : what is IndexE { Key: StrE k } ? $" '{k.Value}'" : "";
        throw new LuaException($"attempt to call{name} (a {TypeName(fn)} value)", line);
    }

    object Eval(Expr e, Scope sc)
    {
        switch (e)
        {
            case NilE: return null;
            case BoolE b: return b.Value;
            case NumE n: return n.Value;
            case StrE s: return s.Value;
            case VarargE: var va = sc.Varargs; return va != null && va.Length > 0 ? va[0] : null;
            case ParenE p: return Eval(p.Inner, sc);
            case NameE n: { var r = sc.Find(n.Name); return r != null ? r.Value : Globals.Get(n.Name); }
            case IndexE ix:
            {
                var obj = Eval(ix.Obj, sc);
                var key = Eval(ix.Key, sc);
                if (obj is LuaTable t) return t.Get(key);
                if (obj is string && key is string sk) return (Globals.Get("string") as LuaTable)?.Get(sk);
                string what = ix.Obj is NameE nm ? $" '{nm.Name}'" : "";
                throw new LuaException($"attempt to index{what} (a {TypeName(obj)} value)", ix.Line);
            }
            case CallE or MethodE: { var r = EvalMulti(e, sc); return r.Length > 0 ? r[0] : null; }
            case FuncE f: return new Closure(f, sc);
            case TableE t:
            {
                var table = new LuaTable();
                int n = 1;
                for (int i = 0; i < t.Fields.Count; i++)
                {
                    var (k, v) = t.Fields[i];
                    if (k != null) { table.Set(Eval(k, sc), Eval(v, sc)); continue; }
                    if (i == t.Fields.Count - 1) foreach (var x in EvalMulti(v, sc)) table.Set((double)n++, x);
                    else table.Set((double)n++, Eval(v, sc));
                }
                return table;
            }
            case UnE u:
            {
                var a = Eval(u.A, sc);
                return u.Op switch
                {
                    Tok.Not => !Truthy(a),
                    Tok.Minus => -ToNum(a, u.Line, "arithmetic on"),
                    Tok.Hash => a is string s ? (double)s.Length : a is LuaTable tb ? tb.Length : throw new LuaException($"attempt to get length of a {TypeName(a)} value", u.Line),
                    Tok.Tilde => (double)~ToInt(a, u.Line),
                    _ => null,
                };
            }
            case BinE b:
            {
                if (b.Op == Tok.And) { var l = Eval(b.A, sc); return Truthy(l) ? Eval(b.B, sc) : l; }
                if (b.Op == Tok.Or) { var l = Eval(b.A, sc); return Truthy(l) ? l : Eval(b.B, sc); }
                var x = Eval(b.A, sc);
                var y = Eval(b.B, sc);
                switch (b.Op)
                {
                    case Tok.Eq: return RawEquals(x, y);
                    case Tok.Ne: return !RawEquals(x, y);
                    case Tok.Lt: return Less(x, y, b.Line);
                    case Tok.Gt: return Less(y, x, b.Line);
                    case Tok.Le: return !Less(y, x, b.Line);
                    case Tok.Ge: return !Less(x, y, b.Line);
                    case Tok.Concat:
                        if ((x is string || x is double) && (y is string || y is double)) return ToStr(x) + ToStr(y);
                        throw new LuaException($"attempt to concatenate a {TypeName(x is string or double ? y : x)} value", b.Line);
                }
                double p = ToNum(x, b.Line, "arithmetic on"), q = ToNum(y, b.Line, "arithmetic on");
                return b.Op switch
                {
                    Tok.Plus => p + q,
                    Tok.Minus => p - q,
                    Tok.Star => p * q,
                    Tok.Slash => p / q,
                    Tok.DSlash => Math.Floor(p / q),
                    Tok.Percent => p - Math.Floor(p / q) * q,
                    Tok.Caret => Math.Pow(p, q),
                    Tok.Amp => (double)(ToInt(x, b.Line) & ToInt(y, b.Line)),
                    Tok.Pipe => (double)(ToInt(x, b.Line) | ToInt(y, b.Line)),
                    Tok.Tilde => (double)(ToInt(x, b.Line) ^ ToInt(y, b.Line)),
                    Tok.Shl => (double)(ToInt(x, b.Line) << (int)ToInt(y, b.Line)),
                    Tok.Shr => (double)(long)((ulong)ToInt(x, b.Line) >> (int)ToInt(y, b.Line)),
                    _ => null,
                };
            }
        }
        throw new LuaException("can't evaluate this", e.Line);
    }

    // ───────── value helpers ─────────

    public static bool Truthy(object v) => v != null && !(v is bool b && !b);

    public static bool RawEquals(object a, object b)
    {
        if (a == null || b == null) return a == b;
        if (a is double x && b is double y) return x == y;
        if (a is string s && b is string t) return s == t;
        if (a is bool p && b is bool q) return p == q;
        return ReferenceEquals(a, b);
    }

    static bool Less(object a, object b, int line)
    {
        if (a is double x && b is double y) return x < y;
        if (a is string s && b is string t) return string.CompareOrdinal(s, t) < 0;
        throw new LuaException($"attempt to compare {TypeName(a)} with {TypeName(b)}", line);
    }

    public static string TypeName(object v) => v switch
    {
        null => "nil", bool => "boolean", double => "number", string => "string", LuaTable => "table", LuaFunction => "function", _ => "userdata",
    };

    public static double? TryNum(object v)
    {
        if (v is double d) return d;
        if (v is string s)
        {
            s = s.Trim();
            if (s.StartsWith("0x", StringComparison.OrdinalIgnoreCase) && long.TryParse(s[2..], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var h)) return h;
            if (double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var r)) return r;
        }
        return null;
    }

    static double ToNum(object v, int line, string what) =>
        TryNum(v) ?? throw new LuaException($"attempt to perform {what} a {TypeName(v)} value", line);

    static long ToInt(object v, int line)
    {
        double d = ToNum(v, line, "bitwise operation on");
        if (d != Math.Floor(d)) throw new LuaException("number has no integer representation", line);
        return (long)d;
    }

    public static string ToStr(object v) => v switch
    {
        null => "nil",
        bool b => b ? "true" : "false",
        double d => FormatNumber(d),
        string s => s,
        LuaTable t => $"table: 0x{t.GetHashCode():x8}",
        LuaFunction f => $"function: 0x{f.GetHashCode():x8}",
        _ => v.ToString(),
    };

    public static string FormatNumber(double d)
    {
        if (double.IsNaN(d)) return "nan";
        if (double.IsInfinity(d)) return d > 0 ? "inf" : "-inf";
        if (d == Math.Floor(d) && Math.Abs(d) < 1e15) return ((long)d).ToString(CultureInfo.InvariantCulture);
        return d.ToString("G14", CultureInfo.InvariantCulture);
    }

    static double Arg(object[] a, int i, string fn)
    {
        var v = i < a.Length ? a[i] : null;
        return TryNum(v) ?? throw new LuaException($"bad argument #{i + 1} to '{fn}' (number expected, got {TypeName(v)})");
    }

    static string SArg(object[] a, int i, string fn)
    {
        var v = i < a.Length ? a[i] : null;
        if (v is string s) return s;
        if (v is double) return ToStr(v);
        throw new LuaException($"bad argument #{i + 1} to '{fn}' (string expected, got {TypeName(v)})");
    }

    static object[] One(object v) => new[] { v };
    static readonly object[] None = Array.Empty<object>();

    // ───────── standard library (the safe parts) ─────────

    void OpenLibraries()
    {
        Register("print", a => { Log(string.Join("\t", a.Select(ToStr))); return None; });
        Register("type", a => One(TypeName(a.Length > 0 ? a[0] : null)));
        Register("tostring", a => One(ToStr(a.Length > 0 ? a[0] : null)));
        Register("tonumber", a =>
        {
            if (a.Length > 1 && a[1] != null)
            {
                int b = (int)Arg(a, 1, "tonumber");
                try { return One((double)Convert.ToInt64(SArg(a, 0, "tonumber").Trim(), b)); } catch { return One(null); }
            }
            return One(a.Length > 0 ? TryNum(a[0]) is double d ? d : null : null);
        });
        Register("error", a => throw new LuaException(a.Length > 0 ? ToStr(a[0]) : "error"));
        Register("assert", a =>
        {
            if (a.Length == 0 || !Truthy(a[0])) throw new LuaException(a.Length > 1 ? ToStr(a[1]) : "assertion failed!");
            return a;
        });
        Register("pcall", a =>
        {
            if (a.Length == 0) throw new LuaException("bad argument #1 to 'pcall' (value expected)");
            try { return new object[] { true }.Concat(CallValue(a[0], a[1..], 0)).ToArray(); }
            catch (LuaStopped) { throw; }
            catch (LuaException ex) { return new object[] { false, ex.Message }; }
        });
        Register("select", a =>
        {
            if (a.Length > 0 && a[0] is string s && s == "#") return One((double)(a.Length - 1));
            int n = (int)Arg(a, 0, "select");
            if (n < 0) n = a.Length + n; else if (n == 0) throw new LuaException("bad argument #1 to 'select' (index out of range)");
            return n < a.Length ? a[n..] : None;
        });
        Register("next", a =>
        {
            if (a.Length == 0 || a[0] is not LuaTable t) throw new LuaException("bad argument #1 to 'next' (table expected)");
            var (k, v) = t.Next(a.Length > 1 ? a[1] : null);
            return k == null ? One(null) : new[] { k, v };
        });
        var next = Globals.Get("next");
        Register("pairs", a =>
        {
            if (a.Length == 0 || a[0] is not LuaTable t) throw new LuaException($"bad argument #1 to 'pairs' (table expected, got {TypeName(a.Length > 0 ? a[0] : null)})");
            return new[] { next, t, null };
        });
        var ipairsIter = new Builtin("ipairs", a =>
        {
            var t = (LuaTable)a[0];
            double i = (double)a[1] + 1;
            var v = t.Get(i);
            return v == null ? One(null) : new[] { (object)i, v };
        });
        Register("ipairs", a =>
        {
            if (a.Length == 0 || a[0] is not LuaTable t) throw new LuaException($"bad argument #1 to 'ipairs' (table expected, got {TypeName(a.Length > 0 ? a[0] : null)})");
            return new object[] { ipairsIter, t, 0.0 };
        });
        Register("rawget", a => One(a.Length > 0 && a[0] is LuaTable t ? t.Get(a.Length > 1 ? a[1] : null) : null));
        Register("rawset", a => { if (a.Length > 0 && a[0] is LuaTable t) t.Set(a[1], a.Length > 2 ? a[2] : null); return a.Length > 0 ? One(a[0]) : None; });
        Register("rawequal", a => One(RawEquals(a.Length > 0 ? a[0] : null, a.Length > 1 ? a[1] : null)));
        Register("setmetatable", a => a.Length > 0 ? One(a[0]) : None); // metatables aren't used by G Hub scripts; accepted and ignored
        Register("getmetatable", _ => One(null));
        Func<object[], object[]> unpack = a =>
        {
            if (a.Length == 0 || a[0] is not LuaTable t) throw new LuaException("bad argument #1 to 'unpack' (table expected)");
            int from = a.Length > 1 && a[1] != null ? (int)Arg(a, 1, "unpack") : 1;
            int to = a.Length > 2 && a[2] != null ? (int)Arg(a, 2, "unpack") : t.Length;
            var r = new List<object>();
            for (int i = from; i <= to; i++) r.Add(t.Get((double)i));
            return r.ToArray();
        };
        Register("unpack", unpack);

        // math
        var math = new LuaTable();
        void M(string n, Func<object[], object[]> f) => math.Set(n, new Builtin("math." + n, f));
        math.Set("pi", Math.PI);
        math.Set("huge", double.PositiveInfinity);
        M("floor", a => One(Math.Floor(Arg(a, 0, "floor"))));
        M("ceil", a => One(Math.Ceiling(Arg(a, 0, "ceil"))));
        M("abs", a => One(Math.Abs(Arg(a, 0, "abs"))));
        M("sqrt", a => One(Math.Sqrt(Arg(a, 0, "sqrt"))));
        M("sin", a => One(Math.Sin(Arg(a, 0, "sin"))));
        M("cos", a => One(Math.Cos(Arg(a, 0, "cos"))));
        M("tan", a => One(Math.Tan(Arg(a, 0, "tan"))));
        M("exp", a => One(Math.Exp(Arg(a, 0, "exp"))));
        M("log", a => One(a.Length > 1 ? Math.Log(Arg(a, 0, "log"), Arg(a, 1, "log")) : Math.Log(Arg(a, 0, "log"))));
        M("pow", a => One(Math.Pow(Arg(a, 0, "pow"), Arg(a, 1, "pow"))));
        M("fmod", a => One(Arg(a, 0, "fmod") % Arg(a, 1, "fmod")));
        M("modf", a => { double x = Arg(a, 0, "modf"), i = Math.Truncate(x); return new object[] { i, x - i }; });
        M("min", a => One(a.Select((_, i) => Arg(a, i, "min")).Min()));
        M("max", a => One(a.Select((_, i) => Arg(a, i, "max")).Max()));
        M("random", a =>
        {
            if (a.Length == 0) return One(_rng.NextDouble());
            int lo = a.Length > 1 ? (int)Arg(a, 0, "random") : 1, hi = (int)Arg(a, a.Length > 1 ? 1 : 0, "random");
            if (lo > hi) throw new LuaException("bad argument to 'random' (interval is empty)");
            return One((double)_rng.Next(lo, hi + 1));
        });
        M("randomseed", _ => None); // Pulse's random numbers are already random per run
        Globals.Set("math", math);

        // string
        var str = new LuaTable();
        void S(string n, Func<object[], object[]> f) => str.Set(n, new Builtin("string." + n, f));
        S("len", a => One((double)SArg(a, 0, "len").Length));
        S("upper", a => One(SArg(a, 0, "upper").ToUpperInvariant()));
        S("lower", a => One(SArg(a, 0, "lower").ToLowerInvariant()));
        S("rep", a => One(string.Concat(Enumerable.Repeat(SArg(a, 0, "rep"), Math.Max(0, (int)Arg(a, 1, "rep"))))));
        S("reverse", a => One(new string(SArg(a, 0, "reverse").Reverse().ToArray())));
        S("sub", a =>
        {
            var s = SArg(a, 0, "sub");
            int i = a.Length > 1 ? (int)Arg(a, 1, "sub") : 1, j = a.Length > 2 && a[2] != null ? (int)Arg(a, 2, "sub") : -1;
            if (i < 0) i = Math.Max(s.Length + i + 1, 1); else if (i == 0) i = 1;
            if (j < 0) j = s.Length + j + 1; else if (j > s.Length) j = s.Length;
            return One(i > j ? "" : s.Substring(i - 1, j - i + 1));
        });
        S("byte", a =>
        {
            var s = SArg(a, 0, "byte");
            int i = a.Length > 1 ? (int)Arg(a, 1, "byte") : 1;
            return i >= 1 && i <= s.Length ? One((double)s[i - 1]) : None;
        });
        S("char", a => One(new string(a.Select((_, i) => (char)(int)Arg(a, i, "char")).ToArray())));
        S("find", a =>
        {
            var s = SArg(a, 0, "find"); var p = SArg(a, 1, "find");
            int init = a.Length > 2 && a[2] != null ? Math.Max((int)Arg(a, 2, "find"), 1) : 1;
            int at = init - 1 <= s.Length ? s.IndexOf(p, init - 1, StringComparison.Ordinal) : -1; // plain search (patterns are rare in G Hub scripts)
            return at < 0 ? One(null) : new object[] { (double)(at + 1), (double)(at + p.Length) };
        });
        S("format", a => One(Format(SArg(a, 0, "format"), a[1..])));
        Globals.Set("string", str);

        // table
        var tbl = new LuaTable();
        void T(string n, Func<object[], object[]> f) => tbl.Set(n, new Builtin("table." + n, f));
        T("insert", a =>
        {
            if (a.Length == 0 || a[0] is not LuaTable t) throw new LuaException("bad argument #1 to 'insert' (table expected)");
            if (a.Length == 2) t.Set((double)(t.Length + 1), a[1]);
            else
            {
                int pos = (int)Arg(a, 1, "insert"), n = t.Length;
                for (int i = n; i >= pos; i--) t.Set((double)(i + 1), t.Get((double)i));
                t.Set((double)pos, a[2]);
            }
            return None;
        });
        T("remove", a =>
        {
            if (a.Length == 0 || a[0] is not LuaTable t) throw new LuaException("bad argument #1 to 'remove' (table expected)");
            int n = t.Length;
            if (n == 0) return One(null);
            int pos = a.Length > 1 ? (int)Arg(a, 1, "remove") : n;
            var v = t.Get((double)pos);
            for (int i = pos; i < n; i++) t.Set((double)i, t.Get((double)(i + 1)));
            t.Set((double)n, null);
            return One(v);
        });
        T("concat", a =>
        {
            if (a.Length == 0 || a[0] is not LuaTable t) throw new LuaException("bad argument #1 to 'concat' (table expected)");
            var sep = a.Length > 1 && a[1] != null ? SArg(a, 1, "concat") : "";
            return One(string.Join(sep, Enumerable.Range(1, t.Length).Select(i => ToStr(t.Get((double)i)))));
        });
        T("unpack", unpack);
        Globals.Set("table", tbl);

        // os: only time
        var os = new LuaTable();
        os.Set("time", new Builtin("os.time", _ => One((double)DateTimeOffset.UtcNow.ToUnixTimeSeconds())));
        os.Set("clock", new Builtin("os.clock", _ => One(Environment.TickCount64 / 1000.0)));
        os.Set("date", new Builtin("os.date", a => One(DateFormat(a.Length > 0 && a[0] is string f ? f : "%c"))));
        Globals.Set("os", os);
    }

    /// <summary>C-style date formatting, as os.date and G Hub's GetDate use.</summary>
    public static string DateFormat(string f)
    {
        var now = DateTime.Now;
        var sb = new StringBuilder();
        for (int i = 0; i < f.Length; i++)
        {
            if (f[i] != '%' || i + 1 >= f.Length) { sb.Append(f[i]); continue; }
            char c = f[++i];
            sb.Append(c switch
            {
                'Y' => now.ToString("yyyy"), 'y' => now.ToString("yy"), 'm' => now.ToString("MM"), 'd' => now.ToString("dd"),
                'H' => now.ToString("HH"), 'I' => now.ToString("hh"), 'M' => now.ToString("mm"), 'S' => now.ToString("ss"),
                'p' => now.ToString("tt", CultureInfo.InvariantCulture), 'A' => now.ToString("dddd", CultureInfo.InvariantCulture),
                'a' => now.ToString("ddd", CultureInfo.InvariantCulture), 'B' => now.ToString("MMMM", CultureInfo.InvariantCulture),
                'b' => now.ToString("MMM", CultureInfo.InvariantCulture), 'c' => now.ToString("ddd MMM d HH:mm:ss yyyy", CultureInfo.InvariantCulture),
                'x' => now.ToString("MM/dd/yy"), 'X' => now.ToString("HH:mm:ss"), '%' => "%",
                _ => "%" + c,
            });
        }
        return sb.ToString();
    }

    /// <summary>string.format: %d %i %u %c %x %X %o %e %E %f %g %G %q %s %% with flags, width and precision.</summary>
    public static string Format(string fmt, object[] args)
    {
        var sb = new StringBuilder();
        int ai = 0;
        for (int i = 0; i < fmt.Length; i++)
        {
            if (fmt[i] != '%') { sb.Append(fmt[i]); continue; }
            if (++i >= fmt.Length) throw new LuaException("invalid format (ends with '%')");
            if (fmt[i] == '%') { sb.Append('%'); continue; }
            int s = i;
            while (i < fmt.Length && "-+ #0".IndexOf(fmt[i]) >= 0) i++;
            string flags = fmt[s..i];
            int ws = i;
            while (i < fmt.Length && char.IsDigit(fmt[i])) i++;
            int width = i > ws ? int.Parse(fmt[ws..i], CultureInfo.InvariantCulture) : 0;
            int prec = -1;
            if (i < fmt.Length && fmt[i] == '.')
            {
                int ps = ++i;
                while (i < fmt.Length && char.IsDigit(fmt[i])) i++;
                prec = i > ps ? int.Parse(fmt[ps..i], CultureInfo.InvariantCulture) : 0;
            }
            if (i >= fmt.Length) throw new LuaException("invalid format");
            char conv = fmt[i];
            object arg = ai < args.Length ? args[ai] : null;
            ai++;
            if (arg == null && conv != 's' && conv != 'q') throw new LuaException($"bad argument #{ai + 1} to 'format' (number expected, got no value)");
            string body = conv switch
            {
                'd' or 'i' or 'u' => Pad0(((long)Math.Truncate(TryNum(arg) ?? 0)).ToString(CultureInfo.InvariantCulture), prec),
                'c' => ((char)(int)(TryNum(arg) ?? 0)).ToString(),
                'x' => Pad0(((long)(TryNum(arg) ?? 0)).ToString("x"), prec),
                'X' => Pad0(((long)(TryNum(arg) ?? 0)).ToString("X"), prec),
                'o' => Convert.ToString((long)(TryNum(arg) ?? 0), 8),
                'f' or 'F' => (TryNum(arg) ?? 0).ToString("F" + (prec < 0 ? 6 : prec), CultureInfo.InvariantCulture),
                'e' or 'E' => (TryNum(arg) ?? 0).ToString((conv == 'e' ? "e" : "E") + (prec < 0 ? 6 : prec), CultureInfo.InvariantCulture).Replace("e+0", "e+").Replace("e-0", "e-").Replace("E+0", "E+").Replace("E-0", "E-"),
                'g' or 'G' => (TryNum(arg) ?? 0).ToString((conv == 'g' ? "g" : "G") + (prec < 0 ? 6 : Math.Max(prec, 1)), CultureInfo.InvariantCulture),
                'q' => "\"" + ToStr(arg).Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\n", "\\n") + "\"",
                's' => prec >= 0 && ToStr(arg).Length > prec ? ToStr(arg)[..prec] : ToStr(arg),
                _ => throw new LuaException($"invalid conversion '%{conv}' to 'format'"),
            };
            if ((conv is 'd' or 'i' or 'f' or 'F' or 'e' or 'E' or 'g' or 'G') && !body.StartsWith('-'))
                body = (flags.Contains('+') ? "+" : flags.Contains(' ') ? " " : "") + body;
            if (body.Length < width)
            {
                if (flags.Contains('-')) body = body.PadRight(width);
                else if (flags.Contains('0') && conv != 's' && conv != 'c')
                {
                    bool sign = body.Length > 0 && (body[0] == '-' || body[0] == '+' || body[0] == ' ');
                    body = sign ? body[0] + body[1..].PadLeft(width - 1, '0') : body.PadLeft(width, '0');
                }
                else body = body.PadLeft(width);
            }
            sb.Append(body);
        }
        return sb.ToString();

        static string Pad0(string digits, int prec)
        {
            if (prec < 0) return digits;
            bool neg = digits.StartsWith('-');
            var d = neg ? digits[1..] : digits;
            return (neg ? "-" : "") + d.PadLeft(prec, '0');
        }
    }
}
