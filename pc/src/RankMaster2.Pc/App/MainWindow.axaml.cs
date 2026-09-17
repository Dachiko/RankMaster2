namespace RankMaster2.Pc.App;

using Avalonia.Controls;

/// <summary>
/// A-startup-and-shell.md § 3.1, § 3.4. The shell: full screen, black, hosts part E's root
/// (<see cref="RankMaster2.Pc.Ui.Views.UiRoot"/>, now that Ui/ has landed). Handles no keys itself
/// — Esc is E's <c>UiRoot.QuitRequested</c>, wired by <see cref="Composition"/>.
/// </summary>
public partial class MainWindow : Window
{
    private readonly CompositionResult? _services;

    /// <summary>For the Avalonia designer only; production and tests use the other constructor.</summary>
    public MainWindow() : this(null)
    {
    }

    public MainWindow(CompositionResult? services)
    {
        InitializeComponent();
        _services = services;
        if (services is not null)
            Content = services.Root;

        Opened += OnOpened;
    }

    private void OnOpened(object? sender, EventArgs e)
    {
        StartupClock.Mark("window_opened");

        var topLevel = TopLevel.GetTopLevel(this);
        topLevel?.RequestAnimationFrame(frameTime =>
        {
            StartupClock.Mark("first_frame");
            StartupClock.AssertNoVideoEngine();

            // AUDIT2.md § 3.1: this used to call Link.ConnectAsync directly, which connects but does
            // none of what RankCoordinator.InitializeAsync does on top of that — load the last
            // folder so "Resume" can appear, and raise Changed so the start screen repaints once the
            // connect settles instead of showing "Not connected" forever. Before this fix nothing in
            // the whole program called InitializeAsync at all.
            if (_services is not null)
                _ = System.Threading.Tasks.Task.Run(() => _services.InitializeAsync(default));
        });
    }
}
