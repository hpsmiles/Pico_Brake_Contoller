using System;
using System.Windows;
using BrakeCalibrator.ViewModels;

namespace BrakeCalibrator;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
    }

    private MainViewModel ViewModel => (MainViewModel)DataContext;

    private void OnExitClick(object sender, RoutedEventArgs e)
    {
        Close();
    }

    private void OnAboutClick(object sender, RoutedEventArgs e)
    {
        MessageBox.Show(
            "Pico Brake Controller v0.2\n" +
            "Sim Racing Pneumatic Brake + Throttle Calibration Tool\n\n" +
            "C# WPF + LiveCharts2 Edition",
            "About", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    protected override void OnClosed(EventArgs e)
    {
        base.OnClosed(e);
        ViewModel.Dispose();
    }
}
