using Microsoft.SqlServer.TransactSql.ScriptDom;

namespace TSqlParser;

/// <summary>
/// Classifies the operators a SQL expression or predicate is built from into a
/// normalized, stable token set - the structured complement to the raw expression
/// text kept in an edge's <c>logic</c> property. Where <c>logic</c> ("Price * Qty",
/// "Status = 'X' AND Qty &gt; 0") is human-readable but only queryable by string
/// match, these tokens are meant to be consumed by a rule engine: each is
/// "<c>category:symbol</c>", e.g. <c>arith:*</c>, <c>logical:AND</c>,
/// <c>compare:&gt;</c>, <c>func:SUM</c>, <c>case:CASE</c>, <c>cast:CONVERT</c>.
///
/// The category is what drives risk rules independently of the exact symbol:
/// <list type="bullet">
/// <item><c>arith</c>/<c>bitwise</c> - value math; a source type change can overflow/truncate.</item>
/// <item><c>logical</c>/<c>compare</c>/<c>range</c>/<c>set</c>/<c>pattern</c>/<c>null</c> - row selection (WHERE/JOIN); affects which rows, not the value.</item>
/// <item><c>cast</c> - an explicit dependency on the source's data type.</item>
/// <item><c>func</c>/<c>case</c> - conditional/transforming logic.</item>
/// </list>
///
/// Tokens are de-duplicated and ordered, so the same expression always yields the
/// same list (stable diffs, deterministic graph output). Overriding <c>Visit</c>
/// (not <c>ExplicitVisit</c>) keeps ScriptDom's child traversal intact, so operators
/// nested at any depth - inside a CASE, a function argument, a subquery - are all seen.
/// </summary>
public static class OperatorClassifier
{
    /// <summary>Returns the de-duplicated, ordered operator tokens of <paramref name="fragment"/>, or an empty list for null / operator-free expressions (a bare column or literal).</summary>
    public static IReadOnlyList<string> Classify(TSqlFragment? fragment)
    {
        if (fragment == null)
            return Array.Empty<string>();

        var visitor = new Visitor();
        fragment.Accept(visitor);
        return visitor.Tokens.Count == 0 ? Array.Empty<string>() : visitor.Tokens.ToList();
    }

    private sealed class Visitor : TSqlFragmentVisitor
    {
        // SortedSet => de-duplicated and deterministically ordered in one structure.
        public SortedSet<string> Tokens { get; } = new(StringComparer.Ordinal);

        public override void Visit(BinaryExpression node) => Tokens.Add(Arithmetic(node.BinaryExpressionType));

        public override void Visit(BooleanBinaryExpression node) =>
            Tokens.Add(node.BinaryExpressionType == BooleanBinaryExpressionType.And ? "logical:AND" : "logical:OR");

        public override void Visit(BooleanNotExpression node) => Tokens.Add("logical:NOT");

        public override void Visit(BooleanComparisonExpression node) => Tokens.Add(Comparison(node.ComparisonType));

        public override void Visit(BooleanTernaryExpression node) =>
            Tokens.Add(node.TernaryExpressionType == BooleanTernaryExpressionType.Between ? "range:BETWEEN" : "range:NOT BETWEEN");

        public override void Visit(BooleanIsNullExpression node) =>
            Tokens.Add(node.IsNot ? "null:IS NOT NULL" : "null:IS NULL");

        public override void Visit(LikePredicate node) =>
            Tokens.Add(node.NotDefined ? "pattern:NOT LIKE" : "pattern:LIKE");

        public override void Visit(InPredicate node) =>
            Tokens.Add(node.NotDefined ? "set:NOT IN" : "set:IN");

        public override void Visit(SearchedCaseExpression node) => Tokens.Add("case:CASE");
        public override void Visit(SimpleCaseExpression node) => Tokens.Add("case:CASE");

        public override void Visit(CastCall node) => Tokens.Add("cast:CAST");
        public override void Visit(TryCastCall node) => Tokens.Add("cast:TRY_CAST");
        public override void Visit(ConvertCall node) => Tokens.Add("cast:CONVERT");
        public override void Visit(TryConvertCall node) => Tokens.Add("cast:TRY_CONVERT");

        public override void Visit(FunctionCall node)
        {
            var name = node.FunctionName?.Value;
            if (!string.IsNullOrEmpty(name))
                Tokens.Add("func:" + name.ToUpperInvariant());
        }

        private static string Arithmetic(BinaryExpressionType t) => t switch
        {
            BinaryExpressionType.Add => "arith:+",
            BinaryExpressionType.Subtract => "arith:-",
            BinaryExpressionType.Multiply => "arith:*",
            BinaryExpressionType.Divide => "arith:/",
            BinaryExpressionType.Modulo => "arith:%",
            BinaryExpressionType.BitwiseAnd => "bitwise:&",
            BinaryExpressionType.BitwiseOr => "bitwise:|",
            BinaryExpressionType.BitwiseXor => "bitwise:^",
            BinaryExpressionType.LeftShift => "bitwise:<<",
            BinaryExpressionType.RightShift => "bitwise:>>",
            BinaryExpressionType.Concat => "concat:+",
            _ => "arith:" + t,
        };

        private static string Comparison(BooleanComparisonType t) => t switch
        {
            BooleanComparisonType.Equals => "compare:=",
            BooleanComparisonType.GreaterThan => "compare:>",
            BooleanComparisonType.LessThan => "compare:<",
            BooleanComparisonType.GreaterThanOrEqualTo => "compare:>=",
            BooleanComparisonType.LessThanOrEqualTo => "compare:<=",
            BooleanComparisonType.NotEqualToBrackets => "compare:<>",
            BooleanComparisonType.NotEqualToExclamation => "compare:!=",
            BooleanComparisonType.NotLessThan => "compare:!<",
            BooleanComparisonType.NotGreaterThan => "compare:!>",
            BooleanComparisonType.IsDistinctFrom => "compare:IS DISTINCT FROM",
            BooleanComparisonType.IsNotDistinctFrom => "compare:IS NOT DISTINCT FROM",
            BooleanComparisonType.NotLike => "pattern:NOT LIKE",
            BooleanComparisonType.LeftOuterJoin => "compare:*=",
            BooleanComparisonType.RightOuterJoin => "compare:=*",
            _ => "compare:" + t,
        };
    }
}
