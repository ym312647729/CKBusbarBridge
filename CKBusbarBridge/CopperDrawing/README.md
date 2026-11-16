# 铜排图纸运行包

本目录保存 `PartDrawingDoc` 铜排图纸功能的私有运行包，供当前插件独立部署“铜排图纸”窗口、扫描、设置、出图及 PDF 处理链路。复制和输出规则以[宿主工程](../CKBusbarBridge.csproj)为准；项目统一约束见[现行文档](../../docs/README.md)。

- `lib` 中的 `*.dll.bin` 会在构建时以 `CopperDrawing\*.dll` 输出。保留 `.bin` 后缀可防止旧版 NPOI 等私有依赖参与当前插件的编译引用解析。
- `Runtime\Settings` 会输出到插件根目录的 `Settings`。
- `Runtime\templates\Drawings` 会输出到插件根目录的 `templates\Drawings`。

更新该运行包时，应同步更新 `PartDrawingDoc` 的完整 DLL 依赖、基础设置文件和工程图模板，并继续排除开发机上的 `Settings\Custom` 用户配置。
