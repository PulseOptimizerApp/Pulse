namespace Pulse.Core.Input.Lua;

// ───────── Syntax tree ─────────

public abstract record Expr(int Line);
public sealed record NilE(int Line) : Expr(Line);
public sealed record BoolE(bool Value, int Line) : Expr(Line);
public sealed record NumE(double Value, int Line) : Expr(Line);
public sealed record StrE(string Value, int Line) : Expr(Line);
public sealed record VarargE(int Line) : Expr(Line);
public sealed record NameE(string Name, int Line) : Expr(Line);
public sealed record IndexE(Expr Obj, Expr Key, int Line) : Expr(Line);
public sealed record CallE(Expr Fn, List<Expr> Args, int Line) : Expr(Line);
public sealed record MethodE(Expr Obj, string Name, List<Expr> Args, int Line) : Expr(Line);
public sealed record FuncE(List<string> Params, bool Vararg, List<Stat> Body, int Line) : Expr(Line);
public sealed record BinE(Tok Op, Expr A, Expr B, int Line) : Expr(Line);
public sealed record UnE(Tok Op, Expr A, int Line) : Expr(Line);
public sealed record ParenE(Expr Inner, int Line) : Expr(Line);
public sealed record TableE(List<(Expr Key, Expr Value)> Fields, int Line) : Expr(Line); // Key null = positional

public abstract record Stat(int Line);
public sealed record LocalS(List<string> Names, List<Expr> Values, int Line) : Stat(Line);
public sealed record AssignS(List<Expr> Targets, List<Expr> Values, int Line) : Stat(Line);
public sealed record CallS(Expr Call, int Line) : Stat(Line);
public sealed record IfS(List<(Expr Cond, List<Stat> Body)> Branches, List<Stat> Else, int Line) : Stat(Line);
public sealed record WhileS(Expr Cond, List<Stat> Body, int Line) : Stat(Line);
public sealed record RepeatS(List<Stat> Body, Expr Cond, int Line) : Stat(Line);
public sealed record NumForS(string Var, Expr Start, Expr Stop, Expr Step, List<Stat> Body, int Line) : Stat(Line);
public sealed record GenForS(List<string> Vars, List<Expr> Exprs, List<Stat> Body, int Line) : Stat(Line);
public sealed record DoS(List<Stat> Body, int Line) : Stat(Line);
public sealed record ReturnS(List<Expr> Values, int Line) : Stat(Line);
public sealed record BreakS(int Line) : Stat(Line);
public sealed record LocalFuncS(string Name, FuncE Func, int Line) : Stat(Line);

/// <summary>Recursive-descent parser for Lua 5.1 (plus a few harmless 5.3 operators).</summary>
public sealed class LuaParser
{
    readonly List<Token> _t;
    int _p;

    LuaParser(List<Token> tokens) { _t = tokens; }

    public static List<Stat> Parse(string source)
    {
        var p = new LuaParser(LuaLexer.Lex(source));
        var block = p.Block();
        if (p.Peek.Kind != Tok.Eof) throw p.Error($"'{p.Peek.Text}' not expected here");
        return block;
    }

    Token Peek => _t[_p];
    Token Next() => _t[_p++];
    bool Is(Tok k) => Peek.Kind == k;
    bool Accept(Tok k) { if (!Is(k)) return false; _p++; return true; }
    LuaException Error(string msg) => new(msg, Peek.Line);

    Token Expect(Tok k, string what)
    {
        if (!Is(k)) throw Error($"'{what}' expected near '{(Peek.Kind == Tok.Eof ? "end of script" : Peek.Text)}'");
        return Next();
    }

    static bool BlockEnd(Tok k) => k is Tok.Eof or Tok.End or Tok.Else or Tok.Elseif or Tok.Until;

    List<Stat> Block()
    {
        var list = new List<Stat>();
        while (!BlockEnd(Peek.Kind))
        {
            if (Accept(Tok.Semi)) continue;
            if (Is(Tok.Return))
            {
                int line = Next().Line;
                var vals = BlockEnd(Peek.Kind) || Is(Tok.Semi) ? new List<Expr>() : ExprList();
                Accept(Tok.Semi);
                list.Add(new ReturnS(vals, line));
                break;
            }
            list.Add(Statement());
        }
        return list;
    }

