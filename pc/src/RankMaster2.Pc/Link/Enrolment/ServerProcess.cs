using System.Diagnostics;

namespace RankMaster2.Pc.Link.Enrolment;

/// <summary>
/// Starts the server host and forgets it (§ 5.2.4). The link never stops, kills, or waits on the
/// server: the Tray's single-instance mutex makes a duplicate start harmless, and the server must
/// outlive the client so the phone can keep using it after the PC closes.
/// </summary>
internal static class ServerProcess
{
    /// <summary>Returns the started <see cref="Process"/> only so a test harness can adopt it for
    /// cleanup (§ 6.3 C4); production code never inspects or awaits it.</summary>
    public static Process? Start(string executable, IReadOnlyList<string> arguments)
    {
        try
        {
            var start = new ProcessStartInfo(executable)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = false,
                RedirectStandardError = false,
            };
            foreach (var argument in arguments) start.ArgumentList.Add(argument);

            return Process.Start(start);
        }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or IOException)
        {
            return null;
        }
    }
}
