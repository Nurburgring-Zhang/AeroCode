// Copyright (c) AeroCode V3.0
// MainWindow — 桌面主窗口薄壳。内容全部在 MainView（与 Android single-view 共享）。
using Avalonia.Controls;

namespace AeroCode.App.Views;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
    }
}
