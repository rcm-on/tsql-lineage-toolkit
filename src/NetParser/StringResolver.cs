using System.Text;
using System.Text.RegularExpressions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace NetParser;

/// <summary>
/// A string expression resolved by local data-flow: an ordered mix of known
/// text parts and "holes" (values only known at runtime, e.g. a parameter).
/// "Integration.Get" + feedName + "Updates" resolves to
/// [Text("Integration.Get"), Hole(feedName), Text("Updates")].
/// </summary>
public sealed class SqlStringTemplate
{
    public List<object> Parts { get; } = new();   // string | Hole

    public sealed record Hole(string Name, IParameterSymbol? Parameter);

    public bool IsLiteral => Parts.All(p => p is string);

    public string? Literal => IsLiteral ? string.Concat(Parts.Cast<string>()) : null;

    public bool HasKnownText => Parts.Any(p => p is string s && s.Trim().Length > 0);

    /// <summary>Single hole backed by a method parameter, if that is the template's only unknown.</summary>
    public Hole? SingleParameterHole
    {
        get
        {
            var holes = Parts.OfType<Hole>().ToList();
            return holes.Count == 1 && holes[0].Parameter is not null ? holes[0] : null;
        }
    }

    /// <summary>Regex matching candidate catalog names against the template.</summary>
    public Regex ToRegex()
    {
        var sb = new StringBuilder("^");
        foreach (var p in Parts)
            sb.Append(p is string s ? Regex.Escape(s) : ".*");
        sb.Append('$');
        return new Regex(sb.ToString(), RegexOptions.IgnoreCase);
    }

    /// <summary>Replace every hole with a concrete value (call-site narrowing).</summary>
    public string Substitute(string holeValue) =>
        string.Concat(Parts.Select(p => p is string s ? s : holeValue));

    public override string ToString() =>
        string.Concat(Parts.Select(p => p is string s ? s : $"{{{((Hole)p).Name}}}"));
}

/// <summary>
/// Resolves a C# string expression to a <see cref="SqlStringTemplate"/> using
/// the semantic model when it can (locals with a single initializer, const and
/// readonly fields, nameof) and syntax fallbacks when it cannot (unresolved
/// external symbols). Intra-method only; interprocedural narrowing of the
/// remaining holes is done by the extractor from call sites.
/// </summary>
public static class StringResolver
{
    public static SqlStringTemplate Resolve(ExpressionSyntax expr, SemanticModel model, int depth = 0)
    {
        var t = new SqlStringTemplate();
        if (depth > 12) { t.Parts.Add(new SqlStringTemplate.Hole(expr.ToString(), null)); return t; }

        switch (expr)
        {
            case LiteralExpressionSyntax lit when lit.IsKind(SyntaxKind.StringLiteralExpression):
                t.Parts.Add(lit.Token.ValueText);
                return t;

            case InterpolatedStringExpressionSyntax interp:
                foreach (var content in interp.Contents)
                {
                    if (content is InterpolatedStringTextSyntax text)
                        t.Parts.Add(text.TextToken.ValueText);
                    else if (content is InterpolationSyntax hole)
                        Append(t, Resolve(hole.Expression, model, depth + 1));
                }
                return t;

            case BinaryExpressionSyntax bin when bin.IsKind(SyntaxKind.AddExpression):
                Append(t, Resolve(bin.Left, model, depth + 1));
                Append(t, Resolve(bin.Right, model, depth + 1));
                return t;

            case ParenthesizedExpressionSyntax paren:
                return Resolve(paren.Expression, model, depth + 1);

            case InvocationExpressionSyntax inv when inv.Expression is IdentifierNameSyntax { Identifier.ValueText: "nameof" }:
                t.Parts.Add(inv.ArgumentList.Arguments.FirstOrDefault()?.ToString() ?? "");
                return t;

            case IdentifierNameSyntax or MemberAccessExpressionSyntax:
                return ResolveSymbol(expr, model, t, depth);

            default:
                t.Parts.Add(new SqlStringTemplate.Hole(expr.ToString(), null));
                return t;
        }
    }

    private static SqlStringTemplate ResolveSymbol(ExpressionSyntax expr, SemanticModel model, SqlStringTemplate t, int depth)
    {
        var symbol = model.GetSymbolInfo(expr).Symbol;

        // Constant known to the compiler (const fields, locals folded, enums).
        var constant = model.GetConstantValue(expr);
        if (constant.HasValue && constant.Value is string cs)
        {
            t.Parts.Add(cs);
            return t;
        }

        switch (symbol)
        {
            case ILocalSymbol local:
            {
                var init = FindSingleLocalInitializer(expr, local.Name);
                if (init is not null)
                    return Resolve(init, model, depth + 1);
                break;
            }
            case IFieldSymbol { IsReadOnly: true } or IFieldSymbol { IsConst: true }:
            {
                var declarator = symbol.DeclaringSyntaxReferences
                    .Select(r => r.GetSyntax())
                    .OfType<VariableDeclaratorSyntax>()
                    .FirstOrDefault(v => v.Initializer is not null);
                if (declarator?.Initializer is { } fi && fi.Value.SyntaxTree == model.SyntaxTree)
                    return Resolve(fi.Value, model, depth + 1);
                break;
            }
            case IParameterSymbol param:
                t.Parts.Add(new SqlStringTemplate.Hole(param.Name, param));
                return t;
        }

        // Syntax fallback (no or foreign symbol): a local declared once in the
        // enclosing method with an initializer.
        if (symbol is null && expr is IdentifierNameSyntax id)
        {
            var init = FindSingleLocalInitializer(expr, id.Identifier.ValueText);
            if (init is not null)
                return Resolve(init, model, depth + 1);
        }

        t.Parts.Add(new SqlStringTemplate.Hole(expr.ToString(), null));
        return t;
    }

    /// <summary>
    /// The initializer of local variable <paramref name="name"/> in the method
    /// enclosing <paramref name="site"/>, but only when that is its single
    /// assignment (a reassigned variable is not resolvable by this pass).
    /// </summary>
    private static ExpressionSyntax? FindSingleLocalInitializer(SyntaxNode site, string name)
    {
        var method = site.Ancestors().OfType<BaseMethodDeclarationSyntax>().FirstOrDefault();
        if (method is null) return null;

        var declarators = method.DescendantNodes().OfType<VariableDeclaratorSyntax>()
            .Where(v => v.Identifier.ValueText == name && v.Initializer is not null)
            .ToList();
        if (declarators.Count != 1) return null;

        bool reassigned = method.DescendantNodes().OfType<AssignmentExpressionSyntax>()
            .Any(a => a.Left is IdentifierNameSyntax lid && lid.Identifier.ValueText == name);
        return reassigned ? null : declarators[0].Initializer!.Value;
    }

    private static void Append(SqlStringTemplate target, SqlStringTemplate source)
    {
        foreach (var p in source.Parts)
        {
            // Merge adjacent text parts so templates/regexes stay canonical.
            if (p is string s && target.Parts.Count > 0 && target.Parts[^1] is string prev)
                target.Parts[^1] = prev + s;
            else
                target.Parts.Add(p);
        }
    }
}
