using System.Text.RegularExpressions;

namespace RankMaster2.Cli;

/// <summary>
/// What rm2ctl prints, and the record of what passed.
///
/// This tool exists so the server can be exercised without a phone, which makes its output the
/// product. Someone reading a failed run needs to see what was asked, what came back, and which
/// clause of the contract the two disagreed about — in that order, without re-running with a
/// verbose flag.
/// </summary>
public sealed class Journal(bool verbose)
{
    private readonly List<string> _failures = new();
    private int _passed;
    private string _step = "";

    public bool Verbose { get; } = verbose;
    public int Passed => _passed;
    public int Failed => _failures.Count;
    public bool AnythingFailed => _failures.Count > 0;

    public void Title(string text)
    {
        Console.WriteLine();
        Console.WriteLine(text);
        Console.WriteLine(new string('=', text.Length));
    }

    /// <summary>Begin a step — one logical thing the tool is doing.</summary>
    public void Step(string name)
    {
        _step = name;
        Console.WriteLine();
        Console.WriteLine("  " + name);
    }

    public void Request(string method, string url, string? body = null)
    {
        Console.WriteLine($"    -> {method} {url}");
        if (body is not null && Verbose) Console.WriteLine($"       {Summarise(body, 300)}");
    }

    /// <summary>The response line, with a one-line summary of what came back.</summary>
    public void Response(int status, string summary)
    {
        Console.WriteLine($"    <- {status}  {summary}");
    }

    /// <summary>Extra lines of detail under a response.</summary>
    public void Detail(string text)
    {
        foreach (var line in text.Split('\n'))
            Console.WriteLine("       " + line);
    }

    public void Note(string text) => Console.WriteLine("    .. " + text);

    /// <summary>Record a check. <paramref name="clause"/> names the promise being checked.</summary>
    public bool Check(bool condition, string clause, string? detail = null)
    {
        if (condition)
        {
            _passed++;
            if (Verbose) Console.WriteLine($"    ok    {clause}");
            return true;
        }

        _failures.Add($"[{_step}] {clause}" + (detail is null ? "" : $"\n  {detail}"));

        Console.WriteLine($"    FAIL  {clause}");
        if (detail is not null)
            foreach (var line in detail.Split('\n'))
                Console.WriteLine("          " + line);

        return false;
    }

    public void Failure(string clause, string? detail = null) => Check(false, clause, detail);

    public void Ok(string summary)
    {
        Console.WriteLine("    ok    " + summary);
        _passed++;
    }

    /// <summary>Print the tally. Returns the process exit code.</summary>
    public int Summary()
    {
        Console.WriteLine();
        Console.WriteLine(new string('-', 74));

        if (_failures.Count == 0)
        {
            Console.WriteLine($"  {_passed} checks passed. The server matched the contract on everything run here.");
            Console.WriteLine();
            return 0;
        }

        Console.WriteLine($"  {_passed} checks passed, {_failures.Count} failed.");
        Console.WriteLine();
        Console.WriteLine("  Failures, in the order they happened:");
        foreach (var failure in _failures)
        {
            Console.WriteLine();
            foreach (var line in failure.Split('\n'))
                Console.WriteLine("    " + line);
        }

        Console.WriteLine();
        return 1;
    }

    public static string Trim(string text, int limit) =>
        text.Length <= limit ? text : text[..limit] + $"... (+{text.Length - limit} chars)";

    /// <summary>
    /// A payload, shortened for one line — except that the trim never cuts a credential in half.
    /// A device token or a pairing code is printed <b>once</b>, in full, by the command that obtained
    /// it; a summary line that carried a truncated copy of the same token above the real one cost the
    /// media fork a whole run of copying the wrong string (K3). So a body or a response that carries
    /// <c>token</c> or <c>code</c> has that value replaced here, and the full value is printed by the
    /// command itself.
    /// </summary>
    public static string Summarise(string text, int limit) =>
        Trim(CarriesCredential(text) ? Redact(text) : text, limit);

    /// <summary>Does this JSON payload carry a device token or a pairing code?</summary>
    public static bool CarriesCredential(string text) =>
        CredentialField.IsMatch(text);

    private static readonly Regex CredentialField =
        new("\"(token|code)\"\\s*:\\s*\"[^\"]*\"", RegexOptions.IgnoreCase);

    private static string Redact(string text) =>
        CredentialField.Replace(text, match =>
            match.Value[..match.Value.IndexOf('"', 1)] + "\": \"(printed in full below)\"");
}
