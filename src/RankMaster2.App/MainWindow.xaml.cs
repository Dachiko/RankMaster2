using System.Windows;
using RankMaster2.Catalog;
using RankMaster2.Ranking;

namespace RankMaster2;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        StatusText.Text =
            $"TrueSkill ready. PrefetchPairs will default to {MediaPipeline.DefaultPrefetchPairs}. " +
            $"JSON file: {JsonCatalog.FileName}.";
        _ = new TrueSkill();
        _ = new PairSelector();
        _ = new JsonCatalog();
    }
}
