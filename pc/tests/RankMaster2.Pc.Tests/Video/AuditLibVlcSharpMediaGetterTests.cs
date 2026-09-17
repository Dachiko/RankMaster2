// AUDIT throwaway test (not production). LibVlcPlayer (Video/Backend/LibVlcBackend.cs) reads
// MediaPlayer.Media in ReleaseMedia() ("ReferenceEquals(_player.Media, _media)") and in Statistics()
// once a second per surface. In LibVLCSharp the Media getter constructs a NEW managed Media wrapper
// around libvlc_media_player_get_media (which retains a native reference) on every call; the wrapper
// is never disposed by LibVlcPlayer. This test inspects the shipped assembly (no libvlc needed): the
// getter's IL contains a `newobj Media(IntPtr)`, and Media has no finalizer, so each call is a
// native reference that is only released if someone calls Dispose — nobody does.
using System.Reflection;
using LibVLCSharp.Shared;
using Xunit;

namespace RankMaster2.Pc.Tests.Video;

public sealed class AuditLibVlcSharpMediaGetterTests
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
