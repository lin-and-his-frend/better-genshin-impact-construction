# 启动参数说明

启动参数本质是 Unity 独立平台播放器命令行参数。  
为了安全，BetterGI 仅允许以下参数组合：

- `-window-mode exclusive`
  - 独占全屏，即选择游戏进程以独占全屏模式运行。
  - 该模式与游戏内浏览器功能不兼容。
- `-screen-fullscreen` 或 `-screen-fullscreen 0|1`
  - 设置是否以全屏模式显示。
- `-popupwindow`
  - 无边框窗口模式。
- `-platform_type CLOUD_THIRD_PARTY_MOBILE`
  - 触摸屏模式（键鼠输入将被忽略）。
- `-screen-width <数值>`
  - 设置宽度，例如 `1920`。
- `-screen-height <数值>`
  - 设置高度，例如 `1080`。
- `-monitor <序号>`
  - 多显示器时指定显示器序号。

## 常见组合

```text
-popupwindow -screen-width 1920 -screen-height 1080
```

```text
-window-mode exclusive -monitor 1
```
