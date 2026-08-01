namespace Parser.Contracts;

/// <summary>
/// One extraction engine (T-SQL, .NET solution, ...) that turns an input into a
/// partial graph sharing the Vocab contract. Payloads from several extractors
/// are merged by id by the ParserGeneral orchestrator; id namespaces must not
/// collide ("Db::..." for SQL, "app::..." for application nodes).
/// </summary>
public interface IGraphExtractor
{
    /// <summary>Extractor name for logs and payload provenance (e.g. "tsql", "net").</summary>
    string Name { get; }

    /// <summary>Whether this extractor understands the given input path (file or directory).</summary>
    bool CanHandle(string inputPath);

    /// <summary>Analyze the input and return its partial graph.</summary>
    GraphPayload Extract(string inputPath);
}
