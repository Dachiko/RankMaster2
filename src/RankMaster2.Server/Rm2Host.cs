namespace RankMaster2.Server;

/// <summary>
/// Builds the server exactly as <c>Program.cs</c> does, so a second host can run the identical
/// application. There are two hosts: the console <c>RankMaster2.Server.exe</c>, and
/// <c>RankMaster2.Tray.exe</c>, which puts the same server behind a notification-area icon on
/// Windows.
/// <para/>
/// Neither host may configure the server differently. Everything that decides how the server
/// behaves — TLS, the bind address, pairing, the auth gate — lives in
/// <see cref="Security.SecurityEndpoints.UseRankMaster2Security"/> and is called from here, once,
/// for both. <paramref name="configure"/> exists for the things a host legitimately owns and the
/// server cannot know: the tray has no console, so it adds a file log.
/// </summary>
public static class Rm2Host
{
    public static WebApplication Build(string[] args, Action<WebApplicationBuilder>? configure = null)
    {
        // The content root is where appsettings.json is looked for, and it defaults to the working
        // directory. For a server started from a shortcut, a scheduled task or the Startup folder,
        // that is not where the exe lives - so a config file placed beside the exe was read when
        // you double-clicked it and silently ignored when Windows launched it, and the only symptom
        // was a server still on loopback that no phone could reach.
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            Args = args,
            ContentRootPath = AppContext.BaseDirectory,
        });

        // The one seam between the session and media layers, and the only wiring neither of them
        // could do from inside its own folder. Media asks "is a session open, and is this id one of
        // its records?"; the registry answers without taking the session gate, because a GET must
        // not queue behind a vote.
        builder.Services.AddSingleton<Media.IMediaSessionAccessor>(_ =>
            new Media.DelegatingMediaSessionAccessor(
                () => Sessions.SessionRegistry.Shared.CurrentForMedia));

        configure?.Invoke(builder);

        var app = builder.Build();

        // TLS, pairing, tokens, the fail-closed auth gate, /ping and /libraries/*.
        // Must come first: the middleware it installs guards every route mapped below.
        // Routes and everything behind them live in Security/.
        Security.SecurityEndpoints.UseRankMaster2Security(app);

        // The /session* group (SERVER_SPEC.md § 10). Routes live in Sessions/SessionEndpoints.cs.
        Sessions.SessionEndpoints.MapSessionEndpoints(app);

        // The /session/rename group (SERVER_SPEC.md § 10.16). Routes live in Sessions/RenameEndpoints.cs.
        Sessions.RenameEndpoints.MapRenameEndpoints(app);

        // The /media/* group (SERVER_SPEC.md § 12). Routes and everything behind them live in Media/.
        Media.MediaEndpoints.MapMediaEndpoints(app);

        return app;
    }
}
