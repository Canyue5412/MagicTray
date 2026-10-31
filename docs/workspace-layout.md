# 目录与发布范围

公开仓库与 Windows 发布包包含：

```text
MagicTray.exe               当前 Windows 成品
启动MagicTray.cmd            切换到程序目录后启动
README.md                   使用、依赖与制作说明
.gitignore                  排除本机配置与运行数据
docs/                       合成界面截图、性能方法、验证与版本记录
src/MagicTray/              当前源码、构建脚本与合成测试夹具
tests/run-local.ps1          本机可复现回归入口
```

开发工作区另外保留以下本机数据，均不公开：

| 路径 | 用途 |
|---|---|
| `MagicTray.ini`、`Magic5Tray.ini` | 个人连接配置和 DPAPI 加密凭据；留在原处 |
| `logs/` | 运行日志、备份清单与最近结果；留在原处 |
| `packages/` | 现有离线依赖；未移动或重新分发 |
| `_tooltest/` | 历史测试输出、合成数据及测试 Python 依赖；保留原路径 |
| `archive/private-deployment/` | 旧手机部署文档与原始 README |
| `archive/legacy-workspace/` | 旧 Magic5Tray 成品、旧启动器、旧 PowerShell 脚本 |
| `archive/previous-builds/` | 原 `.previous.exe` 成品 |
| `archive/release-history/` | 旧发布包与当前发布副本更新前的快照 |
| `_publish/Magic5Tray/` | 旧发布 Git 工作副本，保留元数据和历史 |
| `_publish/MagicTray/` | 按公开白名单准备的当前发布副本 |

历史脚本备份方向与当前工具可能不同，使用前应读归档说明。发布时按以上公开范围选取文件，不能仅凭 `.gitignore` 判断已经跟踪的文件是否安全。
