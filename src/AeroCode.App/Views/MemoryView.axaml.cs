// Copyright (c) AeroCode V3.0
// MemoryView — 平台无关记忆视图。应用无隐式 VM→View 装配，显式从 DI 解析（与 ChatView 同模式）：
// 单例 MemoryViewModel（构造即加载 + G5 召回/人工沉淀能力）。
using Avalonia.Controls;
using AeroCode.App.ViewModels;

namespace AeroCode.App.Views;

public partial class MemoryView : UserControl
{
    public MemoryView()
    {
        InitializeComponent();
        DataContext ??= App.Services.GetService(typeof(MemoryViewModel));
    }
}
