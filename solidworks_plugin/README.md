# SolidWorks 2024 减速器设计与参数化建模插件

本插件为一级、二级圆柱齿轮减速器提供中文设计流程，并在 SolidWorks 2024 中生成可编辑的原生零件和总装。两种流程均支持直齿轮、斜齿轮；齿轮、轴、箱体、端盖、轴承和标准件按参数建模，可生成带配合的总装，也可按计算位置固定零件。

> **第一次使用**：运行 `更新插件.cmd` 安装当前构建，再启动 SolidWorks 并运行 `加载插件菜单.cmd`。若 SolidWorks 已打开，请先保存工作中的文档，安装后完全重启。更新系统注册需要 Windows 管理员权限。

## 环境与安装

- Windows、64 位 SOLIDWORKS、.NET Framework 4.x。`build.ps1` 默认从 `C:\Program Files\SOLIDWORKS Corp\SOLIDWORKS` 读取 SolidWorks API；安装位置不同时传入 `-SolidWorksDir`。
- 插件基于 **SOLIDWORKS 2024 SP5.0** 开发和验证。建议使用 **SOLIDWORKS 2024 或更新版本**，优先选用已验证的 2024 SP5.0。使用更新版本时，请先编译插件并用样例确认能正常加载和建模。
- 设计计算使用本项目根目录的 Python 代码及依赖。建议 Python 3.12；可将 `REDUCER_SW_PYTHON` 设为解释器完整路径。只做设计、保存项目或生成说明书时，可以不启动 SolidWorks；生成原生模型时必须启动。
- 当前可用构建路径记录在 `latest-build.txt`。修改 C# 代码后运行 `build.ps1` 编译，再运行 `更新插件.cmd` 更新系统注册。正在运行的 SolidWorks 不会自动替换已经加载的 DLL，需保存文档并重启。

安装步骤：

1. 双击 `更新插件.cmd`；若弹出 Windows 管理员授权，请同意。也可右键选择“以管理员身份运行”。
2. 确认窗口显示 `System plugin registration updated`。保存 SolidWorks 中的文档，完全退出并重新启动 SolidWorks。
3. 运行 `加载插件菜单.cmd`，在“减速器生成器”选项卡查看按钮。若选项卡未出现，在 SolidWorks 的“工具 → 插件”中启用 **Reducer Generator**。

插件按钮从左到右为：**一级减速器设计 → 一级减速器建模 → 二级减速器设计 → 二级减速器建模 → 导入参数生成**。

## 从设计到模型

| 任务 | SolidWorks 按钮 | 独立入口 |
| --- | --- | --- |
| 一级减速器完整设计、校核、说明书及建模 | 一级减速器设计 | `完整设计与生成.cmd` |
| 一级参数编辑、校验及建模 | 一级减速器建模 | `参数编辑与生成.cmd` |
| 二级减速器完整设计、校核、说明书及建模 | 二级减速器设计 | `二级完整设计与生成.cmd` |
| 二级参数编辑、校验及建模 | 二级减速器建模 | `二级减速器参数化建模.cmd` |
| 打开建模参数或设计项目 | 导入参数生成 | `run.ps1 -Editor -ParameterFile <文件>` |

导入 `model_parameters.json` 可直接打开建模编辑器。导入已关联建模参数的 `project.json` 也会打开建模编辑器，移动项目时可将两个文件放在同一目录。只有设计数据的项目会打开对应的设计窗口，重新校核后可保存建模方案或生成模型。

两种建模编辑器和二级设计的「模型与说明书」页都可取消勾选「添加装配配合」。一级设计可先开启生成前的详细尺寸编辑器。生成的总装会将零件固定在计算位置，适合查看布置和检查干涉。需要齿轮联动时，勾选该选项再生成。

**推荐流程**：进入相应的“设计”窗口，输入功率、转速、目标输出转速和使用条件；选择直齿或斜齿，设置齿数、法面模数、齿宽及校核参数；点击自动设计或重新计算；确认校核结果后保存设计项目、生成说明书，再生成 SolidWorks 模型。修改设计输入后需要重新计算。二级设计可分别设置高速级、低速级的齿轮类型及带正负号的螺旋角；配对齿轮的旋向由程序确定。

二级设计直接填写减速器输入轴功率和转速。“自动分配齿数”根据目标输出转速调整两级大齿轮齿数。打开旧项目时，插件会把原有工况换算为输入轴功率和转速，载入后请重新计算。

已经有参数时，可在一级、二级“建模”窗口按零件类别分页编辑并校验。一级完整样例使用 `parameters_v4.json`（schema v4），二级样例使用 `parameters_two_stage.json`（schema v5）；尺寸单位均为 **mm**。二级窗口包含三轴位置、各轴段、键槽、两级齿轮副、轴承、箱体与端盖，并提供只读参数预览。表格第一列中连续相同的零件或位置名称会合并显示并垂直居中，尺寸项仍逐行编辑。二级参数调整模数、齿数或螺旋角后，可点击“同步中心距”，再检查轴承座、齿轮孔与箱体净空。旧版 v1–v3 文件保留用于兼容测试，不包含当前全部零件。

