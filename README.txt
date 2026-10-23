NETSPEEDWIDGET
English / 简体中文

ENGLISH

Portable desktop download/upload monitor for Windows 10 or 11 with
.NET Framework 4.8. Extract the entire ZIP and open NetSpeedWidget.exe.
Keep NetSpeedWidget.exe.config beside it. No installation is needed.

Drag to move. Right-click or use the three-dot menu for Always on top,
Show in taskbar, units, and adapter selection. Rates update about once
per second; graphs show 60 samples. Units use multiples of 1,000:
1 KB/s = 1,000 bytes/s, and one byte = eight bits.

Use the arrow button to expand app details. Compact totals need no admin
permission. Enable app details explicitly requests Windows administrator
permission and restarts the widget if approved; cancelling leaves the
existing widget running. Apps groups executable basenames; Processes
shows individual PIDs. Click Download/Upload to sort, click again to
reverse, and scroll for more rows.

The app table measures TCP/UDP over IPv4/IPv6 on all adapters. Selecting
an adapter only changes compact totals. Proxy/VPN forwarding can credit
both the original app and a forwarding process, so adding rows can count
the same logical transfer twice. Headers, local traffic and sampling also
cause differences. These are current rates, not maximum connection speed.

Tracing stops when you collapse, hide, minimize, or exit. Restoring an
expanded widget starts a new measurement period. No packet payloads are
captured, no trace files are written, and no measurements are sent out.

Esc or Hide to tray hides the widget; double-click its notification-area
icon to restore it. Reopening the executable also restores the existing
instance. Shift+F10 opens the menu. Exit closes the app completely.

Preferences are in settings.xml beside the executable. Use a writable
folder. To reset, exit, remove settings.xml, then reopen. If rates remain
zero, start a transfer and check the adapter; app details show activity
only after monitoring starts. If tracing fails, compact totals still work.


简体中文

适用于 Windows 10 或 Windows 11 的便携式桌面下载/上传网速监测工具，
需要 .NET Framework 4.8。请完整解压 ZIP 后打开 NetSpeedWidget.exe，
并将 NetSpeedWidget.exe.config 保留在同一文件夹中，无需安装。

拖动即可移动。右键或点击三点菜单，可设置窗口置顶、任务栏显示、
速率单位和网卡。读数约每秒更新一次，趋势图显示最近 60 次采样。
单位采用十进制：1 KB/s = 每秒 1,000 字节，1 字节 = 8 比特。

点击箭头展开应用详情。紧凑视图的总速率无需管理员权限。
只有点击 Enable app details 才会请求 Windows 管理员权限，批准后
重启小组件；取消提示不会关闭原窗口。Apps 按可执行文件的基本名称
分组，Processes 分别显示各进程 PID。点击 Download/Upload 排序，
再次点击反转顺序，滚动查看更多条目。

应用表格统计所有网卡上的 IPv4/IPv6 TCP/UDP 流量，网卡选择仅影响
紧凑视图的总速率。代理/VPN 转发可能把同一次传输分别归属到原始应用
和转发进程，因此将各行相加可能重复计数。协议头、本机通信和采样
也会导致差异。读数表示当前速率，并非连接的最大带宽。

收起、隐藏、最小化或退出会停止进程跟踪。恢复展开的小组件会开始
新的测量周期。程序不捕获数据包载荷、不写入跟踪文件，也不发送测量结果。

Esc 或 Hide to tray 隐藏窗口；双击通知区域图标可恢复。
再次打开可执行文件也会恢复已有窗口。Shift+F10 打开菜单，
Exit 完全退出程序。

偏好设置保存在可执行文件旁的 settings.xml 中，请使用可写入的文件夹。
重置时先退出程序，再删除 settings.xml 后重新打开。如果速率一直为零，
请启动一次传输并检查网卡；应用详情仅显示监测开始后的活动。
即使跟踪无法启动，紧凑视图的总速率仍可使用。
