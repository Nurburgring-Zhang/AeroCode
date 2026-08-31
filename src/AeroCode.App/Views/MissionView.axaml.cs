// Copyright (c) AeroCode
// MissionView — Autonomy Mission 面板（批次 B G5）。桌面由 MainView 页签承载，
// Android single-view 同构。应用无隐式 VM→View 装配，显式从 DI 解析（与 ChatView 同模式）。
using Avalonia.Controls;
using AeroCode.App.ViewModels;

namespace AeroCode.App.Views;

public partial class MissionView : UserControl
{
    public MissionView()
    {
        InitializeComponent();
        DataContext ??= App.Services.GetService(typeof(MissionViewModel));
    }
}