也可以直接运行：

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\run.ps1 -Design
powershell -NoProfile -ExecutionPolicy Bypass -File .\run.ps1 -TwoDesign
powershell -NoProfile -ExecutionPolicy Bypass -File .\run.ps1 -ParameterFile .\parameters_v4.json
powershell -NoProfile -ExecutionPolicy Bypass -File .\run.ps1 -ParameterFile .\parameters_two_stage.json
```

`生成完整模型.cmd` 使用一级 v4 样例。`生成原生模型.cmd` 使用旧版 v3 参数，仅用于兼容性验证。

## 建模内容与操作

- **齿轮**：用渐开线草图生成齿形；直齿使用原生切除与圆周阵列，斜齿使用原生扭转扫描与圆周阵列。二级的两级齿轮可分别选择直齿或斜齿。
- **零件**：阶梯轴、轴承、平键、端盖、箱体、箱盖及附件独立生成。一级默认样例为 **34 个零件、55 个总装组件**；含标准件的二级默认样例为 **33 个零件、103 个总装组件**。
- **标准件与螺纹**：图纸要求的螺栓、螺钉、螺母、垫圈、定位销等使用独立可编辑零件；当前未直接引用共享 Toolbox 默认配置。一级锥销采用 4×20 mm，顶端略高于箱盖法兰。相关零件和孔带 SolidWorks 装饰螺纹线，螺纹实体采用简化圆柱外形。
- **总装配合**：只固定下箱体，其余组件按同心、重合、距离或角度等原生配合定位；齿轮副增加齿轮运动配合。标准件也按孔轴线和安装端面配合。
- **中文名称与颜色**：新生成零件的草图和主要建模特征在创建后立即使用中文名称。零件按类别着色，总装树中的组件使用中文显示名。磁盘上的 `.SLDPRT` 文件名保留参数契约所需的英文标识；零件另有“中文名称”自定义属性。为使中文组件名在重开后继续显示，生成成功后插件会关闭 SolidWorks“工具 → 选项 → 外部参考 → 替换文档时更新组件名称”。
- **暂停、停止与内存**：建模进度窗口提供“暂停/继续”“停止建模”和最小化。当前 SolidWorks 操作结束后，暂停或停止会在下一个检查点生效。建模中关闭设计窗口会请求停止，并在清理后关闭窗口。停止后已保存的文件保留在本次输出目录。零件完成后逐个保存和关闭。
- **箱体优化**：一级箱体和箱盖的成对同高肋板采用多轮廓一次拉伸；下箱体同一深度的回油槽按互不相交的轮廓合并切除。横向油槽与相邻 φ9 法兰孔保留至少 0.5 mm 净距。默认配方步骤分别由 40、22 减至 30、20。轴、键槽、齿轮、轴承及附件的特征和草图在创建后立即命名为中文，生成过程中即可查看中文特征树。

每次生成会创建新的 `output/<时间戳>_<编号>/`，不覆盖先前结果。主要文件如下：

| 文件 | 用途 |
| --- | --- |
| `*.SLDPRT`、`reducer_assembly.SLDASM` | 原生零件与总装 |
| `parameters.json` | 本次建模参数快照 |
| `parts_manifest.json` | 零件实体、体积、组件及配合清单 |
| `generation.log` | 生成过程与错误信息 |
| `native_audit.json` | 执行原生审计后生成的零件及总装检查结果 |

设计项目、模型参数和说明书位于 `design_projects/`；计算桥接日志位于 `.bridge/`。

## 检查与重新装配

在插件目录的 PowerShell 中执行：

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\build.ps1   # 编译并更新 latest-build.txt
powershell -NoProfile -ExecutionPolicy Bypass -File .\verify.ps1  # 离线检查
$build = (Get-Content .\latest-build.txt -Raw).Trim()
& (Join-Path $build 'ReducerLauncher.exe') --audit-output '.\output\某次输出目录'
& (Join-Path $build 'ReducerLauncher.exe') --audit-interference '.\output\某次输出目录'
```

原生审计需要已启动的 SolidWorks；它会只读检查保存后的零件、体积、特征错误、组件位置、原生配合，以及一级总装的实体干涉。油位盖与 O 形圈的密封预压单独记录，其余超过 0.1 mm³ 的交叠会判为错误。已有输出只需重建总装时，使用 `--reassemble <一级输出目录>` 或 `--remate-two-stage <二级输出目录>`，随后再运行 `--audit-output`。`build.ps1 -NoPromote` 可编译测试版本而不改变当前默认构建。
若只需快速查干涉，可运行 `--audit-interference`，结果写入 `interference_report.json`，不逐件重算体积。



## 常见问题

| 现象 | 处理 |
| --- | --- |
| 报 `Invalid parameter schema; expected version 1, mm`，或只看到旧按钮 | 系统仍加载旧插件。运行 `更新插件.cmd`，确认注册成功，保存文档并完全重启 SolidWorks。 |
| 双击 `.ps1` 被执行策略拦截 | 使用同名 `.cmd`，或在命令行加 `-ExecutionPolicy Bypass`。 |
| 设计窗口能打开，但不能生成模型 | 先启动 SOLIDWORKS，并检查参数校验及窗口中的错误提示。 |
| 总装重开后中文组件名变为英文 | 在 SolidWorks“工具 → 选项 → 外部参考”中关闭“替换文档时更新组件名称”，再重开总装。 |
| 生成中断或运行缓慢 | 查看本次 `output` 子目录的 `generation.log`；暂停会等当前 SolidWorks 操作结束后生效。 |

## 适用范围

模型用于设计展示、装配关系和参数化修改。斜齿齿根过渡、轴承内部、箱体局部结构以及实体螺纹均有简化；当前没有完成整机干涉、材料疲劳、制造公差和接触分析。工程计算依赖输入的许用应力与载荷系数，制造前应独立复核。

参考尺寸来自项目的设计参数及减速器图纸。二级设计流程参考了 Reducer Designer，其 MIT 许可见 `third_party/Reducer-Designer-LICENSE.txt`。

## 参考文献

参考文献：《机械设计》（第十版），高等教育出版社

## 问题反馈

欢迎通过 GitHub Issues 提交使用问题和改进建议。
