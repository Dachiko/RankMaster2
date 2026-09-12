var builder = WebApplication.CreateBuilder(args);
var app = builder.Build();

// TLS, pairing, tokens, the fail-closed auth gate, /ping and /libraries/*.
// Must come first: the middleware it installs guards every route mapped below.
// Routes and everything behind them live in Security/.
RankMaster2.Server.Security.SecurityEndpoints.UseRankMaster2Security(app);

// The /session* group (SERVER_SPEC.md § 10). Routes live in Sessions/SessionEndpoints.cs.
RankMaster2.Server.Sessions.SessionEndpoints.MapSessionEndpoints(app);

// The /media/* group (SERVER_SPEC.md § 12). Routes and everything behind them live in Media/.
RankMaster2.Server.Media.MediaEndpoints.MapMediaEndpoints(app);

app.Run();

public partial class Program;
