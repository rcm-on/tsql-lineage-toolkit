using Microsoft.SqlServer.TransactSql.ScriptDom;

namespace TSqlParser;

/// <summary>
/// Classifies a WHERE predicate as domain business logic vs. row addressing, so a
/// consumer can decide whether to treat it as a "business rule" or ignore it as
/// plumbing - without ever discarding the underlying data (see <see cref="Classify"/>).
///
/// Heuristic (from real corpora, e.g. WideWorldImporters/Ola Hallengren/First
/// Responder Kit): a column compared against a literal ("IsActive = 1",
/// "Status &lt;&gt; 'X'") is a domain-shaped condition - it encodes a business
/// decision baked into the SQL. A column compared against a parameter/variable
/// ("ID = @ID") or a system value ("object_id = @@PROCID") is just addressing a
/// row or environment state, not expressing a rule. IS NULL/IS NOT NULL and
/// literal-valued IN/LIKE count as domain signals too (they test a state, e.g. a
/// soft-delete flag). A predicate that mixes both shapes ("Status = 'X' AND
/// ID = @ID") is reported as "mixed" rather than force-picked one way, and a
/// predicate with no classifiable comparison (column-to-column joins, bare
/// function calls, EXISTS with nothing at this level) also comes back "mixed" -
/// the safe, non-committal default; the raw text and FilterColumns are still
/// captured regardless of this classification, so nothing is lost by an
/// imprecise call here.
/// </summary>
public static class FilterRuleClassifier
{
    public const string DomainFilter = "domain_filter";
    public const string KeyLookup = "key_lookup";
    public const string Mixed = "mixed";

    public static string Classify(TSqlFragment? fragment)
    {
        if (fragment == null)
            return Mixed;

        var visitor = new Visitor();
        fragment.Accept(visitor);

        return (visitor.SawLiteralSignal, visitor.SawKeySignal) switch
        {
            (true, false) => DomainFilter,
            (false, true) => KeyLookup,
            _ => Mixed,
        };
    }

    private sealed class Visitor : TSqlFragmentVisitor
    {
        public bool SawLiteralSignal { get; private set; }
        public bool SawKeySignal { get; private set; }

        public override void Visit(BooleanComparisonExpression node)
        {
            Classify(node.FirstExpression);
            Classify(node.SecondExpression);
        }

        public override void Visit(BooleanIsNullExpression node) => SawLiteralSignal = true;

        public override void Visit(InPredicate node)
        {
            if (node.Subquery != null)
                return; // "col IN (SELECT ...)" - not a literal/param signal at this level.
            if (node.Values == null || node.Values.Count == 0)
                return;
            if (node.Values.All(v => v is Literal))
                SawLiteralSignal = true;
            else if (node.Values.Any(v => v is VariableReference or GlobalVariableExpression))
                SawKeySignal = true;
        }

        public override void Visit(LikePredicate node)
        {
            Classify(node.SecondExpression);
        }

        public override void Visit(BooleanTernaryExpression node)
        {
            // BETWEEN x AND y
            Classify(node.SecondExpression);
            Classify(node.ThirdExpression);
        }

        private void Classify(ScalarExpression? expr)
        {
            switch (expr)
            {
                case Literal:
                    SawLiteralSignal = true;
                    break;
                case VariableReference:
                case GlobalVariableExpression:
                    SawKeySignal = true;
                    break;
            }
        }
    }
}
