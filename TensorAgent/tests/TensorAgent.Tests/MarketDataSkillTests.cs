// Copyright (c) Zhongkai Fu. All rights reserved.
// https://github.com/zhongkaifu/TensorSharp
//
// This file is part of TensorSharp.
//
// TensorSharp is licensed under the BSD-3-Clause license found in the LICENSE file in the root directory of this source tree.
//
// TensorSharp is distributed in the hope that it will be useful, but WITHOUT ANY WARRANTY; without even the implied warranty of
// MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the BSD-3-Clause License for more details.

using System.Text.Json;
using System.Text.RegularExpressions;
using TensorAgent.Core.Sandbox;
using TensorAgent.Core.Python;
using TensorAgent.Core.Shell;
using TensorSharp.AgentHost.CodeExec;

namespace TensorAgent.Tests;

/// <summary>
/// The market-data skill: the task-specific help that used to live in every prompt.
///
/// <para>
/// A complete Yahoo Finance screener program was pasted into the shell tool's
/// DESCRIPTION, which the model reads on every turn, and it made the model reach for
/// finance APIs on requests that had nothing to do with finance. The program itself
/// was not the problem — where it lived was. It now lives in a skill, which is
/// injected only when the request matches it, and which does the job better than the
/// generic research skill could: research came back with a page reading "Oops,
/// something went wrong" and an article about capital gains tax.
/// </para>
/// <para>
/// These tests do not reach the network. They drive the script's own parsing,
/// validation and output shaping against payloads written here, which is where the
/// promises in SKILL.md actually live: a row missing a field is DROPPED rather than
/// printed with a gap, and a bad symbol is reported rather than guessed at.
/// </para>
/// </summary>
[Collection(LivePythonCollection.Name)]
public sealed class MarketDataSkillTests : IDisposable
{
    private static readonly string Repo = FindRepoRoot();
    private static readonly string Script =
        Path.Combine(Repo, "TensorAgent", "skills", "market-data", "scripts", "market_movers.py");

    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "tensoragent-market-" + Guid.NewGuid().ToString("N"));

    public MarketDataSkillTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, true); } catch { }
    }

    // =====================================================================================
    // what the bundle promises, checked without a socket
    // =====================================================================================

    [Fact]
    public void TheSkillIsPresentAndDeclaresWhenToUseIt()
    {
        string manifest = Path.Combine(Repo, "TensorAgent", "skills", "market-data", "SKILL.md");
        Assert.True(File.Exists(manifest), $"{manifest} is missing");
        Assert.True(File.Exists(Script), $"{Script} is missing");

        string text = File.ReadAllText(manifest).Replace("\r\n", "\n");
        Assert.StartsWith("---\n", text, StringComparison.Ordinal);
        Assert.Matches(@"(?m)^name:\s*market-data\s*$", text);

        Match description = Regex.Match(text, @"(?m)^description:\s*(?<value>.+)$");
        Assert.True(description.Success, "a skill with no description is invisible to the model");
        string value = description.Groups["value"].Value;
        // The words a request actually uses, because that is what the model matches on.
        foreach (string trigger in new[] { "stock", "share", "price", "gainers" })
            Assert.Contains(trigger, value, StringComparison.OrdinalIgnoreCase);
        // And it must say what it needs, since the switch is off by default.
        Assert.Contains("Network switch", value, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// The script may import only what the phone actually has: no requests, no pandas,
    /// no yfinance. The whole point of the skill is that it needs nothing installed.
    /// </summary>
    [Fact]
    public void TheScriptImportsOnlyTheStandardLibrary()
    {
        string[] allowed =
        {
            "__future__", "argparse", "json", "sys", "urllib", "datetime",
        };
        foreach (Match import in Regex.Matches(
                     File.ReadAllText(Script),
                     @"(?m)^\s*(?:from\s+(?<m>[A-Za-z_][\w.]*)\s+import|import\s+(?<m>[A-Za-z_][\w.]*))"))
        {
            string module = import.Groups["m"].Value.Split('.')[0];
            Assert.Contains(module, allowed);
        }
    }

    // =====================================================================================
    // the promises that keep a wrong number out of an answer
    // =====================================================================================

    /// <summary>
    /// A row missing any printed field is dropped. Asking for five and getting three is
    /// the correct outcome when only three rows are complete; printing five with two
    /// blanks would put a gap where a fact goes.
    /// </summary>
    [LivePythonFact]
    public void AnIncompleteRowIsDroppedRatherThanPrintedWithAGap()
    {
        string output = RunProbe("""
            rows = [
                complete("AAA", 10.0, 1.0, 11.1, 100),
                dict(complete("BBB", 20.0, 2.0, 11.1, 200), shortName=None),
                dict(complete("CCC", 30.0, 3.0, 11.1, 300), regularMarketVolume="lots"),
                dict(complete("DDD", 40.0, 4.0, 11.1, 400), quoteType="ETF"),
                complete("EEE", 50.0, 5.0, 11.1, 500),
            ]
            kept = [r["symbol"] for r in mm.rows_from(payload(rows), True, 10)]
            print("KEPT " + ",".join(kept))
            """);
        Assert.Contains("KEPT AAA,EEE", output, StringComparison.Ordinal);
    }

    /// <summary>A boolean is not a number, however much Python agrees that it is.</summary>
    [LivePythonFact]
    public void ABooleanIsNotAcceptedWhereANumberIsRequired()
    {
        string output = RunProbe("""
            print("BOOL " + str(mm.is_number(True)) + " INT " + str(mm.is_number(3))
                  + " FLOAT " + str(mm.is_number(1.5)) + " STR " + str(mm.is_number("7")))
            """);
        Assert.Contains("BOOL False INT True FLOAT True STR False", output, StringComparison.Ordinal);
    }

    /// <summary>
    /// The table is the answer, so its cells have to be the fetched values: the sign is
    /// explicit both ways, the percentage carries its unit, the CURRENCY is printed —
    /// a London row is priced in pence, so a bare number is a hundredfold error read as
    /// a fact — and a pipe inside a company name does not break the row into columns.
    /// </summary>
    [LivePythonFact]
    public void TheTableRendersFetchedValuesAndSurvivesAPipeInAName()
    {
        string output = RunProbe("""
            rows = mm.rows_from(payload([
                dict(complete("AAA", 1234.5, -6.25, -0.5, 1234567), shortName="Ay | Bee Corp"),
                dict(complete("BBB", 15.75, 2.5, 18.75, 900), currency="GBp"),
            ]), True, 10)
            print(mm.table(rows, True))
            """);
        // A negative row and a positive one: with only negatives the '+' flag in the
        // format string is indistinguishable from no flag, so the test claimed to check
        // an explicit sign while proving nothing about it.
        Assert.Contains("| 1 | AAA | Ay / Bee Corp | 1,234.50 | USD | -6.25 | -0.50% | 1,234,567 |",
            output, StringComparison.Ordinal);
        Assert.Contains("| 2 | BBB | BBB Inc | 15.75 | GBp | +2.50 | +18.75% | 900 |",
            output, StringComparison.Ordinal);
    }

    /// <summary>
    /// A quote is derived from the chart meta, and a row whose previous close is absent
    /// or zero yields nothing rather than a division or an invented change.
    /// </summary>
    [LivePythonFact]
    public void AQuoteIsDerivedFromTheMetaAndRefusesToDivideByAMissingClose()
    {
        string output = RunProbe("""
            # 125 from 50 is +75 and +150%: two different numbers, so an assertion on
            # both cannot pass by reading the same one twice.
            good = mm.quote_row("AAPL", {"symbol": "AAPL", "longName": "Apple Inc.",
                "currency": "USD", "regularMarketPrice": 125.0, "chartPreviousClose": 50.0,
                "regularMarketVolume": 42, "regularMarketTime": 0})
            print("PCT %.4f CHANGE %.4f NAME %s CUR %s" % (
                good["change_percent"], good["change"], good["name"], good["currency"]))
            print("NOCURRENCY " + str(mm.quote_row("X", {"regularMarketPrice": 1.0,
                "chartPreviousClose": 1.0, "regularMarketVolume": 1})))
            print("ZERO " + str(mm.quote_row("X", {"regularMarketPrice": 1.0,
                "chartPreviousClose": 0.0, "regularMarketVolume": 1})))
            print("MISSING " + str(mm.quote_row("X", {"regularMarketPrice": 1.0,
                "regularMarketVolume": 1})))
            print("NOTADICT " + str(mm.quote_row("X", None)))
            """);
        Assert.Contains("PCT 150.0000 CHANGE 75.0000 NAME Apple Inc. CUR USD", output, StringComparison.Ordinal);
        // Currency is required here exactly as it is on the screener path, so the
        // printed column can never be blank beside a number.
        Assert.Contains("NOCURRENCY None", output, StringComparison.Ordinal);
        Assert.Contains("ZERO None", output, StringComparison.Ordinal);
        Assert.Contains("MISSING None", output, StringComparison.Ordinal);
        Assert.Contains("NOTADICT None", output, StringComparison.Ordinal);
    }

    /// <summary>
    /// The spec it writes is one the documents skill accepts, and it splits at twelve
    /// rows because that is what a table slide holds.
    /// </summary>
    [LivePythonFact]
    public void TheDeckSpecIsShapedForMakePptxAndSplitsLongTables()
    {
        string output = RunProbe("""
            rows = mm.rows_from(payload([
                complete("S%02d" % i, 10.0 + i, 1.0, 5.0, 1000 + i) for i in range(20)
            ]), True, 20)
            spec = mm.deck_spec(rows, "Movers", True)
            print(json.dumps({
                "slides": len(spec["slides"]),
                "layouts": [s["layout"] for s in spec["slides"]],
                "first_rows": len(spec["slides"][1]["rows"]),
                "second_rows": len(spec["slides"][2]["rows"]),
                "columns": spec["slides"][1]["columns"],
                "widths": sorted({len(r) for s in spec["slides"][1:] for r in s["rows"]}),
                "first_row": spec["slides"][1]["rows"][0],
            }))
            """);
        string json = output[output.IndexOf('{')..(output.LastIndexOf('}') + 1)];
        using JsonDocument parsed = JsonDocument.Parse(json);
        JsonElement root = parsed.RootElement;
        Assert.Equal(3, root.GetProperty("slides").GetInt32());
        Assert.Equal("title", root.GetProperty("layouts")[0].GetString());
        Assert.Equal("table", root.GetProperty("layouts")[1].GetString());
        Assert.Equal(12, root.GetProperty("first_rows").GetInt32());
        Assert.Equal(8, root.GetProperty("second_rows").GetInt32());
        Assert.Equal("Rank", root.GetProperty("columns")[0].GetString());
        // The header and the body cells are built in two separate places, so a column
        // added to one and not the other makes a deck whose values sit under the wrong
        // headings — which no assertion on slide counts would ever notice.
        int columns = root.GetProperty("columns").GetArrayLength();
        JsonElement widths = root.GetProperty("widths");
        Assert.Equal(1, widths.GetArrayLength());
        Assert.Equal(columns, widths[0].GetInt32());
        Assert.Contains("Currency", root.GetProperty("columns").EnumerateArray()
            .Select(column => column.GetString()));
        Assert.Equal("USD", root.GetProperty("first_row")[
            root.GetProperty("columns").EnumerateArray().ToList()
                .FindIndex(column => column.GetString() == "Currency")].GetString());
    }

    /// <summary>
    /// A malformed payload is empty rows, never an exception: the script's own error
    /// path is what tells the model to say so rather than answer from memory.
    /// </summary>
    [LivePythonFact]
    public void AMalformedPayloadYieldsNoRowsRatherThanThrowing()
    {
        string output = RunProbe("""
            for bad in ({}, {"finance": None}, {"finance": {"result": []}},
                        {"finance": {"result": [{"quotes": "not a list"}]}}):
                print("ROWS " + str(len(mm.rows_from(bad, True, 10))))
            """);
        Assert.Equal(4, Regex.Matches(output, "ROWS 0").Count);
    }

    /// <summary>
    /// A table whose rows do not share a moment must not be dated by one of them.
    ///
    /// <para>
    /// The heading used to print the FIRST row's timestamp as the whole table's, which is
    /// arbitrary within one exchange and wrong across several — a Tokyo close presented
    /// as the moment a New York row was priced.
    /// </para>
    /// </summary>
    [LivePythonFact]
    public void ATableIsDatedByAllItsRowsRatherThanTheFirstOne()
    {
        string output = RunProbe("""
            same = [{"as_of": "2026-01-01 00:00:00 UTC"}, {"as_of": "2026-01-01 00:00:00 UTC"}]
            differ = [{"as_of": "2026-01-01 00:00:00 UTC"}, {"as_of": "2026-01-02 09:30:00 UTC"}]
            print("SAME " + mm.describe_stamps(same))
            print("DIFFER " + mm.describe_stamps(differ))
            print("NONE [" + mm.describe_stamps([{"as_of": ""}]) + "]")
            """);
        Assert.Contains("SAME as of 2026-01-01 00:00:00 UTC", output, StringComparison.Ordinal);
        Assert.Contains("DIFFER rows stamped between 2026-01-01 00:00:00 UTC and 2026-01-02 09:30:00 UTC",
            output, StringComparison.Ordinal);
        Assert.Contains("NONE []", output, StringComparison.Ordinal);
    }

    /// <summary>
    /// Outside the regular session the price is the last close, and a heading that says
    /// "today" without saying so implies a live number.
    /// </summary>
    [LivePythonFact]
    public void AClosedMarketIsNamedRatherThanImpliedToBeLive()
    {
        string output = RunProbe("""
            print("OPEN [" + mm.describe_session([{"market_state": "REGULAR"}]) + "]")
            print("SHUT " + mm.describe_session([{"market_state": "CLOSED"}]))
            print("POST " + mm.describe_session([{"market_state": "POST"}]))
            print("MIXED " + mm.describe_session([{"market_state": "REGULAR"}, {"market_state": "CLOSED"}]))
            print("UNKNOWN [" + mm.describe_session([{"market_state": ""}]) + "]")
            """);
        Assert.Contains("OPEN []", output, StringComparison.Ordinal);
        Assert.Contains("SHUT last market closed price", output, StringComparison.Ordinal);
        Assert.Contains("POST last after hours price", output, StringComparison.Ordinal);
        Assert.Contains("MIXED mixed session states", output, StringComparison.Ordinal);
        Assert.Contains("UNKNOWN []", output, StringComparison.Ordinal);
    }

    /// <summary>
    /// The CLI contract SKILL.md documents, exercised through main() — the argparse
    /// wiring was previously untested, so every promise in the options table rested on
    /// nothing. No network: each case must be rejected before a request is made.
    /// </summary>
    [LivePythonFact]
    public void TheDocumentedArgumentContractIsEnforcedBeforeAnyRequest()
    {
        string output = RunProbe("""
            def code(argv):
                try:
                    mm.main(argv)
                    return 0
                except SystemExit as exit:
                    return exit.code
                except BaseException as error:
                    return "RAISED " + type(error).__name__

            print("NEITHER " + str(code([])))
            print("BOTH " + str(code(["--movers", "gainers", "--quote", "AAPL"])))
            print("ZERO " + str(code(["--movers", "gainers", "--count", "0"])))
            print("TOOMANY " + str(code(["--movers", "gainers", "--count", "51"])))
            print("BADKIND " + str(code(["--movers", "nonsense"])))
            print("ELEVEN " + str(code(["--quote"] + ["A%d" % i for i in range(11)])))
            print("CAP " + str(mm.MAX_COUNT) + " SYMS " + str(mm.MAX_SYMBOLS))
            """);
        // argparse exits 2 for a usage error, which is what the docstring documents.
        foreach (string label in new[] { "NEITHER 2", "BOTH 2", "ZERO 2", "TOOMANY 2", "BADKIND 2", "ELEVEN 2" })
            Assert.Contains(label, output, StringComparison.Ordinal);
        // The request asks for 3x what is wanted, so the cap must leave headroom under
        // the endpoint's own 100-row ceiling rather than sitting on it.
        Assert.Contains("CAP 50 SYMS 10", output, StringComparison.Ordinal);
    }

    /// <summary>
    /// The app ships with its Network switch OFF, and the sandbox refuses a request with
    /// a PermissionError, which is not a URLError. Before this was handled, the very
    /// first use of the skill in the shipped configuration printed a traceback instead of
    /// the exit code and the sentence that say what to do.
    /// </summary>
    [LivePythonFact]
    public void TheDefaultNetworkOffConfigurationExitsCleanlyAndSaysWhy()
    {
        string output = RunProbe("""
            def run(raiser):
                saved = mm.urllib.request.urlopen
                mm.urllib.request.urlopen = raiser
                try:
                    mm.fetch("https://query1.finance.yahoo.com/x")
                    return "NO EXIT"
                except SystemExit as exit:
                    return "EXIT " + str(exit.code)
                except BaseException as error:
                    return "RAISED " + type(error).__name__
                finally:
                    mm.urllib.request.urlopen = saved

            def bare(*a, **k):
                raise PermissionError("urllib.Request: network access is disabled by the user")
            def wrapped(*a, **k):
                raise mm.urllib.error.URLError(PermissionError("host not on the allow-list"))

            print("BARE " + run(bare))
            print("WRAPPED " + run(wrapped))
            """);
        Assert.Contains("BARE EXIT 3", output, StringComparison.Ordinal);
        Assert.Contains("WRAPPED EXIT 3", output, StringComparison.Ordinal);
    }

    // =====================================================================================
    // driving the script through the app's own interpreter and sandbox
    // =====================================================================================

    /// <summary>
    /// Loads the script as a module and runs <paramref name="body"/> against it, through
    /// the embedded interpreter under the app's real confinement — the same way the
    /// skill runner will. `mm` is the module, `complete()` builds a full screener row and
    /// `payload()` wraps rows the way the endpoint does.
    /// </summary>
    private string RunProbe(string body)
    {
        var python = new EmbeddedPython(
            Environment.GetEnvironmentVariable(LivePythonFactAttribute.RootVariable));
        Assert.True(python.IsAvailable, python.UnavailableReason);

        string scripts = Path.GetDirectoryName(Script)!;
        var policy = new ExecutionPolicy(
            AllowScripts: true,
            AllowNetwork: false,
            WorkRoot: _root,
            ReadableRoots: new[] { scripts },
            TempRoot: _root);
        var context = new InterpreterContext(
            _root, new Dictionary<string, string> { ["HOME"] = _root }, policy);

        // No interpolation: the probe is Python and Python is made of braces.
        const string preambleTemplate = """
            import json, sys, importlib.util
            spec = importlib.util.spec_from_file_location("mm", __SCRIPT__)
            mm = importlib.util.module_from_spec(spec)
            spec.loader.exec_module(mm)

            def complete(symbol, price, change, percent, volume):
                return {"symbol": symbol, "shortName": symbol + " Inc", "currency": "USD",
                        "quoteType": "EQUITY", "regularMarketPrice": price,
                        "regularMarketChange": change, "regularMarketChangePercent": percent,
                        "regularMarketVolume": volume, "regularMarketTime": 0}

            def payload(rows):
                return {"finance": {"result": [{"quotes": rows}]}}


            """;
        string preamble = preambleTemplate.Replace("__SCRIPT__", Quote(Script), StringComparison.Ordinal);

        ExecutionResult result = python
            .RunCodeAsync(preamble + body, Array.Empty<string>(), context, CancellationToken.None)
            .GetAwaiter().GetResult();
        Assert.True(result.ExitCode == 0,
            $"the probe failed:{Environment.NewLine}{result.Stdout}{result.Stderr}");
        return result.Stdout;
    }

    private static string Quote(string value) => "\"" + value.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";

    private static string FindRepoRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (Directory.Exists(Path.Combine(directory.FullName, "TensorAgent", "skills")))
                return directory.FullName;
            directory = directory.Parent;
        }
        throw new InvalidOperationException("the repository root was not found above " + AppContext.BaseDirectory);
    }
}
