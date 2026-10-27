# PILL 0.2.0 界面更新与验证

参考本机 PCM-A10 工具的紧凑 Fluent 界面。默认窗口由 1080×840 改为 620×660，面积减少约 55%；最小 540×540。标题为 PILL，删除重复大标题和宣传副标题，保留原生标题栏。

## 用户界面

- 耳机、均衡器、触控、设置四页；常用音量控制和左右电量在首页。
- 电量采用小型电池图标加精确百分比：50% 及以上绿色，25～49% 黄色，更低为红色；未上报显示未知。
- 连接和断开共用一个按钮。扫描按钮变为带工具提示和辅助功能名称的刷新图标。
- 右上角菜单提供系统 / 浅色 / 深色主题、Windows 设置和备份入口。切换应用主题不会修改系统设置。
- 设备增益与诊断详情可折叠。小窗口保持纵向滚动，保留全部设置入口。
- 中文控件使用 Segoe UI Variable / Microsoft YaHei UI，输入框和页签使用 WPF 原生 Fluent 模板。

## 本轮证据

1. Release 编译通过，0 警告、0 错误；20 项离线协议检查通过，见 `protocol-checks-0.2.0.json`。
2. 发布 EXE 作为独立进程运行 `--preview-ui`，退出码 0；明确不访问 BLE 或 Windows 音频。生成八张深浅主题页面预览和一张最小窗口预览，见 `previews/` 与 `ui-preview-receipt-0.2.0.json`。这些是本地模拟数据，不是本轮设备读数。
3. 对照 0.1.0 的源码包：BleTransport、GaiaProtocol、PillController、PillState、WindowsAudio 五个核心文件逐字节相同，见 `core-comparison-0.2.0.json`。0.1.0 的 41 项实机与协议检查仍作为历史证据，未把它们算作本轮重测。
4. 正常发布 EXE 已启动，桌面辅助功能树显示完整控件和初始状态。桌面捕获返回黑画面且激活窗口失败，因此本轮没有完成真实鼠标点击连接 / 断开的桌面回归，也没有新增耳机写入测试。本轮没有修改耳机配置。

## 开发预览

```powershell
$env:PILL_PREVIEW_DIR = Join-Path $PWD 'artifacts/ui-0.2.0'
dotnet src/PillManager/bin/Release/net10.0-windows10.0.19041.0/PillManager.dll --preview-ui
```

此模式只用于生成本应用的 WPF 视觉预览，带“界面预览”标记，并在完成后退出。普通启动不注入模拟数据。

Fluent 主题使用 [Microsoft 官方 WPF 主题机制](https://learn.microsoft.com/en-us/dotnet/desktop/wpf/whats-new/net90)。.NET 10 中的运行时 ThemeMode 切换仍带实验性标记，代码仅在该调用处抑制 WPF0001；目标运行时维持 .NET 10。
