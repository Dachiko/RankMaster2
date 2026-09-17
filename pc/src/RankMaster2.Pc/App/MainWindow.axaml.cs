namespace RankMaster2.Pc.App;

using Avalonia.Controls;

/// <summary>
/// A-startup-and-shell.md § 3.1, § 3.4. The shell: full screen, black, hosts part E's root (a
/// <see cref="Seams.PlaceholderRoot"/> until Ui/ lands). Handles no keys itself — Esc is E's
/// <c>UiRoot.QuitRequested</c>, wired by <see cref="Composition"/>.
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

            if (_services is not null)
                _ = System.Threading.Tasks.Task.Run(() => _services.Link.ConnectAsync());
        });
    }
}
