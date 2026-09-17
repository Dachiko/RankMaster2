using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using RankMaster2.Server;
using RankMaster2.Server.Security;

namespace RankMaster2.Tray;

/// <summary>
/// The Windows host: the same server as <c>RankMaster2.Server.exe</c>, with no console window and a
/// notification-area icon instead.
/// <para/>
/// The server is hosted <b>in this process</b>, not launched as a child. One process means Exit is a
/// real shutdown rather than a kill, the folder lock is released on the way out, and there is no
/// orphaned server left listening when the icon disappears.
/// </summary>
internal static class Program
{
    /// <summary>Kestrel gets this long to stop before the process leaves anyway.</summary>
    private static readonly TimeSpan ShutdownGrace = TimeSpan.FromSeconds(5);

    [STAThread]
    private static int Main(string[] args)
    {
        // One server, one port. A second instance would fail to bind and the owner would be left
        // with two icons and one working server, which is worse than a plain refusal.
        using var single = new Mutex(initiallyOwned: true, @"Local\RankMaster2.Tray.Single", out var isOnlyInstance);
        if (!isOnlyInstance)
        {
            MessageBox.Show(
                "Rank Master 3 is already running. Look for its icon in the notification area.",
                "Rank Master 3", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return 0;
        }

        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        Application.SetHighDpiMode(HighDpiMode.SystemAware);

        var dataDirectory = "";
        WebApplication app;
        try
        {
            app = Rm2Host.Build(args, builder =>
            {
                // Resolved from the very configuration the server is about to read, so the log
                // lands beside the certificate and the token store even when the data directory
                // has been moved.
                var options = new Rm2SecurityOptions();
                builder.Configuration.GetSection(Rm2SecurityOptions.SectionName).Bind(options);
                dataDirectory = options.ResolveDataDirectory();

                // There is no console to log to. Without this the tray build is the one build
                // nobody can diagnose.
                builder.Logging.AddProvider(new FileLoggerProvider(Path.Combine(dataDirectory, "logs")));
            });

            app.StartAsync().GetAwaiter().GetResult();
        }
        catch (Exception e)
        {
            // AUDIT2.md § 2.3: this used to guess "another copy already running" for every
            // SocketException-or-IOException, which is wrong for two of the five ways startup can
            // fail (an unreadable certificate, an unwritable data directory) and sent the owner
            // looking for a second copy of the program that did not exist. Rm2Host.DescribeStartupFailure
            // is shared with the console host so the two never disagree about the same failure, and it
            // only names a cause it can actually support from the exception — never a guess.
            var detail = Rm2Host.DescribeStartupFailure(e);
            TryLogStartupFailure(dataDirectory, e, detail);

            MessageBox.Show(
                detail,
                "Rank Master 3", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return 1;
        }

        using var tray = new TrayApp(app, dataDirectory);
        Application.Run();

        try
        {
            using var grace = new CancellationTokenSource(ShutdownGrace);
            app.StopAsync(grace.Token).GetAwaiter().GetResult();
        }
        catch (Exception)
        {
            // Nothing is lost by a rough exit: every response the server has already sent was
            // durable before it was sent (SERVER_SPEC.md § 13.1). The folder lock is released with
            // the process either way.
        }

        return 0;
    }

    /// <summary>
    /// SERVER_RUNNING.md § 12's last resort is "Nothing to go on → logs\server-*.log" — but a
    /// start-up failure this early never reaches the point where the server's own logger is wired
    /// up, so without this the log would be silent about the one thing he most needs it for. Uses
    /// the same <see cref="FileLoggerProvider"/> and the same file-per-day naming as a normal run,
    /// so it is the same log SERVER_RUNNING.md already points him to — not a second, undocumented one.
    /// Never throws: a crash dialog that itself crashes would be a worse trade than no log line.
    /// </summary>
    private static void TryLogStartupFailure(string dataDirectory, Exception e, string detail)
    {
        if (string.IsNullOrWhiteSpace(dataDirectory)) return;

        try
        {
            using var provider = new FileLoggerProvider(Path.Combine(dataDirectory, "logs"));
            var logger = provider.CreateLogger("Startup");
            logger.LogCritical(e, "{Detail}", detail);
        }
        catch (Exception)
        {
        }
    }
}
