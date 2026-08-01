using System.Text.Json;

namespace NetParser;

/// <summary>
/// Catalog of recognizable SQL names (procs/views/functions and tables) loaded
/// from a nodestore model.json. Indexes writing variants, case-insensitive,
/// to the stable node id. Ported from app-bridge-spike/bridge/Catalog.cs; the
/// database name is derived per node from its id instead of hardcoded.
/// </summary>
public sealed class Catalog
{
    private readonly Dictionary<string, string> _procVariants = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> _tableVariants = new(StringComparer.OrdinalIgnoreCase);
    // Table name without schema -> id, only when unique across the catalog
    // (used for EF conventions where the schema is unknown).
    private readonly Dictionary<string, string?> _tableByBareName = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<(string Name, string Id)> _procs = new();

    /// <summary>Canonical "Schema.Name" proc names with their ids (for template matching).</summary>
    public IReadOnlyList<(string Name, string Id)> Procs => _procs;

    public bool TryResolveProc(string name, out string id) => _procVariants.TryGetValue(name.Trim(), out id!);

    public bool TryResolveTable(string name, out string id) => _tableVariants.TryGetValue(name.Trim(), out id!);

    /// <summary>Resolve a table by bare name (no schema); fails when ambiguous.</summary>
    public bool TryResolveTableBareName(string name, out string id)
    {
        id = "";
        if (_tableByBareName.TryGetValue(name.Trim(), out var found) && found is not null)
        {
            id = found;
            return true;
        }
        return false;
    }

    public static Catalog Load(string path)
    {
        string text = File.ReadAllText(path, new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        using var doc = JsonDocument.Parse(text);

        var catalog = new Catalog();
        if (!doc.RootElement.TryGetProperty("nodes", out var nodes))
            return catalog;

        foreach (var node in nodes.EnumerateArray())
        {
            if (!node.TryGetProperty("label", out var labelEl) || !node.TryGetProperty("name", out var nameEl)
                || !node.TryGetProperty("id", out var idEl))
                continue;

            string label = labelEl.GetString() ?? "";
            string name = nameEl.GetString() ?? "";
            string id = idEl.GetString() ?? "";
            if (name.Length == 0 || id.Length == 0)
                continue;

            // "Db::Schema.Obj" or "db:table:schema.tabla" -> db is the id prefix.
            int sep = id.IndexOf(':');
            string db = sep > 0 ? id[..sep] : "";

            if (label == "SqlObject")
            {
                catalog._procs.Add((name, id));
                foreach (var variant in BuildVariants(name, db))
                    catalog._procVariants[variant] = id;
            }
            else if (label == "Table")
            {
                foreach (var variant in BuildVariants(name, db))
                    catalog._tableVariants[variant] = id;

                int dot = name.IndexOf('.');
                string bare = dot > 0 ? name[(dot + 1)..] : name;
                // null marks "seen more than once" -> ambiguous, never resolved.
                catalog._tableByBareName[bare] =
                    catalog._tableByBareName.ContainsKey(bare) ? null : id;
            }
        }

        return catalog;
    }

    private static IEnumerable<string> BuildVariants(string schemaDotName, string database)
    {
        yield return schemaDotName;

        int dot = schemaDotName.IndexOf('.');
        if (dot > 0)
        {
            string schema = schemaDotName[..dot];
            string obj = schemaDotName[(dot + 1)..];

            yield return $"[{schema}].[{obj}]";
            if (database.Length > 0)
            {
                yield return $"{database}.{schema}.{obj}";
                yield return $"[{database}].[{schema}].[{obj}]";
            }
        }
    }
}
