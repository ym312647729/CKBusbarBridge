# CKBusbarBridge 项目约束

- 这是母线桥项目试用版独立仓库。不要改动原项目。柜体及母线桥只做参数草稿和 SolidWorks 视口预览；只允许新建并保存空总装。生成按钮仅提示暂未开发。
- 保留设置、关于及针对既有实体的出图。不要加入柜体、基座、铜排、基架等原生装配或零件模型、实体生成入口、其他客户工程图模板或业务记录。
- 保持原 SolidWorks STA 和 OpenGL 会话边界。不要重建、自动注册、运行 SolidWorks 测试、制作安装包或推送，除非用户明确要求相应动作。改完可做 Debug 增量编译、静态核查和本地提交。
- 长度用 mm、双精度计算和保存。运行资源修改后刷新清单。文档入口见 docs/README.md。
- Git 双仓库推送约定（2026-11-18 用户确认）：用户要求推送时，原公司 Git 仓库 `http://git.frp.rongdisoft.com.cn:13000/wangyuming/CKBusbarBridge.git` 与公司 GitHub 仓库 `https://github.com/ym312647729/CKBusbarBridge.git` 必须推送同一提交、同一分支，并分别核对结果。本地 `origin` 保留原拉取地址，设置上述两个 `pushurl`；`github` 用于查看公司 GitHub 远程。沿用按需推送和不强制覆盖的规则。
