using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace RankMaster2.Pc.Ui.Views;

/// <summary>Plan § 1.2: one toast slot above the strip. A new toast replaces the text and restarts
/// the clock -- <see cref="Surface.RankModel.Toast"/> already carries the expiry, this only reflects it.</summary>
public partial class Toast : UserControl
{
    private readonly TextBlock _text;

    public Toast()
    {
        AvaloniaXamlLoader.Load(this);
        _text = this.FindControl<TextBlock>("Text")!;
    }

    public void Render((string Text, DateTimeOffset ExpiresAt)? toast, DateTimeOffset now)
    {
        var visible = toast is not null && now < toast.Value.ExpiresAt;
        IsVisible = visible;
        if (visible) _text.Text = toast!.Value.Text;
    }
}
