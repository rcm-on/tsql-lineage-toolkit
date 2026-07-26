using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace TSqlParser;

/// <summary>
/// Store-agnostic agent benchmark ("bench-make" / "bench-grade"): measures how well
/// an LLM agent NAVIGATES a nodestore, model by model, with the same six case types
/// on any store (WWI, FRK, a client DB...). bench-make derives the case subjects and
/// the expected answers deterministically from the store's own precomputed artifacts
/// (change_map.json, model.json, lineage_path.json) and emits self-contained prompt
/// files; bench-grade scores an answers directory produced by any model.
///
/// What this measures: navigation + output validity of the agent under test — NOT
/// engine correctness (the engine has its own oracle gates). Ground truth comes from
/// the store itself, so a perfect score means "the agent found the precomputed answer
/// and emitted valid JSON", which is exactly the agent-first contract of the store
/// (index.json howto). Case types mirror the measured scenarios of
/// docs/nodestore-analysis.md (local, chain, corpus, impact, column, rule).
/// </summary>
public static class AgentBench
{
    private static readonly JsonSerializerOptions Pretty = new()
    {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    // ── bench-make ────────────────────────────────────────────────────────────

    /// <summary>
    /// <paramref name="seed"/> rotates which eligible subject each case picks
    /// (seed % candidates): different-but-reproducible case sets from the same
    /// store, so models can't be tuned to one fixed question while every model
    /// under the same seed still answers exactly the same questions.
    /// </summary>
    public static int Make(string storeDir, string benchDir, int seed = 0)
    {
        var changeMapPath = Path.Combine(storeDir, "change_map.json");
        var modelPath = Path.Combine(storeDir, "model.json");
        if (!File.Exists(changeMapPath) || !File.Exists(modelPath))
        {
            Console.Error.WriteLine($"bench-make: change_map.json/model.json not found in store '{storeDir}'");
            return 1;
        }

        using var cmDoc = JsonDocument.Parse(File.ReadAllText(changeMapPath, Encoding.UTF8));
        using var modelDoc = JsonDocument.Parse(File.ReadAllText(modelPath, Encoding.UTF8));
        var impact = cmDoc.RootElement.TryGetProperty("impact", out var imp) && imp.ValueKind == JsonValueKind.Object
            ? imp.EnumerateObject().ToList()
            : new List<JsonProperty>();

        Directory.CreateDirectory(Path.Combine(benchDir, "cases"));
        Directory.CreateDirectory(Path.Combine(benchDir, "expected"));
        Directory.CreateDirectory(Path.Combine(benchDir, "answers"));

        var cases = new List<Dictionary<string, object?>>();

        string Name(JsonProperty p) => p.Value.GetProperty("name").GetString() ?? p.Name;
        static List<JsonElement> Arr(JsonElement e, string prop) =>
            e.TryGetProperty(prop, out var a) && a.ValueKind == JsonValueKind.Array
                ? a.EnumerateArray().ToList() : new List<JsonElement>();

        static T? Pick<T>(List<T> candidates, int seed) where T : struct =>
            candidates.Count == 0 ? null : candidates[seed % candidates.Count];

        // C1 (local): a writing object (seed 0: the one writing the most tables).
        {
            var candidates = impact
                .Where(p => Arr(p.Value, "via_data").Count > 0)
                .OrderByDescending(p => Arr(p.Value, "via_data").Count)
                .ThenBy(Name, StringComparer.Ordinal)
                .ToList();
            if (candidates.Count > 0)
            {
                var s = candidates[seed % candidates.Count];
                var tables = Arr(s.Value, "via_data")
                    .Select(d => d.GetProperty("table").GetString()!)
                    .OrderBy(x => x, StringComparer.Ordinal).ToList();
                AddCase(cases, benchDir, "C1", "writes_of_object",
                    $"List every table that the object '{Name(s)}' writes to.",
                    "{\"tables\": [\"<table name>\", ...]}",
                    new Dictionary<string, object> { ["tables"] = tables });
            }
            else AddSkipped(cases, "C1", "writes_of_object", "no object with via_data in change_map");
        }

        // C2 (chain): an object with callees (seed 0: the largest transitive closure).
        {
            var candidates = impact
                .Where(p => Arr(p.Value, "via_calls").Count > 0)
                .OrderByDescending(p => Arr(p.Value, "via_calls").Count)
                .ThenBy(Name, StringComparer.Ordinal)
                .ToList();
            if (candidates.Count > 0)
            {
                var s = candidates[seed % candidates.Count];
                var callees = Arr(s.Value, "via_calls")
                    .Select(v => (object)new Dictionary<string, object>
                    {
                        ["object"] = v.GetProperty("object").GetString()!,
                        ["depth"] = v.GetProperty("depth").GetInt32(),
                        ["conditional"] = v.GetProperty("conditional").GetBoolean(),
                    })
                    .ToList();
                AddCase(cases, benchDir, "C2", "transitive_callees",
                    $"List every object that '{Name(s)}' can transitively invoke (its full callee closure), and for each one: at which call depth it is FIRST reached and whether reaching it is conditional.",
                    "{\"callees\": [{\"object\": \"<name>\", \"depth\": <int>, \"conditional\": <bool>}, ...]}",
                    new Dictionary<string, object> { ["callees"] = callees });
            }
            else AddSkipped(cases, "C2", "transitive_callees", "no object with via_calls in change_map");
        }

        // C3 (corpus): top 3 SqlObjects by total_steps (order-sensitive).
        {
            var top = modelDoc.RootElement.GetProperty("nodes").EnumerateArray()
                .Where(n => n.TryGetProperty("label", out var l) && l.GetString() == "SqlObject"
                            && n.TryGetProperty("total_steps", out _))
                .OrderByDescending(n => n.GetProperty("total_steps").GetInt32())
                .ThenBy(n => n.GetProperty("name").GetString(), StringComparer.Ordinal)
                .Take(3)
                .Select(n => n.GetProperty("name").GetString()!)
                .ToList();
            if (top.Count == 3)
                AddCase(cases, benchDir, "C3", "corpus_top_steps",
                    "Which are the 3 objects with the most steps (property total_steps) in the whole database, in descending order? Break ties by ascending name.",
                    "{\"objects\": [\"<name most steps>\", \"<2nd>\", \"<3rd>\"]}",
                    new Dictionary<string, object> { ["objects"] = top });
            else AddSkipped(cases, "C3", "corpus_top_steps", "fewer than 3 SqlObjects in model.json");
        }

        // C4 (impact): a written table with readers (seed 0: the most-read one).
        {
            var candidates = new List<(string Owner, string Table, List<string> Consumers)>();
            foreach (var p in impact.OrderBy(Name, StringComparer.Ordinal))
                foreach (var d in Arr(p.Value, "via_data"))
                {
                    var consumers = Arr(d, "consumers").Select(c => c.GetString()!).ToList();
                    if (consumers.Count > 0)
                        candidates.Add((Name(p), d.GetProperty("table").GetString()!, consumers));
                }
            candidates = candidates
                .OrderByDescending(c => c.Consumers.Count)
                .ThenBy(c => c.Owner, StringComparer.Ordinal)
                .ThenBy(c => c.Table, StringComparer.Ordinal)
                .ToList();
            if (Pick(candidates, seed) is { } b)
                AddCase(cases, benchDir, "C4", "table_consumers",
                    $"The object '{b.Owner}' writes to the table '{b.Table}'. Which objects read that table (i.e. are exposed to that write)?",
                    "{\"consumers\": [\"<object name>\", ...]}",
                    new Dictionary<string, object> { ["consumers"] = b.Consumers.OrderBy(x => x, StringComparer.Ordinal).ToList() });
            else AddSkipped(cases, "C4", "table_consumers", "no written table with consumers in change_map");
        }

        // C5 (column): a rooted lineage_path column (seed rotates over all of them,
        // ordered by object name then column name).
        {
            var candidates = new List<(string ObjName, string Column, List<string> Roots)>();
            foreach (var n in modelDoc.RootElement.GetProperty("nodes").EnumerateArray()
                         .Where(n => n.TryGetProperty("label", out var l) && l.GetString() == "SqlObject")
                         .OrderBy(n => n.GetProperty("name").GetString(), StringComparer.Ordinal))
            {
                if (!n.TryGetProperty("path", out var pathEl))
                    continue;
                var lpPath = Path.Combine(storeDir, Path.GetDirectoryName(pathEl.GetString()!)!, "lineage_path.json");
                if (!File.Exists(lpPath))
                    continue;
                using var lpDoc = JsonDocument.Parse(File.ReadAllText(lpPath, Encoding.UTF8));
                foreach (var col in lpDoc.RootElement.EnumerateObject().OrderBy(c => c.Name, StringComparer.Ordinal))
                {
                    var roots = Arr(col.Value, "roots").Select(r => r.GetString()!).ToList();
                    if (roots.Count > 0)
                        candidates.Add((n.GetProperty("name").GetString()!, col.Name, roots));
                }
            }
            if (Pick(candidates, seed) is { } f)
                AddCase(cases, benchDir, "C5", "column_roots",
                    $"For the output column '{f.Column}' of the object '{f.ObjName}': from which root base-table column(s) does its data ultimately come? Answer as 'table.column' strings exactly as the store records them.",
                    "{\"roots\": [\"<schema.table.column>\", ...]}",
                    new Dictionary<string, object> { ["roots"] = f.Roots.OrderBy(x => x, StringComparer.Ordinal).ToList() });
            else AddSkipped(cases, "C5", "column_roots", "no lineage_path.json with rooted columns");
        }

        // C6 (rule): a conditional hop in the callee closures (seed rotates).
        {
            var candidates = new List<(string Owner, string Callee, string Condition)>();
            foreach (var p in impact.OrderBy(Name, StringComparer.Ordinal))
                foreach (var v in Arr(p.Value, "via_calls"))
                {
                    if (!v.GetProperty("conditional").GetBoolean())
                        continue;
                    var cond = v.TryGetProperty("condition_text", out var ct) ? ct.GetString() : null;
                    if (!string.IsNullOrWhiteSpace(cond))
                        candidates.Add((Name(p), v.GetProperty("object").GetString()!, cond!));
                }
            if (Pick(candidates, seed) is { } f)
                AddCase(cases, benchDir, "C6", "call_condition",
                    $"Under what condition does '{f.Owner}' first reach '{f.Callee}' in its call closure? Answer with the condition text as the store records it.",
                    "{\"condition\": \"<condition text>\"}",
                    new Dictionary<string, object> { ["condition"] = f.Condition });
            else AddSkipped(cases, "C6", "call_condition", "no conditional hop with condition_text");
        }

        Utf8Io.WriteAllText(Path.Combine(benchDir, "cases.json"), JsonSerializer.Serialize(new Dictionary<string, object>
        {
            ["seed"] = seed,
            ["cases"] = cases,
        }, Pretty));
        var made = cases.Count(c => c["skipped"] is false);
        Console.WriteLine($"bench-make: {made} case(s) generated, {cases.Count - made} skipped (seed {seed}) -> {benchDir}");
        foreach (var c in cases)
            Console.WriteLine($"  {c["id"]} {c["type"]}: {(c["skipped"] is false ? "ok" : $"SKIPPED ({c["skip_reason"]})")}");
        return 0;
    }

    private static void AddCase(List<Dictionary<string, object?>> cases, string benchDir,
        string id, string type, string question, string schema, Dictionary<string, object> expected)
    {
        // The prompt is self-contained and model-agnostic: any agent with file access
        // to the store (or a human relaying files to a chat model) can take it as-is.
        var prompt = $"""
            # agent-bench {id} ({type})

            You are evaluated on navigating a T-SQL "nodestore": a directory of JSON files
            describing SQL objects, their dependencies and their impact.

            Store directory: <STORE>
            (Read <STORE>/index.json first: its "howto" section tells you which file
            answers which kind of question. Read as few files as possible.)

            ## Question

            {question}

            ## Output contract

            Reply with STRICT JSON only - no prose, no markdown fence - matching:

            {schema}

            Save/return it as the answer for case {id}.
            """;
        Utf8Io.WriteAllText(Path.Combine(benchDir, "cases", $"{id}.prompt.md"), prompt);
        Utf8Io.WriteAllText(Path.Combine(benchDir, "expected", $"{id}.json"), JsonSerializer.Serialize(expected, Pretty));
        cases.Add(new Dictionary<string, object?>
        {
            ["id"] = id,
            ["type"] = type,
            ["question"] = question,
            ["prompt_file"] = $"cases/{id}.prompt.md",
            ["expected_file"] = $"expected/{id}.json",
            ["skipped"] = false,
        });
    }

    private static void AddSkipped(List<Dictionary<string, object?>> cases, string id, string type, string reason) =>
        cases.Add(new Dictionary<string, object?>
        {
            ["id"] = id, ["type"] = type, ["skipped"] = true, ["skip_reason"] = reason,
        });

    // ── bench-grade ───────────────────────────────────────────────────────────

    /// <summary>Exit codes: 0 = every graded case passed, 2 = some FAIL/MISSING/INVALID, 1 = malformed bench.</summary>
    public static int Grade(string benchDir, string answersDir)
    {
        var casesPath = Path.Combine(benchDir, "cases.json");
        if (!File.Exists(casesPath))
        {
            Console.Error.WriteLine($"bench-grade: cases.json not found in '{benchDir}' (run bench-make first)");
            return 1;
        }
        using var casesDoc = JsonDocument.Parse(File.ReadAllText(casesPath, Encoding.UTF8));

        int pass = 0, fail = 0, skip = 0;
        var perCase = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var c in casesDoc.RootElement.GetProperty("cases").EnumerateArray())
        {
            var id = c.GetProperty("id").GetString()!;
            var type = c.GetProperty("type").GetString()!;
            if (c.GetProperty("skipped").GetBoolean())
            {
                Console.WriteLine($"  {id} {type,-20} SKIP");
                perCase[id] = "SKIP";
                skip++;
                continue;
            }

            var expected = JsonDocument.Parse(File.ReadAllText(Path.Combine(benchDir, c.GetProperty("expected_file").GetString()!), Encoding.UTF8)).RootElement;
            var answerPath = Path.Combine(answersDir, $"{id}.json");
            if (!File.Exists(answerPath))
            {
                Console.WriteLine($"  {id} {type,-20} MISSING ({answerPath})");
                perCase[id] = "MISSING";
                fail++;
                continue;
            }

            JsonElement answer;
            try
            {
                // Tolerate the classic model sins: BOM and a ```json fence.
                var raw = File.ReadAllText(answerPath, Encoding.UTF8).TrimStart('﻿').Trim();
                raw = Regex.Replace(raw, @"^```(?:json)?\s*|\s*```$", "");
                answer = JsonDocument.Parse(raw).RootElement;
            }
            catch (JsonException e)
            {
                Console.WriteLine($"  {id} {type,-20} INVALID JSON ({e.Message})");
                perCase[id] = "INVALID";
                fail++;
                continue;
            }

            var (ok, detail) = GradeCase(type, expected, answer);
            Console.WriteLine($"  {id} {type,-20} {(ok ? "PASS" : $"FAIL {detail}")}");
            perCase[id] = ok ? "PASS" : $"FAIL {detail}";
            if (ok) pass++; else fail++;
        }

        var graded = pass + fail;
        Console.WriteLine($"bench-grade: {pass}/{graded} PASS ({skip} skipped) - answers: {answersDir}");

        // Persist a per-model scorecard so runs accumulate into a comparison table.
        // The model/engine metadata comes from the run's own answers/<model>/run.json
        // (free-form: model, provider, date, engine_commit, tool_calls, notes...).
        object? runMeta = null;
        var runMetaPath = Path.Combine(answersDir, "run.json");
        if (File.Exists(runMetaPath))
            try { runMeta = JsonSerializer.Deserialize<Dictionary<string, object>>(File.ReadAllText(runMetaPath, Encoding.UTF8)); }
            catch (JsonException) { runMeta = "(run.json invalid)"; }
        Directory.CreateDirectory(Path.Combine(benchDir, "results"));
        var resultPath = Path.Combine(benchDir, "results", $"{new DirectoryInfo(answersDir).Name}.json");
        Utf8Io.WriteAllText(resultPath, JsonSerializer.Serialize(new Dictionary<string, object?>
        {
            ["answers_dir"] = new DirectoryInfo(answersDir).Name,
            ["seed"] = casesDoc.RootElement.TryGetProperty("seed", out var sd) ? sd.GetInt32() : 0,
            ["run"] = runMeta,
            ["pass"] = pass,
            ["graded"] = graded,
            ["skipped"] = skip,
            ["per_case"] = perCase,
        }, Pretty));
        Console.WriteLine($"  scorecard -> {resultPath}");

        return fail == 0 ? 0 : 2;
    }

