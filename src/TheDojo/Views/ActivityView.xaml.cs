using System.Windows;
using System.Windows.Controls;
using TheDojo.ViewModels;

namespace TheDojo.Views;

public partial class ActivityView : UserControl
{
    public ActivityView()
    {
        InitializeComponent();
    }

    private void OnSpend(object sender, RoutedEventArgs e)
    {
        if (DataContext is ActivityViewModel vm)
        {
            vm.MetricIndex = 0;
        }
    }

    private void OnPrompts(object sender, RoutedEventArgs e)
    {
        if (DataContext is ActivityViewModel vm)
        {
            vm.MetricIndex = 1;
        }
    }
}
