# SOLIDWORKS 圆柱齿轮减速器设计与建模插件

用于课程设计。插件支持一级、二级直齿或斜齿圆柱齿轮减速器的设计、校核和说明书导出，并通过 SOLIDWORKS 生成可编辑特征的 SLDPRT 零件和 SLDASM 总装。可以在 SOLIDWORKS 中继续修改零件和装配关系。

`solidworks_plugin/` 包含插件源码、构建脚本和样例。根目录的 Python 文件及 `model_generator/` 中的计算模块为插件提供参数计算与说明书功能，原生模型由 SOLIDWORKS API 生成。


## 下载使用版

从 [GitHub Releases](https://github.com/xiaoxuebite/solidworks-reducer-plugin/releases) 下载 Windows x64 预编译 ZIP。完整解压到当前账号可写的目录，再按包内 README 安装。需要 Python 3.12，Pillow 已随包提供。安装和卸载入口分别为 `setup.cmd` 和 `uninstall.cmd`。

## 从源码安装

安装环境为 Windows、64 位 SOLIDWORKS、.NET Framework 4.x 和 Python 3.12。插件基于 **SOLIDWORKS 2024 SP5.0** 开发和验证。建议使用 **SOLIDWORKS 2024 或更新版本**，优先选用已验证的 2024 SP5.0。使用更新版本时，请先编译插件并用样例确认能正常加载和建模。请保留完整项目目录，插件需要读取其中的 Python 计算模块、样例参数和参考图表。

1. 在项目根目录打开 PowerShell。将下列示例路径换成本机 SOLIDWORKS 的实际安装目录，该目录应包含 `api\redist`。

   ```powershell
   powershell -NoProfile -ExecutionPolicy Bypass -File .\solidworks_plugin\build.ps1 -SolidWorksDir 'C:\Program Files\SOLIDWORKS Corp\SOLIDWORKS'
   py -3.12 -m pip install -r requirements.txt
   ```

   Pillow 用于生成一级设计说明书中的插图。插件计算在本机 Python 进程中执行，无需启动网页后端。使用虚拟环境时，可以将 `REDUCER_SW_PYTHON` 设为该环境的 `python.exe` 完整路径，再从同一环境启动设计窗口。
2. 如已打开 SOLIDWORKS，先保存文档并完全退出。运行 [更新插件.cmd](solidworks_plugin/更新插件.cmd)，按提示完成管理员注册。
3. 启动 SOLIDWORKS，在「工具 → 插件」中启用 **Reducer Generator**。也可在 SOLIDWORKS 启动后运行 [加载插件菜单.cmd](solidworks_plugin/加载插件菜单.cmd)。随后在「减速器生成器」选项卡使用插件。

每次编译都会在 `solidworks_plugin/bin/` 生成新构建。`latest-build.txt` 记录当前电脑的构建路径。更新源码后，重新编译、注册并重启 SOLIDWORKS。

### 使用入口

| 工具栏入口 | 用途 | 独立入口或样例 |
| --- | --- | --- |
| 一级减速器设计 | 从工况、传动、轴系与箱体设计到强度校核、说明书和建模 | [完整设计与生成.cmd](solidworks_plugin/完整设计与生成.cmd) |
| 一级减速器建模 | 编辑尺寸、校验参数并生成原生零件和总装 | [一级样例 v4](solidworks_plugin/parameters_v4.json) |
| 二级减速器设计 | 分配两级传动比，分别设计两级齿轮并校核三根轴、轴承和平键 | [二级完整设计与生成.cmd](solidworks_plugin/二级完整设计与生成.cmd) |
| 二级减速器建模 | 编辑三轴位置、轴段、两级齿轮、箱体及端盖并生成原生模型 | [二级样例 v5](solidworks_plugin/parameters_two_stage.json) |
| 导入参数生成 | 载入已有设计项目或建模参数 JSON | 可导入 `project.json` 或 `model_parameters.json` |

独立设计窗口支持在 SOLIDWORKS 启动前计算和编辑项目。生成原生模型时需要运行 SOLIDWORKS。两个设计窗口均可保存项目并导出 Markdown 说明书。修改参数后，点击 **「重新计算与校核」** 刷新结果，窗口会停留在当前步骤。二级建模页调整齿数、法面模数或螺旋角后，可用 **「同步中心距」** 更新三轴布置，再执行参数校验。

材料下拉框支持预设和自定义材料。齿轮材料类别用于选取弹性系数。切换材料会保留原有数值，硬度、许用应力等可在对应设计页手动填写。**「设计参考图表」** 提供只读资料入口。教材图表可按下文的参考文献查阅。

### 装配配合与低内存环境

一级、二级建模窗口均提供 **「添加装配配合」**。一级从设计窗口生成时，先勾选 **「生成前打开详细尺寸编辑器」**，再到编辑器取消配合。二级设计窗口的「模型与说明书」页也直接提供这项开关。

- **取消配合**时，零件固定在计算位置，适合课堂展示、查看布置或检查静态干涉。低内存电脑可以优先用这种方式验证生成流程。
- **添加配合**时，插件尝试生成定位和齿轮运动配合。需要运动演示时，在 SOLIDWORKS 中检查轴能否转动、齿轮是否联动，以及当前参数下的干涉。

二级装配曾在低内存环境中触发 SOLIDWORKS Resource Monitor 警告。遇到生成缓慢或中断，可关闭其他大型文档和程序，先试固定位置总装，再查看本次输出目录的 `generation.log`。暂停或停止会在当前 SOLIDWORKS 操作结束后的检查点生效。

### 输出与验证

| 目录 | 内容 |
| --- | --- |
| `solidworks_plugin/output/<本次生成目录>/` | SLDPRT、`reducer_assembly.SLDASM`、建模参数快照、`parts_manifest.json` 和 `generation.log` |
| `solidworks_plugin/design_projects/` | 设计项目、建模方案和说明书 |
| `solidworks_plugin/.bridge/` | 本地设计计算的请求、响应与日志 |

构建后可运行下列命令，检查样例参数、一级与二级设计计算、建模参数导出、说明书和窗口流程。

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\solidworks_plugin\verify.ps1
```

原生模型的重建、配合和干涉需要在 SOLIDWORKS 中结合具体输出验证。可以在装配体中执行「评估 → 干涉检查」，并在功能树中查看重建警告和配合状态。原生审计命令见 [插件检查与重新装配说明](solidworks_plugin/README.md#检查与重新装配)。

本项目是用于教学与设计展示的参数化原型。轴承、螺纹和部分结构采用简化模型。全轴疲劳、公差、全周运动干涉及制造条件需按最终方案独立复核。
## 参考文献

参考文献：《机械设计》（第十版），高等教育出版社

## 问题反馈

欢迎通过 GitHub Issues 提交使用问题和改进建议。
