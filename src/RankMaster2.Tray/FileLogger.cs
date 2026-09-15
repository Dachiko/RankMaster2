using System.Text;
using Microsoft.Extensions.Logging;

namespace RankMaster2.Tray;

/// <summary>
/// A small file log, because a tray app has no console and the server's startup line — the address
/// it bound and the certificate fingerprint it loaded — is the first thing anyone needs when a phone
/// will not connect.
/// <para/>
/// One file per day, the last seven kept. Nothing here may throw: a server that will not start
/// because it could not write a log line would be a poor trade.
/// </summary>
internal sealed class FileLoggerProvider(string directory) : ILoggerProvider
{
    private readonly object _gate = new();
    private string _currentPath = "";
    private DateOnly _currentDay;

    public ILogger CreateLogger(string categoryName) => new FileLogger(this, categoryName);

    public void Dispose()
    {
    }

    private void Write(string line)
    {
        lock (_gate)
        {
            try
            {
                var today = DateOnly.FromDateTime(DateTime.Now);
                if (_currentPath.Length == 0 || today != _currentDay)
                {
                    Directory.CreateDirectory(directory);
                    _currentPath = Path.Combine(directory, $"server-{today:yyyy-MM-dd}.log");
                    _currentDay = today;
                    Prune();
                }

                File.AppendAllText(_currentPath, line + Environment.NewLine, Encoding.UTF8);
            }
            catch (Exception)
            {
                // A full disk, a locked file, a data directory that has been deleted underneath us.
                // None of them is a reason to take the server down.
            }
        }
    }

    private void Prune()
    {
        try
        {
            var stale = new DirectoryInfo(directory)
                .GetFiles("server-*.log")
                .OrderByDescending(f => f.Name)
                .Skip(7);

            foreach (var file in stale)
                file.Delete();
        }
        catch (Exception)
        {
        }
    }

    private sealed class FileLogger(FileLoggerProvider provider, string category) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        // Information and above. Debug from the whole ASP.NET stack would bury the four lines
        // that matter.
        public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Information;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(logLevel))
                return;

            var builder = new StringBuilder()
                .Append(DateTimeOffset.Now.ToString("yyyy-MM-dd HH:mm:ss.fff zzz"))
                .Append("  ")
                .Append(Level(logLevel))
                .Append("  ")
                .Append(category)
                .Append("  ")
                .Append(formatter(state, exception));

            if (exception is not null)
                builder.Append(Environment.NewLine).Append(exception);

            provider.Write(builder.ToString());
        }

        private static string Level(LogLevel level) => level switch
        {
            LogLevel.Critical => "CRIT",
            LogLevel.Error => "FAIL",
            LogLevel.Warning => "WARN",
            LogLevel.Information => "INFO",
            _ => "DBUG",
        };
    }
}
