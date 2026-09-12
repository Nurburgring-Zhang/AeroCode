// Copyright (c) AeroCode
// LocalModelsView — 本地模型管理面板（LOCAL_LLM_SPEC P3）。DataContext 由 SettingsView 绑定到
// SettingsViewModel.LocalModels（OllamaClient 驱动的运行时检测/模型管理）。
using Avalonia.Controls;

namespace AeroCode.App.Views;

public partial class LocalModelsView : UserControl
{
    public LocalModelsView()
    {
        InitializeComponent();
    }
}
