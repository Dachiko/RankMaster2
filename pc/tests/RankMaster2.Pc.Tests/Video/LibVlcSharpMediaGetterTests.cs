// A26 / G-audit-remediation.md § 3.8 PC-VIDEO item 1: kept as the proof of the library fact behind
// the fix, renamed off "Audit" now that it documents a fact rather than an unfixed defect (the
// standing rule: no test keeps Audit/AUDIT in its name once it asserts fixed behaviour). The defect
// itself — LibVlcBackend.Statistics() read MediaPlayer.Media once a second per surface, and
// LibVLCSharp's Media getter constructs a NEW managed Media wrapper (retaining a native reference)
// on every call, never disposed by the old code — is fixed by Statistics() now doing
// `using var media = _player.Media;`. This test is unaffected by that fix: it inspects the shipped
// LibVLCSharp assembly itself (no libvlc needed), proving the getter still allocates the way the fix
// assumes, so a future LibVLCSharp upgrade that changes this shape is caught here rather than
// silently reopening the leak.
using System.Reflection;
using LibVLCSharp.Shared;
using Xunit;

namespace RankMaster2.Pc.Tests.Video;

public sealed class LibVlcSharpMediaGetterTests
{
    [Fact]
    public void MediaPlayer_Media_getter_constructs_a_new_wrapper_each_call_and_Media_has_no_finalizer()
    {
        var getter = typeof(MediaPlayer).GetProperty(nameof(MediaPlayer.Media))!.GetGetMethod()!;
        var il = getter.GetMethodBody()!.GetILAsByteArray()!;
        var module = getter.Module;

        var newobjTargets = new List<string>();
        for (var i = 0; i < il.Length - 4; i++)
        {
            if (il[i] != 0x73) continue; // newobj
            var token = BitConverter.ToInt32(il, i + 1);
            try
            {
                var member = module.ResolveMember(token);
                newobjTargets.Add($"{member.DeclaringType?.FullName}::{member.Name}");
            }
            catch (ArgumentException) { }
        }

        Assert.Contains(newobjTargets, t => t.StartsWith("LibVLCSharp.Shared.Media::", StringComparison.Ordinal));

        var finalizer = typeof(Media).GetMethod("Finalize", BindingFlags.NonPublic | BindingFlags.Instance);
        var declaredOnMediaOrBase = finalizer is not null && finalizer.DeclaringType != typeof(object);
        Assert.False(declaredOnMediaOrBase, "Media (or its LibVLCSharp base) declares a finalizer; a dropped wrapper would eventually be released");

        // For the report: what the getter allocates.
        Assert.NotEmpty(newobjTargets);
    }
}
