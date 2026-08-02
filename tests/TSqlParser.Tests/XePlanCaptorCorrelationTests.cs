using TSqlParser;
using Xunit;

namespace TSqlParser.Tests;

/// <summary>
/// Unit tests for the nest_level attribution rule XePlanCaptor.Correlate implements:
/// within one session_id, a non-PROC event (ADHOC/PREPARED dynamic SQL) attaches
/// to the nearest still-open PROC frame with a smaller nest_level. No live SQL
/// Server connection needed - CapturedEvent and the name resolver are synthetic,
/// mirroring exactly what a real XE capture produces (validated by hand against
/// a real 3-level proc-calls-proc-calls-EXEC(@sql) probe; see notes/task-captor-xe.md).
/// </summary>
public class XePlanCaptorCorrelationTests
{
    private static string PlanXmlTouching(string schema, string table, string statementType = "INSERT") => $"""
        <ShowPlanXML xmlns="http://schemas.microsoft.com/sqlserver/2004/07/showplan"><BatchSequence><Batch><Statements><StmtSimple StatementType="{statementType}"><QueryPlan><RelOp PhysicalOp="Table Insert"><Object Database="[TestDb]" Schema="[{schema}]" Table="[{table}]"/></RelOp></QueryPlan></StmtSimple></Statements></Batch></BatchSequence></ShowPlanXML>
        """;

    private static XePlanCaptor.CapturedEvent Ev(
        long sessionId, int nestLevel, string objectType, long objectId, string table, DateTime ts) =>
        new(SessionId: sessionId, NestLevel: nestLevel, ObjectType: objectType, ObjectId: objectId,
            SourceDatabaseId: 9, ObjectName: objectType == "PROC" ? "irrelevant" : "Dynamic SQL",
            PlanXml: PlanXmlTouching("dbo", table), Timestamp: ts, FileOffset: ts.Ticks);

    [Fact]
    public void TwoLevel_AdhocAttachesToItsParentProc()
    {
        // The original documented case: PROC nest=1, then an ADHOC nest>1 in the
        // same session - must attach to that PROC, not be dropped or orphaned.
        var t0 = DateTime.UtcNow;
        var events = new List<XePlanCaptor.CapturedEvent>
        {
            Ev(sessionId: 52, nestLevel: 1, objectType: "PROC",  objectId: 386100416, table: "CurrentValue", ts: t0),
            Ev(sessionId: 52, nestLevel: 2, objectType: "ADHOC", objectId: 488744064, table: "City",         ts: t0.AddMilliseconds(1)),
        };

        var procs = XePlanCaptor.Correlate(events, (dbId, objId) =>
            objId == 386100416 ? ("Sequences", "ReseedSequenceBeyondTableValues") : null);

        var proc = Assert.Single(procs);
        Assert.Equal("Sequences", proc.Schema);
        Assert.Equal("ReseedSequenceBeyondTableValues", proc.Name);
        Assert.Equal(2, proc.Statements.Count);
        Assert.Contains(proc.Statements, s => TableInStmt(s) == "[City]");
    }

    [Fact]
    public void ThreeLevel_DynamicSqlAttachesToImmediateParent_NotTopLevelProc()
    {
        // procA (nest=1) calls procB (nest=2), procB runs EXEC(@sql) (nest=3).
        // The dynamic statement must attach to procB, never to procA - this is
        // the case the brief explicitly flagged as unvalidated beyond 2 levels.
        var t0 = DateTime.UtcNow;
        var events = new List<XePlanCaptor.CapturedEvent>
        {
            Ev(sessionId: 56, nestLevel: 1, objectType: "PROC",     objectId: 1, table: "OwnTableA", ts: t0),
            Ev(sessionId: 56, nestLevel: 2, objectType: "PROC",     objectId: 2, table: "OwnTableB", ts: t0.AddMilliseconds(1)),
            Ev(sessionId: 56, nestLevel: 3, objectType: "PREPARED", objectId: 999, table: "Dimension_City", ts: t0.AddMilliseconds(2)),
        };

        var procs = XePlanCaptor.Correlate(events, (dbId, objId) => objId switch
        {
            1 => ("dbo", "ProcLevelA"),
            2 => ("dbo", "ProcLevelB"),
            _ => null,
        });

        Assert.Equal(2, procs.Count);
        var procA = procs.Single(p => p.Name == "ProcLevelA");
        var procB = procs.Single(p => p.Name == "ProcLevelB");

        Assert.Single(procA.Statements); // only its own statement
        Assert.Equal("[OwnTableA]", TableInStmt(procA.Statements[0]));

        Assert.Equal(2, procB.Statements.Count); // its own + the attributed dynamic one
        Assert.Contains(procB.Statements, s => TableInStmt(s) == "[Dimension_City]");
        Assert.DoesNotContain(procA.Statements, s => TableInStmt(s) == "[Dimension_City]");
    }

    [Fact]
    public void TopLevelAdhoc_WithNoOpenProcFrame_IsDropped()
    {
        // nest_level=1 non-PROC batch (an ad-hoc query run directly, not inside
        // any procedure) has no PROC frame to attach to - must be silently
        // skipped, not crash and not invent a phantom procedure.
        var events = new List<XePlanCaptor.CapturedEvent>
        {
            Ev(sessionId: 1, nestLevel: 1, objectType: "ADHOC", objectId: 42, table: "SomeTable", ts: DateTime.UtcNow),
        };

        var procs = XePlanCaptor.Correlate(events, (_, _) => null);

        Assert.Empty(procs);
    }

    [Fact]
    public void ProcRecursionAtSameDepth_StartsFreshFrame()
    {
        // Two sibling calls to different procs both at nest_level=1 within the
        // same session (procX runs, finishes; procY runs after it) must not
        // bleed a dynamic statement from the second call into the first proc's
        // accumulator just because it was seen earlier in the dictionary.
        var t0 = DateTime.UtcNow;
        var events = new List<XePlanCaptor.CapturedEvent>
        {
            Ev(sessionId: 7, nestLevel: 1, objectType: "PROC",  objectId: 10, table: "TableX",     ts: t0),
            Ev(sessionId: 7, nestLevel: 1, objectType: "PROC",  objectId: 20, table: "TableY",     ts: t0.AddMilliseconds(1)),
            Ev(sessionId: 7, nestLevel: 2, objectType: "ADHOC", objectId: 30, table: "DynamicUnderY", ts: t0.AddMilliseconds(2)),
        };

        var procs = XePlanCaptor.Correlate(events, (dbId, objId) => objId switch
        {
            10 => ("dbo", "ProcX"),
            20 => ("dbo", "ProcY"),
            _ => null,
        });

        var procX = procs.Single(p => p.Name == "ProcX");
        var procY = procs.Single(p => p.Name == "ProcY");
        Assert.Single(procX.Statements);
        Assert.Equal(2, procY.Statements.Count); // own + the dynamic one that ran after it started
        Assert.Contains(procY.Statements, s => TableInStmt(s) == "[DynamicUnderY]");
    }

    private static string TableInStmt(System.Xml.Linq.XElement stmt) =>
        stmt.Descendants().First(e => e.Name.LocalName == "Object").Attribute("Table")!.Value;
}
