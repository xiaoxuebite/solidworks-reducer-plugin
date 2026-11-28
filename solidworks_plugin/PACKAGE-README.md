# SOLIDWORKS 减速器设计与建模插件（使用版）

用于机械课程设计。插件支持一级、二级圆柱齿轮减速器的设计、校核和说明书导出，可在 SOLIDWORKS 中生成可编辑的原生零件与总装。

## 安装

1. 准备 64 位 Windows、.NET Framework 4.8 或更新的 4.x 版本、64 位 SOLIDWORKS 和 64 位 Python 3.12。安装 Python 时保留 Python Launcher（`py.exe`）。Pillow 随包提供。
2. 将 ZIP **完整解压**到当前账号可写、打算长期保留的目录，例如「文档」里的子目录。保留包内的目录结构。
3. 保存工作并关闭 SOLIDWORKS 和减速器设计窗口。双击 `setup.cmd`，同意 Windows 管理员权限提示，等待出现 `Installed`。
4. 启动 SOLIDWORKS，在「工具 → 插件」勾选 **Reducer Generator**。如需每次启动都加载，可同时勾选启动项。在「减速器生成器」选项卡找到设计和建模入口。

插件基于 **SOLIDWORKS 2024 SP5.0** 开发，建议使用 SOLIDWORKS 2024 或更新版本。使用更新版本时，先用 `solidworks_plugin/parameters_v4.json` 或 `solidworks_plugin/parameters_two_stage.json` 样例验证加载和建模。

## 移动、更新与卸载

- 更换解压目录或更新版本时，先关闭 SOLIDWORKS 和设计窗口，再从新目录运行 `setup.cmd`，以更新注册的 DLL 路径。
- 卸载时关闭上述窗口，双击 `uninstall.cmd` 并同意管理员权限提示。脚本移除此插件的注册项，保留设计项目、说明书、模型和解压目录。清理旧目录前请先转移其中需要保留的文件。
- 源代码、构建说明和问题反馈见 [项目仓库](https://github.com/xiaoxuebite/solidworks-reducer-plugin)。

参考文献：《机械设计》（第十版），高等教育出版社。
