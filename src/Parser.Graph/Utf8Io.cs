using System.Text;

namespace Parser.Graph;

/// <summary>
/// Single choke point for writing text artifacts (JSON, GraphML, markdown, etc.)
/// produced by this toolkit. .NET's <see cref="Encoding.UTF8"/> singleton emits a
/// UTF-8 byte-order mark (EF BB BF) via its preamble, which is legal UTF-8 but
/// breaks strict consumers such as Python's <c>json.load()</c> ("Unexpected UTF-8
/// BOM (decode using utf-8-sig)"). The toolkit is sold as a portable, diffable
/// artifact set, so every file it writes must be BOM-less, encoding-stable UTF-8.
///
/// Reading is intentionally NOT centralized here: <see cref="System.IO.File.ReadAllText(string, Encoding)"/>
/// already auto-detects and strips a BOM when present (StreamReader's default
/// detectEncodingFromByteOrderMarks is true), so artifacts written by older
/// versions of this tool (which did emit a BOM) keep loading without changes.
/// </summary>
public static class Utf8Io
{
    /// <summary>UTF-8 without a byte-order mark - the encoding every writer in this project must use.</summary>
    public static readonly Encoding NoBom = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);

    /// <summary>Writes <paramref name="contents"/> to <paramref name="path"/> as UTF-8 with no BOM.</summary>
    public static void WriteAllText(string path, string? contents) =>
        File.WriteAllText(path, contents, NoBom);
}
