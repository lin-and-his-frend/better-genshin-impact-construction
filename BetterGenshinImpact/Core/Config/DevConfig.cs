using CommunityToolkit.Mvvm.ComponentModel;
using System;
using BetterGenshinImpact.GameTask.Common.Map.Maps.Base;

namespace BetterGenshinImpact.Core.Config;

/// <summary>
/// 脚本配置
/// </summary>
[Serializable]
public partial class DevConfig : ObservableObject
{
    // 录制地图名称
    [ObservableProperty]
    private string _recordMapName = MapTypes.Teyvat.ToString();

    // 是否显示控制台窗口（开发者）
    [ObservableProperty]
    private bool _consoleWindowEnabled;
}
