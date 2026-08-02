using System.Runtime.CompilerServices;

// Lets TSqlParser.Tests unit-test XePlanCaptor's correlation/emission logic
// (Correlate, ExtractStatement, EmitPlanFiles, ProcAccumulator) without a live
// SQL Server connection, while keeping that surface out of the public API.
[assembly: InternalsVisibleTo("TSqlParser.Tests")]