    Stat Statement()
    {
        var tok = Peek;
        int line = tok.Line;
        switch (tok.Kind)
        {
            case Tok.If:
            {
                Next();
                var branches = new List<(Expr, List<Stat>)>();
                var cond = Expression(); Expect(Tok.Then, "then");
                branches.Add((cond, Block()));
                List<Stat> els = null;
                while (true)
                {
                    if (Accept(Tok.Elseif)) { var c = Expression(); Expect(Tok.Then, "then"); branches.Add((c, Block())); }
                    else if (Accept(Tok.Else)) { els = Block(); Expect(Tok.End, "end"); break; }
                    else { Expect(Tok.End, "end"); break; }
                }
                return new IfS(branches, els, line);
            }
            case Tok.While:
            {
                Next();
                var cond = Expression(); Expect(Tok.Do, "do");
                var body = Block(); Expect(Tok.End, "end");
                return new WhileS(cond, body, line);
            }
            case Tok.Do:
            {
                Next();
                var body = Block(); Expect(Tok.End, "end");
                return new DoS(body, line);
            }
            case Tok.Repeat:
            {
                Next();
                var body = Block(); Expect(Tok.Until, "until");
                return new RepeatS(body, Expression(), line);
            }
            case Tok.For:
            {
                Next();
                var first = Expect(Tok.Name, "name").Text;
                if (Accept(Tok.Assign))
                {
                    var start = Expression(); Expect(Tok.Comma, ",");
                    var stop = Expression();
                    Expr step = Accept(Tok.Comma) ? Expression() : new NumE(1, line);
                    Expect(Tok.Do, "do");
                    var body = Block(); Expect(Tok.End, "end");
                    return new NumForS(first, start, stop, step, body, line);
                }
                var vars = new List<string> { first };
                while (Accept(Tok.Comma)) vars.Add(Expect(Tok.Name, "name").Text);
                Expect(Tok.In, "in");
                var exprs = ExprList(); Expect(Tok.Do, "do");
                var b = Block(); Expect(Tok.End, "end");
                return new GenForS(vars, exprs, b, line);
            }
            case Tok.Function:
            {
                Next();
                Expr target = new NameE(Expect(Tok.Name, "function name").Text, line);
                bool method = false;
                while (Is(Tok.Dot) || Is(Tok.Colon))
                {
                    bool colon = Next().Kind == Tok.Colon;
                    var key = Expect(Tok.Name, "name").Text;
                    target = new IndexE(target, new StrE(key, line), line);
                    if (colon) { method = true; break; }
                }
                var f = FuncBody(line, method);
                return new AssignS(new List<Expr> { target }, new List<Expr> { f }, line);
            }
            case Tok.Local:
            {
                Next();
                if (Accept(Tok.Function))
                {
                    var name = Expect(Tok.Name, "name").Text;
                    return new LocalFuncS(name, FuncBody(line, false), line);
                }
                var names = new List<string> { Expect(Tok.Name, "name").Text };
                SkipAttrib();
                while (Accept(Tok.Comma)) { names.Add(Expect(Tok.Name, "name").Text); SkipAttrib(); }
                var values = Accept(Tok.Assign) ? ExprList() : new List<Expr>();
                return new LocalS(names, values, line);
            }
            case Tok.Break:
                Next();
                return new BreakS(line);
            case Tok.Goto:
                throw Error("'goto' isn't supported in Pulse's scripts");
            default:
            {
                var e = SuffixedExpr();
                if (Is(Tok.Assign) || Is(Tok.Comma))
                {
                    var targets = new List<Expr> { e };
                    while (Accept(Tok.Comma)) targets.Add(SuffixedExpr());
                    Expect(Tok.Assign, "=");
                    foreach (var t in targets)
                        if (t is not (NameE or IndexE)) throw new LuaException("can't assign to this", t.Line);
                    return new AssignS(targets, ExprList(), line);
                }
                if (e is not (CallE or MethodE)) throw new LuaException("syntax error (a statement was expected)", line);
                return new CallS(e, line);
            }
        }
    }

    void SkipAttrib() // Lua 5.4 <const>/<close>: accepted and ignored
    {
        if (Is(Tok.Lt)) { Next(); Expect(Tok.Name, "attribute"); Expect(Tok.Gt, ">"); }
    }

    FuncE FuncBody(int line, bool method)
    {
        Expect(Tok.LParen, "(");
        var ps = new List<string>();
        if (method) ps.Add("self");
        bool vararg = false;
        if (!Is(Tok.RParen))
        {
            do
            {
                if (Accept(Tok.Dots)) { vararg = true; break; }
                ps.Add(Expect(Tok.Name, "parameter name").Text);
            } while (Accept(Tok.Comma));
        }
        Expect(Tok.RParen, ")");
        var body = Block();
        Expect(Tok.End, "end");
        return new FuncE(ps, vararg, body, line);
    }

    List<Expr> ExprList()
    {
        var list = new List<Expr> { Expression() };
        while (Accept(Tok.Comma)) list.Add(Expression());
        return list;
    }

