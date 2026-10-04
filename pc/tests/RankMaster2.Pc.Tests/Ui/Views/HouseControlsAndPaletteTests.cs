using System.Text.RegularExpressions;
using Avalonia;
using RankMaster2.Pc.Ui.Views;
using Xunit;

namespace RankMaster2.Pc.Ui.Tests.Views;

/// <summary>
/// Plan H § 1's palette rule ("no other colour in new or changed files") as a test, and the pure maths
/// of the house controls. The palette test reads the source files plan H adds or changes, so a stray
/// <c>#18181B</c> or <c>White</c> pasted in later fails here, not on Mike's screen.
/// </summary>
public class HouseControlsAndPaletteTests
{
    // ---- the palette (plan H § 1) ---------------------------------------------------------------

    /// <summary>The ten house colours. Plus <c>3A3A3A</c>, which plan H § 3.2 itself names ("unfilled
    /// #3A3A3A") for the rename card's unlit dots.</summary>
    private static readonly HashSet<string> Palette = new(StringComparer.OrdinalIgnoreCase)
    {
        "FCFCFA", // bg and on-ink
        "F1F2EF", // paper
        "141414", // ink
        "555555", // mid
        "737373", // dim
        "ABABA7", // faint
        "DEDEDE", // line
        "8C8C88", // rule
        "E31B23", // accent
        "3A3A3A", // plan H § 3.2: unfilled rename dots
    };

    /// <summary>Every file plan H adds or changes whose colours the palette governs. The compare screen's
    /// files and Theme.axaml (the old, untouched keys) are deliberately not here.</summary>
    private static readonly string[] Files =
    [
        "HouseResources.axaml", "HouseStyles.axaml", "StartView.axaml", "RenameView.axaml", "KeysPage.axaml", "UiRoot.axaml",
        "HouseControls.cs", "StartView.axaml.cs", "RenameView.axaml.cs", "KeysPage.axaml.cs", "UiRoot.axaml.cs",
        "DecodeLayer.cs", "DecodeTimeline.cs", "HouseMotion.cs", // plan H § 5 S3
        "BrowseView.axaml", "BrowseView.axaml.cs", "BrowseList.cs", // plan I: the folder browser
    ];

