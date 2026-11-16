using GeometRi;
using SolidWorks.Interop.sldworks;
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace CKBusbarBridgePreview
{
    /// <summary>
    /// 母线自由绘制临时预览体类（可视化核心载体）
    /// 【功能】封装SolidWorks临时预览体的所有关联信息（几何实体、所属组件、显示样式、控制逻辑），
    /// 实现预览体的隐藏/显示、透明度调整、选择权限控制，是折弯预览可视化的核心对象
    /// 【核心特性】适配SolidWorks COM对象管理，手动触发GC回收避免内存泄漏
    /// </summary>
    public class FreeDrawBody
    {
        /// <summary>
        /// 无参构造函数：初始化默认值（透明度0）
        /// </summary>
        public FreeDrawBody()
        {
        }

        /// <summary>
        /// 全参构造函数：初始化预览体的所有核心属性
        /// </summary>
        /// <param name="color">预览体显示颜色</param>
        /// <param name="body">SolidWorks几何实体（Body2），预览的核心几何对象</param>
        /// <param name="component2">预览体所属组件（装配体环境下）</param>
        /// <param name="lastVector3d">最后折弯段的方向向量（记录路径方向）</param>
        /// <param name="lastPointCount">最后路径点数量（关联折弯路径）</param>
        /// <param name="name">预览体名称（标识用，默认空）</param>
        /// <param name="canSelect">选择权限（0=不可选，1=可选，默认1）</param>
        /// <param name="tempMater">材质透明度（0-1，默认0）</param>
        public FreeDrawBody(Color color, Body2 body, Component2 component2, Vector3d lastVector3d, int lastPointCount,
            string name = "", int canSelect = 1,
            double tempMater = 0)
        {
            Color = color;
            CanSelect = canSelect;
            Body = body;
            Component2 = component2;
            Name = name;
            TempMater = tempMater;
            LastVector3d = lastVector3d;
            LastPointCount = lastPointCount;
        }

        #region 显示样式与权限属性

        /// <summary>
        /// 材质透明度（默认0，范围0-1）
        /// 0=不透明，1=完全透明（SolidWorks材质透明度约定）
        /// </summary>
        public double TempMater { get; set; } = 0;

        /// <summary>
        /// 预览体显示颜色
        /// 用于区分不同相位/类型的母线预览体
        /// </summary>
        public Color Color { get; set; }

        /// <summary>
        /// 选择权限标记（默认1）
        /// 0=不可选中（预览态），1=可选中（编辑态），控制SolidWorks中预览体的交互权限
        /// </summary>
        public int CanSelect { get; set; } = 1;

        #endregion

        #region 核心几何与组件属性

        /// <summary>
        /// SolidWorks几何实体对象（Body2）
        /// 预览体的核心几何载体，存储母线的三维实体数据
        /// </summary>
        public Body2 Body { get; set; }

        /// <summary>
        /// 预览体所属组件（装配体环境下）
        /// 关联预览体到具体的装配体组件，保证显示/隐藏的上下文正确
        /// </summary>
        public Component2 Component2 { get; set; }

        #endregion

        #region 标识与路径关联属性

        /// <summary>
        /// 预览体名称（默认空）
        /// 用于标识不同的预览体（如"A相母线预览体"），便于调试和管理
        /// </summary>
        public string Name { get; set; } = "";

        /// <summary>
        /// 当前预览体由铣排的三段普通直排 Body 相加得到。
        /// </summary>
        public bool IsMilledBar { get; set; }

        /// <summary>
        /// 当前预览体使用铜排大师的立弯分段与实体生成方案。
        /// </summary>
        public bool IsVerticalBend { get; set; }

        /// <summary>
        /// 最后路径点数量
        /// 关联预览体到折弯路径的点位数量，用于路径变更时更新预览体
        /// </summary>
        public int LastPointCount { get; set; }

        /// <summary>
        /// 最后折弯段的方向向量
        /// 记录预览体最后一段的折弯方向，用于后续路径调整的基准
        /// </summary>
        public Vector3d LastVector3d { get; set; }

        #endregion

        #region 核心控制方法：隐藏/显示预览体

        /// <summary>
        /// 隐藏预览体（核心控制方法）
        /// 【逻辑】校验组件/实体有效性 → 调用SolidWorks API隐藏；隐藏异常在预览边界内容错
        /// </summary>
        public void Hide()
        {
            if (Component2 == null || Body == null)
            {
                return;
            }

            try
            {
                // 关闭窗口时组件可能已被 SolidWorks 释放，隐藏失败不能越过预览边界。
                if (string.IsNullOrWhiteSpace(Component2.GetPathName()))
                {
                    return;
                }

                Body.Hide(Component2);
            }
            catch (Exception previewBodyHideException)
            {
                FaceToWallPreviewTrace.Error(
                    "预览体隐藏-已容错",
                    previewBodyHideException,
                    "名称=" + (Name ?? string.Empty));
            }
        }

        /// <summary>
        /// 显示预览体（核心控制方法）
        /// 【逻辑】校验组件/实体有效性 → 设置显示样式（颜色+选择权限）→ 调整透明度
        /// </summary>
        public void Show()
        {
            // 有效性校验：组件非空+组件路径有效+实体非空
            if (Component2 != null && !string.IsNullOrWhiteSpace(Component2.GetPathName()) && Body != null)
            {
                // 第一步：显示实体并设置颜色、选择权限
                // Display3参数：所属组件、颜色Ref值、选择权限
                Body.Display3(Component2, ToColorRef(Color), CanSelect);

                // 第二步：调整材质透明度（修改MaterialPropertyValues2的第8个值，索引7）
                // MaterialPropertyValues2数组结构：[红,绿,蓝,反光,环境,漫反射,高光,透明度,发射率,...]
                var tempMater = (double[])Body.MaterialPropertyValues2;
                tempMater[7] = TempMater; // 索引7对应透明度
                Body.MaterialPropertyValues2 = tempMater;

            }
        }

        public static int ToColorRef(Color color)
        {
            return (color.R << 0) | (color.G << 8) | (color.B << 16);
        }

        #endregion
    }
}
