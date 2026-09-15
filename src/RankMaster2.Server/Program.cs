// The console host. Everything it builds lives in Rm2Host, because RankMaster2.Tray.exe builds the
// same application and the two must never drift apart.
RankMaster2.Server.Rm2Host.Build(args).Run();

// WebApplicationFactory<Program> in the test projects needs this type to be public and to live in
// the entry-point assembly.
public partial class Program;