    private static string ViewsDirectory()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            var candidate = Path.Combine(dir.FullName, "src", "RankMaster2.Pc", "Ui", "Views");
            if (Directory.Exists(candidate)) return candidate;
        }
        throw new DirectoryNotFoundException("pc/src/RankMaster2.Pc/Ui/Views not found above " + AppContext.BaseDirectory);
    }

    /// <summary>Hex colours in a source text that are not a palette colour (or an alpha variant of one).
    /// <c>#RRGGBB</c> is the colour; <c>#AARRGGBB</c> is a palette colour at some alpha; anything else
    /// (3, 4, 5 or 7 digits) is not a colour this project writes, and is reported.</summary>
    public static IEnumerable<string> HexOutsidePalette(string text)
    {
        foreach (Match m in Regex.Matches(text, @"(?<![&\w])#([0-9A-Fa-f]{3,8})\b"))
        {
            var hex = m.Groups[1].Value;
            var rgb = hex.Length switch { 6 => hex, 8 => hex[2..], _ => null };
            if (rgb is null || !Palette.Contains(rgb)) yield return m.Value;
        }
    }

    [Fact]
    public void New_and_changed_view_files_use_only_palette_colours()
    {
        var dir = ViewsDirectory();
        var offenders = new List<string>();
        foreach (var name in Files)
        {
            var path = Path.Combine(dir, name);
            Assert.True(File.Exists(path), path + " is listed in the palette test but does not exist");
            var text = File.ReadAllText(path);

            offenders.AddRange(HexOutsidePalette(text).Select(h => $"{name}: {h}"));

            // Named colours and colour-building calls are as much a colour as a hex literal.
            foreach (Match m in Regex.Matches(text, @"\b(?:Foreground|Background|Fill|Stroke|BorderBrush|Color)=""(?!\{)([A-Za-z]+)"""))
                if (m.Groups[1].Value is not "Transparent")
                    offenders.Add($"{name}: named colour {m.Value}");
            // (Color.FromArgb is not flagged: DecodeLayer builds alpha and in-between shades of a palette colour with it,
            // which is arithmetic on the palette, not a new colour; a literal would still be caught by the hex scan.)
            foreach (Match m in Regex.Matches(text, @"\b(?:Colors|Brushes)\.(?!Transparent\b)\w+|Color\.(?:FromRgb|Parse)\b|new\s+SolidColorBrush\s*\("))
                offenders.Add($"{name}: {m.Value}");
        }

        Assert.True(offenders.Count == 0, "Colours outside the house palette (plan H § 1):\n" + string.Join("\n", offenders));
    }

    [Theory]
    [InlineData("#FCFCFA", true)]
    [InlineData("#fcfcfa", true)]
    [InlineData("#C7FCFCFA", true)]   // bg at 78 %
    [InlineData("#40141414", true)]   // ink at 25 %
    [InlineData("#3A3A3A", true)]     // plan H § 3.2
    [InlineData("#000000", false)]
    [InlineData("#18181B", false)]
    [InlineData("#FFFFFF", false)]
    [InlineData("#FFF", false)]
    [InlineData("#80EF4444", false)]
    public void The_palette_check_itself_accepts_palette_and_alpha_variants_only(string literal, bool allowed) =>
        Assert.Equal(allowed, !HexOutsidePalette("Fill=\"" + literal + "\"").Any());

    [Fact]
    public void XML_character_references_are_not_colours()
    {
        Assert.Empty(HexOutsidePalette("Text=\"&#x2022; &#183;\""));
    }

    // ---- DotRow ---------------------------------------------------------------------------------

    [Theory]
    [InlineData(0, 60, 0)]
    [InlineData(37, 60, 22)]   // the mockup's own example
    [InlineData(50, 60, 30)]
    [InlineData(100, 60, 60)]
    [InlineData(-5, 60, 0)]
    [InlineData(140, 60, 60)]
    public void Percent_becomes_lit_dots(int percent, int count, int expected) =>
        Assert.Equal(expected, DotRow.DotsFor(percent, count));

    [Fact]
    public void Dots_keep_six_pixels_when_they_fit_and_shrink_when_they_do_not()
    {
        Assert.Equal(6, DotRow.DiameterFor(596, 60), 6);
        Assert.Equal(6, DotRow.DiameterFor(900, 60), 6);
        var shrunk = DotRow.DiameterFor(500, 60);
        Assert.InRange(shrunk, 4.3, 4.5);
        // And the shrunken row really does fit: 60 dots and 59 gaps.
        Assert.True(60 * shrunk + 59 * DotRow.Gap <= 500 + 0.001);
    }

    // ---- BusyLine / DotGrid / DottedRule -----------------------------------------------------------

    [Fact]
    public void The_busy_dash_sweeps_from_before_the_track_to_past_its_end()
    {
        var (left0, width) = BusyLine.DashFor(0, 440);
        var (left1, _) = BusyLine.DashFor(1, 440);
        Assert.Equal(-132, left0, 3);   // -30 %
        Assert.Equal(440, left1, 3);    // 100 %
        Assert.Equal(132, width, 3);    // 30 % of the track
        Assert.Equal(18, BusyLine.DashFor(0.5, 30).Width, 3); // never narrower than 18 px
        Assert.Equal(BusyLine.DashFor(1, 100).Left, BusyLine.DashFor(7, 100).Left, 3); // phase is clamped
    }

    [Fact]
    public void The_dot_grid_has_one_dot_per_whole_seven_pixel_cell()
    {
        Assert.Equal((80, 42), DotGrid.CellsFor(new Size(560, 300)));
        Assert.Equal((0, 0), DotGrid.CellsFor(new Size(6, 6)));
    }
}
