using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Pulse.Core;
using Pulse.Core.Mouse;
using Pulse.Core.Reaction;
using Pulse.Ui;

namespace Pulse.Pages;

/// <summary>The Mouse page: a sensitivity converter that matches cm/360 exactly between games.</summary>
public partial class MousePage : UserControl, IPage
{
    static ReactionSettings S => TweakEngine.State.Reaction ??= new ReactionSettings();
    bool _loading = true;

    public MousePage()
    {
        InitializeComponent();
        foreach (var box in new[] { FromGame, ToGame })
            foreach (var g in SensConverter.Games) box.Items.Add(new ComboBoxItem { Content = g.Name, Tag = g });
        // Starts from your VALORANT setting (the one the reaction trainer uses), into CS2.
        FromGame.SelectedIndex = 0;
        ToGame.SelectedIndex = 1;
        FromSens.Text = S.GameSensitivity.ToString("0.###", CultureInfo.CurrentCulture);
        FromDpi.Text = ToDpi.Text = S.MouseDpi.ToString(CultureInfo.CurrentCulture);
        _loading = false;
        Update();
    }

    public void OnShown(object parameter) { }

    SensConverter.Game From => (FromGame.SelectedItem as ComboBoxItem)?.Tag as SensConverter.Game;
    SensConverter.Game To => (ToGame.SelectedItem as ComboBoxItem)?.Tag as SensConverter.Game;

    static double Number(string text) =>
        double.TryParse(text, NumberStyles.Float, CultureInfo.CurrentCulture, out var v) || double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out v) ? v : double.NaN;

    static string Show(double v, int decimals) => double.IsNaN(v) ? "–" : v.ToString("0." + new string('#', Math.Max(1, decimals + 1)), CultureInfo.CurrentCulture);

    void Input_Changed(object sender, RoutedEventArgs e) { if (!_loading) Update(); }

    void Update()
    {
        var from = From; var to = To;
        if (from == null || to == null) return;
        double sens = Number(FromSens.Text), dpi = Number(FromDpi.Text), toDpi = Number(ToDpi.Text);
        if (double.IsNaN(toDpi) || toDpi <= 0) toDpi = dpi;
        double result = SensConverter.Convert(from, sens, dpi, to, toDpi);
        double cm = SensConverter.Cm360(from, sens, dpi);
        // One extra decimal beyond what the game takes, so you can see how close the rounded value is.
        Result.Text = Show(result, to.Decimals);
        FromNote.Text = from.Note == null ? "" : Loc.T(from.Note);
        ToNote.Text = to.Note == null ? "" : Loc.T(to.Note);
        Summary.Text = double.IsNaN(cm) ? Loc.T("Enter your sensitivity and DPI.")
            : Loc.F("{0} cm per 360° ({1} in) · eDPI {2} → {3}", cm.ToString("0.##", CultureInfo.CurrentCulture), (cm / 2.54).ToString("0.##", CultureInfo.CurrentCulture),
                SensConverter.Edpi(sens, dpi).ToString("0.#", CultureInfo.CurrentCulture), SensConverter.Edpi(result, toDpi).ToString("0.#", CultureInfo.CurrentCulture));
        CopyButton.IsEnabled = !double.IsNaN(result);

        // Every game at once, at the "to" DPI.
        AllGames.Children.Clear();
        AllNote.Text = double.IsNaN(cm) ? "" : Loc.F("{0} cm per 360° at {1} DPI, in each game:", cm.ToString("0.##", CultureInfo.CurrentCulture), toDpi.ToString("0", CultureInfo.CurrentCulture));
        if (double.IsNaN(cm)) return;
        var grid = new System.Windows.Controls.Primitives.UniformGrid { Columns = 3 };
        foreach (var g in SensConverter.Games)
        {
            double v = SensConverter.Convert(from, sens, dpi, g, toDpi);
            var row = new Grid { Margin = new Thickness(0, 0, 18, 8) };
            row.ColumnDefinitions.Add(new ColumnDefinition());
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.Children.Add(new TextBlock { Text = g.Name, FontSize = 12.5, Foreground = (Brush)FindResource("TextDimBrush"), TextTrimming = TextTrimming.CharacterEllipsis });
            var val = new TextBlock { Text = SensConverter.Round(g, v).ToString(CultureInfo.CurrentCulture), FontSize = 13, FontWeight = FontWeights.Bold, Margin = new Thickness(8, 0, 0, 0) };
            Grid.SetColumn(val, 1);
            row.Children.Add(val);
            grid.Children.Add(row);
        }
        AllGames.Children.Add(grid);
    }

    void Swap_Click(object sender, RoutedEventArgs e)
    {
        var to = To; var result = Result.Text;
        _loading = true;
        ToGame.SelectedItem = FromGame.SelectedItem;
        foreach (ComboBoxItem i in FromGame.Items) if (i.Tag == to) FromGame.SelectedItem = i;
        if (result != "–") FromSens.Text = result;
        (FromDpi.Text, ToDpi.Text) = (ToDpi.Text, FromDpi.Text);
        _loading = false;
        Update();
    }

    void Copy_Click(object sender, RoutedEventArgs e)
    {
        try { Clipboard.SetText(Result.Text); CopyButton.Content = Loc.T("Copied"); } catch { }
    }
}
