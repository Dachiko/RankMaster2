namespace RankMaster2.Pc.Video.Backend;

/// <summary>The engine and per-media option strings of D-video.md § 5.2, and nowhere else — Phase 1's
/// scope is "ported from VlcRuntime.cs and VlcFramePlayer.cs with the changes this plan names and no
/// others".</summary>
internal static class LibVlcOptions
{
    /// <summary>
    /// <c>new LibVLC(...)</c>'s argument list: the old <c>VlcRuntime</c> list with two changes —
    /// <c>--no-stats</c> deleted (§ 4.3's LoadWatch needs <c>Media.Statistics</c>) and
    /// <c>--quiet</c> replaced with <c>--verbose=1</c>, the one change § 5.2 pre-authorises without a
    /// fresh measurement: the container run of § 6.1/6.2 test 4/row 4 (a forced failure's Detail must
    /// be non-empty) found <c>--quiet</c> genuinely silent under libvlc 3.0.23 for a malformed mp4
    /// (no "moov atom not found" line, nothing at all reached <c>LibVLC.Log</c>) — exactly the
    /// contingency § 5.2 named. <c>--verbose=1</c> (warnings and errors) fixed it; verified against
    /// the same container.
    /// </summary>
    /// <param name="pluginsDir">Null on Linux, where the system's own libvlc finds its plugins without
    /// help (D-video.md § 6.2); <c>libvlc\win-x64\plugins</c> on the shipped Windows layout.</param>
    public static string[] EngineArgs(string? pluginsDir)
    {
        var args = new List<string>
        {
            "--intf=dummy",
            "--aout=dummy",
            "--no-video-title-show",
            "--no-osd",
            "--verbose=1",
            "--avcodec-hw=none",
            "--drop-late-frames",
            "--skip-frames"
        };

        if (pluginsDir is not null)
            args.Add("--plugin-path=" + pluginsDir);

        return args.ToArray();
    }

    /// <summary>
    /// <c>Media.AddOption</c>'s list per media — the old <c>VlcFramePlayer.Play</c> list verbatim,
    /// plus the optional dav1d thread cap rm2vidprobe uses to try a value on the owner's PC without a
    /// rebuild (<c>VideoOptions.Dav1dThreads</c>, 0 = auto = unchanged from the old app).
    /// </summary>
    public static IEnumerable<string> MediaOptions(VideoOptions options)
    {
        yield return ":no-audio";
        yield return ":input-repeat=65535";
        yield return ":avcodec-hw=none";
        if (options.Dav1dThreads > 0)
            yield return $":dav1d-thread-frames={options.Dav1dThreads}";
    }
}
