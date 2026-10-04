using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Logging;
using RankMaster2.Server.Sessions;

namespace RankMaster2.Tray;

/// <summary>
/// The notification-area icon and its menu. Deliberately small: the phone is the interface, and
/// anything that needs a screen on the PC is a thing the phone should have been able to do.
/// <para/>
/// The icon says one thing (a red left pane while a folder is open) and the menu does the two things
/// the owner cannot do from the phone: hand the phone a pairing credential (Show QR, also a single
/// left click on the icon) and stop the server (Exit). There are no balloons and no warnings (plan H
/// § 2.2.2): the server's log records what needs recording.
/// </summary>
internal sealed class TrayApp : IDisposable
{
    private readonly string _dataDirectory;
    private readonly ILogger _logger;
    private readonly NotifyIcon _icon;
    private readonly ContextMenuStrip _menu;
    private readonly Icon _idleIcon;
    private readonly Icon _openIcon;
    private readonly Font _defaultFont;
    private readonly Font _regularFont;
    private readonly System.Windows.Forms.Timer _poll;
    private bool _refreshInFlight;
    private bool _folderOpen;
    private bool _disposed;
    private PairingForm? _pairing;

    public TrayApp(WebApplication app, string dataDirectory)
    {
        _dataDirectory = dataDirectory;
        _logger = app.Logger;

        // Two icons, made once and swapped; a swap is then a handle change, not a redraw.
        _idleIcon = TrayArt.CreateIcon(folderOpen: false);
        _openIcon = TrayArt.CreateIcon(folderOpen: true);

        _defaultFont = House.CreateFont(House.Weight.SemiBold, 13);
        _regularFont = House.CreateFont(House.Weight.Regular, 13);

        _menu = HouseMenu.Create(_regularFont, 180,
            ("Show QR", (_, _) => ShowPairing()),
            (null, null),
            ("Exit", (_, _) => Exit()));
        // Show QR is the default action, and says so by its weight.
        _menu.Items[0].Font = _defaultFont;

        _icon = new NotifyIcon
        {
            Icon = _idleIcon,
            Text = "Rank Master 3",
            ContextMenuStrip = _menu,
            Visible = true,
        };

        // A single left click is Show QR. The right button is left to NotifyIcon, which opens the
        // menu on its own.
        _icon.MouseClick += (_, e) =>
        {
            if (e.Button == MouseButtons.Left)
                ShowPairing();
        };

        // One second is imperceptible for an icon colour. The read usually costs one lock-free
        // field, but its rare contended path can block, so it runs off this thread (A34).
        _poll = new System.Windows.Forms.Timer { Interval = 1000 };
        _poll.Tick += (_, _) => RefreshSession();
        _poll.Start();
        RefreshSession();
    }

    /// <summary>
    /// Asks the server for a pairing window and shows it. The request goes through the sentinel
    /// file in the data directory — the channel SERVER_SPEC.md § 10.1.1 defines — rather than a
    /// direct call, even though the server is in this very process. That channel is what proves the
    /// asker controls the owner's OS account, it is already covered by the server's tests, and
    /// using it here means the tray keeps working unchanged if the server ever moves to a service.
    /// </summary>
    private void ShowPairing()
    {
        if (_pairing is { IsDisposed: false })
        {
            _pairing.Activate();
            return;
        }

        _pairing = new PairingForm(_dataDirectory);
        _pairing.FormClosed += (_, _) => _pairing = null;
        _pairing.Show();
        _pairing.Activate();
    }

    /// <summary>
    /// Stops the message loop; <c>Program</c> then shuts the server down gracefully. No
    /// confirmation prompt and no "unsaved work" warning: there is never any. Every 2xx the server
    /// sent was on disk before it was sent (SERVER_SPEC.md § 13.1), so the worst an exit can cost
    /// is the pair currently on the phone's screen.
    /// </summary>
    private void Exit()
    {
        _icon.Visible = false;
        Application.ExitThread();
    }

    /// <summary>
    /// Reads whether a folder is open off the UI thread and swaps the icon when the answer changes.
    /// <see cref="SessionRegistry.CurrentForMedia"/> is usually a lock-free field read, but its
    /// contended fallback can wait up to five seconds and then throw <see
    /// cref="TimeoutException"/> (A34) — on the timer tick that would freeze the whole menu for
    /// as long as it took. A poll already in flight is skipped rather than piled up, and a failed
    /// read keeps the icon as it was.
    /// </summary>
    private async void RefreshSession()
    {
        if (_refreshInFlight || _disposed)
            return;

        _refreshInFlight = true;
        try
        {
            var open = await Task.Run(() =>
            {
                try
                {
                    return (bool?)(SessionRegistry.Shared.CurrentForMedia is not null);
                }
                catch (Exception e)
                {
                    _logger.LogError(e, "Reading the ranking session for the tray failed; keeping the previous icon.");
                    return null;
                }
            });

            if (_disposed || open is not { } value || value == _folderOpen)
                return;

            _folderOpen = value;
            _icon.Icon = value ? _openIcon : _idleIcon;
        }
        finally
        {
            _refreshInFlight = false;
        }
    }

    public void Dispose()
    {
        _disposed = true;
        _poll.Stop();
        _poll.Dispose();
        _pairing?.Dispose();
        _icon.Visible = false;
        _icon.Dispose();
        _menu.Dispose();
        _idleIcon.Dispose();
        _openIcon.Dispose();
        _defaultFont.Dispose();
        _regularFont.Dispose();
    }
}
