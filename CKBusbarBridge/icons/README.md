# 中压母线桥项目工具栏图标

![功能图标预览](preview.png)

SVG 源图位于 `src/`。普通功能统一用蓝色线条；铜排用与 SolidWorks 预览一致的 A 黄、B 绿、C 红三相色。透明背景，无渐变。

项目统一约束及验证边界见[现行文档](../../docs/README.md)。

`toolbar-icons.json` 定义从左到右的图标顺序，对应 `SwAddin.RegisterCommands` 中的槽位。
图标索引与 SolidWorks 命令 ID 是不同概念，不应混用。

| 索引 | 功能 | 图形 |
| --- | --- | --- |
| 0 | 柜体设计 | 柜体、双向驱动箭头 |
| 1 | 母线桥设计 | 双柜、桥体、中间支撑 |
| 2 | 母排设置 | 铜排、调节滑杆 |
| 3 | 铜排开孔 | 铜排孔位、钻头 |
| 4 | 出工程图 | 工程图纸、尺寸线、标题栏 |
| 5 | 铜排出图 | 叠放图纸、三相折弯铜排 |
| 6 | 一键导出BOM | 表格、导出箭头 |
| 7 | 铜排清单 | 清单板、铜排条目、勾选标记 |
| 8 | 项目设置 | 孔型库、设置齿轮 |
| 9 | 关于 | 信息圆环 |

索引 2、3、6、7 保留原图标槽位，试用版不注册对应独立按钮。

## 资源生成与检查

使用 Node.js 和 `sharp`（已用 0.35.4 验证）。也可以通过 `--sharp-module` 指向已有 sharp 模块目录，无需修改项目依赖。

```powershell
node tools/generate-toolbar-icons.cjs
node tools/generate-toolbar-icons.cjs --check
```

生成器输出六档 `toolbar{size}x.png`，每条宽度为 `10 × size`、高度为 `size`，
以及对应 `mainicon_{size}.png`（桥体图标）、旧版 `AddinIcon.bmp` 和 `preview.png`。
所有运行时 PNG 沿用原来的嵌入资源名称，不增加插件运行时依赖。
修改 SVG 后运行生成器；`--check` 比较源图渲染结果与现有 PNG，并检查按钮索引及资源嵌入。
这两个命令均不会构建项目、注册 COM 或启动 SolidWorks。

资源当前随 DLL 嵌入；源代码与资源更新需要在后续正常编译、部署并重新加载插件后才会显示。
`preview.png` 是设计预览，不是 SolidWorks 实机截图。
