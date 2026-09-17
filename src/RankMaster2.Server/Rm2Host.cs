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
        builder.Services.AddSingleton<Media.IMediaSessionAccessor>(services =>
            new Media.DelegatingMediaSessionAccessor(
                () => (services.GetService<Sessions.SessionRegistry>()
                       ?? Sessions.SessionRegistry.Shared).CurrentForMedia));

        // The other seam: GET /ping reports whether a session is open, which folder, its id and its
        // state (§ 14). The registry answers all four without taking the session gate, so the ping
        // stays cheap to poll. Registering it here is the line that was missing — without it the
        // security layer fell back to a reflection bridge that could only ever fill in `folder`
        // (A4, C15). Resolved from DI so a test that registers its own registry gets its own answer.
        builder.Services.AddSingleton<Security.ISessionStatusProvider>(services =>
            services.GetService<Sessions.SessionRegistry>() ?? Sessions.SessionRegistry.Shared);

        configure?.Invoke(builder);

        var app = builder.Build();

        // A3: close the open session on the way out, so <folder>/.rankmaster.lock is deleted rather
        // than left sitting among the owner's photographs. The OS frees the handle when the process
        // dies; nothing but FolderLock.Dispose removes the file.
        var registry = app.Services.GetService<Sessions.SessionRegistry>();
        if (registry is null)
        {
            // The process-wide session (§ 7). Configuration reaches it here, because it is a static
            // and cannot read appsettings.json for itself. A registry supplied through DI is a test's
            // own, built with the catalog, the journal writer and the durability settings that test
            // needs: configuration must not reach in and change them underneath it.
            registry = Sessions.SessionRegistry.Shared;

            var durability = new Security.Rm2SecurityOptions();
            app.Configuration.GetSection(Security.Rm2SecurityOptions.SectionName).Bind(durability);

            // SERVER_SPEC.md § 2.4, § 13.1: RankMaster2:SaveDelaySeconds and
            // RankMaster2:MaxUnsavedChoices. Neither is on /ping and neither is a feature; they bound
            // what a crash can cost, and 0 restores save-on-every-choice exactly.
            registry.ApplyDurabilityOptions(durability.SaveDelaySeconds, durability.MaxUnsavedChoices);
        }

        app.Lifetime.ApplicationStopping.Register(registry.Dispose);

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
