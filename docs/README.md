# 中压母线桥项目试用版

项目名：`CKBusbarBridge`。柜体与母线桥参数窗口使用 RD.UI.Wpf，默认贴靠整个屏幕工作区右侧四分之一宽度，并在底部给任务进度留出空间。参数窗口不显示图片和二维预览；SolidWorks 视口负责模型预览。生成按钮仅提示“实体生成暂未开发”。

- 可新建并保存空总装；柜体、基座、铜排、基架、绝缘子及吊架只在视口显示。不开启正式桥体生成、实体开孔、已生成模型的二次驱动或 BOM 命令。
- 保留开孔库设置、关于、出工程图和铜排出图。出图仅针对用户已打开的实物装配体；柜体工程图要求项目已有相应工程图文件。随项目仅保留本项目图纸模板。
- 保留驱动视口显示所需的离线几何定义和算法 Worker。离线定义不是可在 SolidWorks 中打开的模型。
- 源码及运行目录与原插件独立，试用版只恢复自己的草稿。

开孔库、铜排出图、图号及关于窗口同样使用 RD.UI.Wpf。普通提示和进度显示在右下角；需要用户选择的确认框使用 RD.UI.Wpf 对话框。铜排出图窗口调用隔离运行域内原 DLL 的无界面出图接口。

## 增量编译

需要 .NET Framework 4.8 开发包、支持 WPF 的 VS MSBuild 及 WebView2 Runtime（运行时）。首次拉取用 VS MSBuild 执行以下命令，编译不会注册 COM：

```powershell
msbuild CKBusbarBridge.sln /restore /t:Build /p:RestorePackagesConfig=true /p:Configuration=Debug /p:Platform="Any CPU" /p:RegisterForComInterop=false /p:SkipSolidWorksRegistration=true
```

本地包源在 `.nuget/local-feed`。面对墙等调用算法 Worker 的方案需单独配置本项目获准使用的授权（进程环境变量 `CK_BUSBAR_WORKER_AUTHORIZATION`）；仓库和默认产物不包含旧项目授权。预览源码或运行资源修改后执行 `pwsh -File tools/Update-ParameterPreviewManifest.ps1`。不把原项目客户模型、模板压缩包、试验记录或用户设置复制到该仓库。
