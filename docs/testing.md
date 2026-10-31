# 可复现的本机验证

测试使用临时生成的文件、合成账号和仅监听 `127.0.0.1` 的 FTP 服务，不读取根目录的个人 INI，不连接真实手机。默认回归不挂载盘符。测试自己的配置、日志和预览保存在 `_tooltest/repro-时间-随机值/`，不进入发布包。

## 环境

- Windows 10/11、Windows PowerShell 5.1、Windows 自带的 .NET Framework C# 编译器。
- 可执行的 rclone（历史基线为 1.75.2）。
- Python 3 与测试依赖 pyftpdlib。依赖仅用于模拟 FTP，程序本身不依赖 Python。
- 使用普通桌面 PowerShell；生成测试 exe 会设置其 Medium 完整性标签。

从仓库根目录执行（rclone 路径换成实际位置）：

```powershell
python -m pip install --target .\_tooltest\deps pyftpdlib==2.1.0
powershell -NoProfile -ExecutionPolicy Bypass -File .\tests\run-local.ps1 -RclonePath C:\Tools\rclone\rclone.exe -Python python
```

需要指定 Python 时，`-Python` 可填写 `python.exe` 的完整路径。入口脚本通过自己的进程环境变量 `MAGICTRAY_TEST_RCLONE` 把 rclone 路径传给夹具，不改写仓库源码或本机 INI。

快速验证入口可加 `-Cases configuration`；只重跑本轮修复可加 `-Cases audit-2.2.1`。PowerShell 中可用数组指定多组，例如 `-Cases configuration,remote-name,ui`。

## 覆盖范围与结果

脚本先将当前源码与夹具共同编译，再依次运行：

| 组 | 检查内容 |
|---|---|
| configuration | 默认并发、范围校验、配置保存与恢复 |
| remote-name | rclone remote 名称、子进程环境隔离与 DPAPI |
| ui | 合成数据下三页界面、快捷键、小窗口和缩放布局 |
| backup-integration | 内容变化、同大小同时间变化、旧版本保留、缺失补传、取消、错误密码 |
| rename-compatibility / remote-denied | 临时改名拒绝兼容、普通写入拒绝与旧版本保护 |
| local / remote / mixed permissions | 本地权限恢复、上传时拒绝及 FTP 权限错误分类 |
| audit-2.2.1 | 精确改名拒绝分类、临时文件清理范围、状态写入失败保留旧记录、部分成功及下一轮补传 |

每组打印退出码与最终断言摘要，汇总写入测试目录的 `summary.json`。任何组失败时脚本返回 1；编译失败、服务无法启动或超时会直接报错。默认单组超时为 240 秒，可通过 `-TimeoutSeconds` 调整。

`local-permissions` 仅对测试生成的文件暂时设置 ACL 拒绝，夹具在 `finally` 中恢复。不要强制结束测试，等待它完成或抛出结果。

## 构建与手工验收

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\src\MagicTray\build.ps1
```

关闭已运行的 MagicTray 后构建。构建不会改写现有 INI。

在真实手机上单独验收：先测试 FTP 连接，再选用专门的小型测试目录备份；修改一个文件后确认旧版本保留；中途停止后再次运行，确认只处理尚未完成的文件。此步骤需用户在自己的设备上执行，不能由本机模拟结果推断已经通过。

当前入口跳过 `Tests.cs.fixture` 的盘符挂载段。需要 WinFsp 挂载验收时，可单独编译该夹具，传入生成 run 目录、localhost 端口与 FTP 根目录，并省略第四个 `backup-only` 参数；夹具会选择空闲盘符并卸载自己创建的挂载。不要对用户已有盘符手工卸载。

[小文件性能方法和历史数据](performance.md)为独立本机合成对照；默认回归不会重跑性能基准，也不承诺真实手机提速比例。