    private static (bool Ok, string Detail) GradeCase(string type, JsonElement expected, JsonElement answer)
    {
        static string Norm(string s) => Regex.Replace(s, @"\s+", " ").Trim().ToLowerInvariant();
        static List<string> Strings(JsonElement e, string prop) =>
            e.TryGetProperty(prop, out var a) && a.ValueKind == JsonValueKind.Array
                ? a.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.String).Select(x => Norm(x.GetString()!)).ToList()
                : new List<string>();
        static (bool, string) SetEq(List<string> exp, List<string> got)
        {
            var missing = exp.Except(got).ToList();
            var extra = got.Except(exp).ToList();
            return missing.Count == 0 && extra.Count == 0
                ? (true, "")
                : (false, $"(missing: [{string.Join("; ", missing)}] extra: [{string.Join("; ", extra)}])");
        }

        switch (type)
        {
            case "writes_of_object":
                return SetEq(Strings(expected, "tables"), Strings(answer, "tables"));
            case "table_consumers":
                return SetEq(Strings(expected, "consumers"), Strings(answer, "consumers"));
            case "column_roots":
                return SetEq(Strings(expected, "roots"), Strings(answer, "roots"));
            case "corpus_top_steps":
            {
                var exp = Strings(expected, "objects");
                var got = Strings(answer, "objects");
                return exp.SequenceEqual(got) ? (true, "") : (false, $"(expected order: [{string.Join("; ", exp)}] got: [{string.Join("; ", got)}])");
            }
            case "transitive_callees":
            {
                static List<string> Keys(JsonElement e) =>
                    e.TryGetProperty("callees", out var a) && a.ValueKind == JsonValueKind.Array
                        ? a.EnumerateArray()
                            .Where(x => x.ValueKind == JsonValueKind.Object)
                            .Select(x => $"{Norm(x.TryGetProperty("object", out var o) && o.ValueKind == JsonValueKind.String ? o.GetString()! : "?")}|{(x.TryGetProperty("depth", out var d) && d.ValueKind == JsonValueKind.Number ? d.GetInt32() : -1)}|{(x.TryGetProperty("conditional", out var c) && c.ValueKind == JsonValueKind.True)}")
                            .ToList()
                        : new List<string>();
                var missing = Keys(expected).Except(Keys(answer)).ToList();
                var extra = Keys(answer).Except(Keys(expected)).ToList();
                return missing.Count == 0 && extra.Count == 0
                    ? (true, "")
                    : (false, $"(missing: [{string.Join("; ", missing)}] extra: [{string.Join("; ", extra)}])");
            }
            case "call_condition":
            {
                var exp = expected.TryGetProperty("condition", out var e) && e.ValueKind == JsonValueKind.String ? Norm(e.GetString()!) : "";
                var got = answer.TryGetProperty("condition", out var g) && g.ValueKind == JsonValueKind.String ? Norm(g.GetString()!) : "";
                // The store's condition text is the ground truth; the model may quote
                // it with extra framing, so containment either way counts.
                var ok = exp.Length > 0 && got.Length > 0 && (got.Contains(exp) || exp.Contains(got));
                return ok ? (true, "") : (false, $"(expected: '{exp}' got: '{got}')");
            }
            default:
                return (false, $"(unknown case type '{type}')");
        }
    }
}