    // Operator precedence (Lua 5.1/5.3): or < and < comparison < | < ~ < & < shift < .. (right) < + - < * / // % < unary < ^ (right)
    static (int Left, int Right)? Binary(Tok k) => k switch
    {
        Tok.Or => (1, 1),
        Tok.And => (2, 2),
        Tok.Lt or Tok.Gt or Tok.Le or Tok.Ge or Tok.Ne or Tok.Eq => (3, 3),
        Tok.Pipe => (4, 4),
        Tok.Tilde => (5, 5),
        Tok.Amp => (6, 6),
        Tok.Shl or Tok.Shr => (7, 7),
        Tok.Concat => (9, 8), // right associative
        Tok.Plus or Tok.Minus => (10, 10),
        Tok.Star or Tok.Slash or Tok.DSlash or Tok.Percent => (11, 11),
        Tok.Caret => (14, 13), // right associative, binds tighter than unary
        _ => null,
    };
    const int UnaryPriority = 12;

    Expr Expression(int limit = 0)
    {
        Expr left;
        if (Is(Tok.Not) || Is(Tok.Minus) || Is(Tok.Hash) || Is(Tok.Tilde))
        {
            var op = Next();
            left = new UnE(op.Kind, Expression(UnaryPriority), op.Line);
        }
        else left = SimpleExpr();
        while (Binary(Peek.Kind) is (int l, int r) && l > limit)
        {
            var op = Next();
            var right = Expression(r);
            left = new BinE(op.Kind, left, right, op.Line);
        }
        return left;
    }

    Expr SimpleExpr()
    {
        var t = Peek;
        switch (t.Kind)
        {
            case Tok.Number: Next(); return new NumE(t.Number, t.Line);
            case Tok.String: Next(); return new StrE(t.Text, t.Line);
            case Tok.Nil: Next(); return new NilE(t.Line);
            case Tok.True: Next(); return new BoolE(true, t.Line);
            case Tok.False: Next(); return new BoolE(false, t.Line);
            case Tok.Dots: Next(); return new VarargE(t.Line);
            case Tok.Function: Next(); return FuncBody(t.Line, false);
            case Tok.LBrace: return Table();
            default: return SuffixedExpr();
        }
    }

    Expr PrimaryExpr()
    {
        var t = Peek;
        if (t.Kind == Tok.Name) { Next(); return new NameE(t.Text, t.Line); }
        if (t.Kind == Tok.LParen)
        {
            Next();
            var e = Expression();
            Expect(Tok.RParen, ")");
            return new ParenE(e, t.Line);
        }
        throw Error($"unexpected '{(t.Kind == Tok.Eof ? "end of script" : t.Text)}'");
    }

    Expr SuffixedExpr()
    {
        var e = PrimaryExpr();
        while (true)
        {
            var t = Peek;
            switch (t.Kind)
            {
                case Tok.Dot: Next(); e = new IndexE(e, new StrE(Expect(Tok.Name, "name").Text, t.Line), t.Line); break;
                case Tok.LBracket:
                    Next();
                    var k = Expression();
                    Expect(Tok.RBracket, "]");
                    e = new IndexE(e, k, t.Line);
                    break;
                case Tok.Colon:
                    Next();
                    var name = Expect(Tok.Name, "method name").Text;
                    e = new MethodE(e, name, CallArgs(), t.Line);
                    break;
                case Tok.LParen or Tok.String or Tok.LBrace:
                    e = new CallE(e, CallArgs(), t.Line);
                    break;
                default: return e;
            }
        }
    }

    List<Expr> CallArgs()
    {
        var t = Peek;
        if (t.Kind == Tok.String) { Next(); return new List<Expr> { new StrE(t.Text, t.Line) }; }
        if (t.Kind == Tok.LBrace) return new List<Expr> { Table() };
        Expect(Tok.LParen, "(");
        var args = Is(Tok.RParen) ? new List<Expr>() : ExprList();
        Expect(Tok.RParen, ")");
        return args;
    }

    Expr Table()
    {
        int line = Expect(Tok.LBrace, "{").Line;
        var fields = new List<(Expr, Expr)>();
        while (!Is(Tok.RBrace))
        {
            if (Is(Tok.LBracket))
            {
                Next();
                var k = Expression();
                Expect(Tok.RBracket, "]");
                Expect(Tok.Assign, "=");
                fields.Add((k, Expression()));
            }
            else if (Is(Tok.Name) && _t[_p + 1].Kind == Tok.Assign)
            {
                var name = Next();
                Next();
                fields.Add((new StrE(name.Text, name.Line), Expression()));
            }
            else fields.Add((null, Expression()));
            if (!Accept(Tok.Comma) && !Accept(Tok.Semi)) break;
        }
        Expect(Tok.RBrace, "}");
        return new TableE(fields, line);
    }
}
