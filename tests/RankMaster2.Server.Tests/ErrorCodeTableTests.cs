using System.Text.RegularExpressions;
using RankMaster2.Server.Contracts;
using RankMaster2.Server.Tests.Harness;
using Xunit;

namespace RankMaster2.Server.Tests;

/// <summary>
/// The error table exists three times — in SERVER_SPEC.md § 5, in openapi.yaml's `ErrorCode` enum,
/// and in the server's own <see cref="ErrorCodes"/> — and a client's error handling is only as good
/// as the agreement between them. These tests are the agreement.
/// </summary>
public class ErrorCodeTableTests
{
    [Fact]
    public void The_servers_code_table_matches_the_contract()
    {
        var contract = ErrorStatuses.All;
        var server = ErrorCodes.All;

        var missing = contract.Keys.Where(code => !server.ContainsKey(code)).OrderBy(c => c).ToArray();
        var extra = server.Keys.Where(code => !contract.ContainsKey(code)).OrderBy(c => c).ToArray();

        var wrongStatus = contract
            .Where(entry => server.TryGetValue(entry.Key, out var status) && status != entry.Value)
            .Select(entry => $"{entry.Key}: contract says {entry.Value}, server says {server[entry.Key]}")
            .ToArray();

        Assert.True(missing.Length == 0, "Codes in SERVER_SPEC.md § 5 the server does not define: " + string.Join(", ", missing));
        Assert.True(extra.Length == 0,
            "Codes the server defines that SERVER_SPEC.md § 5 does not. New codes may be added, but the " +
            "spec has to gain them first: " + string.Join(", ", extra));
        Assert.True(wrongStatus.Length == 0,
            "A code's status is part of the contract and must not change:\n  " + string.Join("\n  ", wrongStatus));
    }

    [Fact]
    public void The_openapi_enum_lists_the_same_codes()
    {
        var text = File.ReadAllText(Repo.OpenApi);

        // The ErrorCode schema's enum block: every "- code" line until the indentation breaks.
        var match = Regex.Match(text, @"ErrorCode:.*?\n      enum:\n((?:\s+- \w+\n)+)", RegexOptions.Singleline);
        Assert.True(match.Success, "Could not find the ErrorCode enum in openapi.yaml.");

        var declared = Regex.Matches(match.Groups[1].Value, @"- (\w+)")
            .Select(m => m.Groups[1].Value)
            .ToHashSet();

        var contract = ErrorStatuses.All.Keys.ToHashSet();

        var missing = contract.Except(declared).OrderBy(c => c).ToArray();
        var extra = declared.Except(contract).OrderBy(c => c).ToArray();

        Assert.True(missing.Length == 0, "In SERVER_SPEC.md § 5 but not in openapi.yaml: " + string.Join(", ", missing));
        Assert.True(extra.Length == 0, "In openapi.yaml but not in SERVER_SPEC.md § 5: " + string.Join(", ", extra));
    }

    [Fact]
    public void Every_code_has_exactly_the_forty_three_the_spec_defines()
    {
        // A count is a crude assertion, but it catches the case where a code is added to all three
        // places at once without anyone deciding it belongs there. 40 plus the three § 10.16 rename
        // codes added 2026-09-16 (rename_in_progress, no_rename_operation, rename_failed).
        Assert.Equal(43, ErrorStatuses.All.Count);
    }

    [Theory]
    [InlineData("unauthenticated", 401)]
    [InlineData("stale_pair_token", 409)]
    [InlineData("no_session", 404)]
    [InlineData("unsupported_width", 400)]
    [InlineData("media_outside_session", 403)]
    [InlineData("media_decode_failed", 422)]
    [InlineData("folder_locked", 423)]
    [InlineData("range_not_satisfiable", 416)]
    [InlineData("session_busy", 503)]
    [InlineData("rename_in_progress", 409)]
    [InlineData("no_rename_operation", 404)]
    [InlineData("rename_failed", 500)]
    public void Spot_checks_against_the_prose_table(string code, int status) =>
        Assert.Equal(status, ErrorStatuses.Of(code));
}
