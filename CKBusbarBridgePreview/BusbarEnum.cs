using System;
using System.ComponentModel;
using System.Reflection;

namespace CKBusbarBridgePreview
{
    /// <summary>
    /// 相序
    /// </summary>
    public enum BusbarPhase_e
    {
        [Description("空相序")]
        Unknow = 0,
        [Description("A相")]
        A = 1,
        [Description("B相")]
        B = 2,
        [Description("C相")]
        C = 3,
        [Description("N相")]
        N = 4,
        [Description("PE相")]
        PE = 5,
        [Description("ABC相")]
        ABC = 6
    }

    /// <summary>
    /// 形状
    /// </summary>
    public enum BusbarShape_e
    {

        unknow = 0,
        /// <summary>
        /// I型排
        /// </summary>
        [Description("I型")]
        I = 1,
        /// <summary>
        /// 闪电
        /// </summary>
        [Description("N型")]
        N = 2,
        /// <summary>
        /// 直角闪电 
        /// </summary>
        [Description("N_V型")]
        N_V = 3,
        /// <summary>
        /// L型
        /// </summary>
        [Description("L型")]
        L = 4,
        /// <summary>
        /// U型
        /// </summary>
        [Description("U型")]
        U =5

    }

    /// <summary>
    /// 配合面开孔类型
    /// </summary>
    public enum MatingHoleShape_e
    {
        unknow = 0,
        [Description("圆孔")]
        Circle = 1,
        [Description("腰孔")]
        Slot = 2,
        [Description("长方孔")]
        Rectangular =3

    }

    /// <summary>
    /// 孔类型
    /// </summary>
    [Serializable]
    public enum HoleType_e
    {
        /// <summary>
        /// 圆
        /// </summary>
        [Description("圆")]
        Circle,

        /// <summary>
        /// 腰孔
        /// </summary>
        [Description("腰孔")]
        Slot,

        /// <summary>
        /// 长方形
        /// </summary>
        [Description("长方形")]
        Rectangle,

        /// <summary>
        /// 六边
        /// </summary>
        [Description("六边")]
        Hexagon,

        UnKnow
    }

    /// <summary>
    /// 母线桥系统零件类型。
    /// 当前只保留业务流程已经使用的类型，后续按需扩展。
    /// </summary>
    public enum SystemPartType_e
    {
        [Description("未知")]
        Unknow = 0,

        [Description("基座")]
        Base = 1,

        [Description("基架")]
        Frame = 2,

        [Description("柜顶伸出排")]
        CabinetTopExtensionBusbar = 3,

        [Description("绝缘子")]
        Insulator = 4,

        [Description("穿墙套管")]
        WallBushing = 5,

        [Description("垫块")]
        SupportBlock = 6
    }

    /// <summary>
    /// 零件类型
    /// 转换成值 20及以上是铜排
    /// </summary>
    public enum PartType_e 
    {     
     
        [Description("未知")]
        Unknow = 0,
    
        /// <summary>
        /// 标准件
        /// </summary>
       [Description("元器件")]
        Standard = 1,
        /// <summary>
        /// 母线框
        /// </summary>
        [Description("母线框")]
        BusbarFrame = 2,
        /// <summary>
        /// 绝缘子
        /// </summary>
        [Description("绝缘子")]
        Insulator = 3,
        /// <summary>
        /// 接地器件
        /// </summary>
        [Description("接地器件")]
        GroundingPart = 4,
        /// <summary>
        /// 套管
        /// </summary>
        [Description("套管")]
         Bushing = 5,
        /// <summary>
        /// 穿心式
        /// </summary>
        [Description("穿心式")]
        ThroughCenter = 6,

        /// <summary>
        /// 绝缘木
        /// </summary>
        [Description("绝缘木")]
        InsulatorWood = 7,

        [Description("主母排垫块")]
        MainSupportBlock = 12,

        [Description("软连接")]
        Soft = 13,

        [Description("垫块")]
        SupportBlock = 14,

        [Description("始端")]
        Start = 15,

        [Description("铜排拖")]
        BusbarTow = 16,

        /// <summary>
        /// 铜排
        /// </summary>
        [Description("铜排")]
        Busbar = 20,

        /// <summary>
        /// 分支排
        /// </summary>
        [Description("分支排")]
        Branch = 21,

        /// <summary>
        /// 主母排
        /// </summary>
        [Description("主母排")]
        Main = 23,

        /// <summary>
        /// 柜顶伸出排
        /// </summary>
        [Description("柜顶伸出排")]
        BoxEnd = 24,

        [Description("柜内主母排")]
        DummyMain = 25,


        /// <summary>
        /// 柜顶伸出排
        /// </summary>
        [Description("联络排")]
        Contact = 30,

        [Description("转接块")]
        SpecialBlock = 31,

        [Description("C型排")]
        CBusbar = 32,

        /// <summary>
        /// 钣金件
        /// </summary>
        [Description("钣金件")]
        SheetMetal = 33


    }

    public enum DefineDirection_e
    {

        [Description("未知")]
        Unknow = 0,

        /// <summary>
        /// 向前
        /// </summary>
        [Description("向前")]
        Front = 1,
        /// <summary>
        /// 向后
        /// </summary>
        [Description("向后")]
        Behind = 2,
        /// <summary>
        /// 向上
        /// </summary>
        [Description("向上")]
        Top = 3,
        /// <summary>
        /// 向下
        /// </summary>
        [Description("向下")]
        Bottom = 4,
        /// <summary>
        /// 向左
        /// </summary>
        [Description("向左")]
        Left = 5,
        /// <summary>
        /// 向右
        /// </summary>
        [Description("向右")]
        Right = 6
    }

    public static class EnumHelper
    {
        /// <summary>
        /// 获取属性
        /// </summary>
        /// <typeparam name="T"></typeparam>
        /// <param name="description"></param>
        /// <returns></returns>
        public static T GetEnumByDescription<T>(string description) where T : Enum
        {
            System.Reflection.FieldInfo[] fields = typeof(T).GetFields();
            foreach (System.Reflection.FieldInfo field in fields)
            {
                object[] objs = field.GetCustomAttributes(typeof(DescriptionAttribute), false); //获取描述属性
                if (objs.Length > 0 && (objs[0] as DescriptionAttribute).Description == description)
                {
                    return (T)field.GetValue(null);
                }
            }
            return default(T);
        }

        /// <summary>
        /// 根据属性描述获取枚举值
        /// </summary>
        /// <typeparam name="T">类型</typeparam>
        /// <param name="des">属性说明</param>
        /// <returns>枚举值</returns>
        public static T GetEnum<T>(string des) where T : struct, IConvertible
        {

            Type type = typeof(T);
            if (!type.IsEnum)
            {
                return default(T);
            }
            T[] enums = (T[])Enum.GetValues(type);
            T temp;
            if (!Enum.TryParse(des, out temp))
            {
                temp = default(T);
            }
            for (int i = 0; i < enums.Length; i++)
            {
                string name = enums[i].ToString();
                FieldInfo field = type.GetField(name);
                object[] objs = field.GetCustomAttributes(typeof(DescriptionAttribute), false);
                if (objs == null || objs.Length == 0)
                {
                    continue;
                }
                DescriptionAttribute descriptionAttribute = (DescriptionAttribute)objs[0];
                string edes = descriptionAttribute.Description;
                if (des == edes)
                {
                    temp = enums[i];
                    break;
                }
            }

            return temp;
        }

        /// <summary>
        /// 获取属性描述
        /// </summary>
        /// <param name="obj"></param>
        /// <returns></returns>
        public static string GetDescription(this Enum obj)
        {
            try
            {
                string objName = obj.ToString();
                Type t = obj.GetType();
                System.Reflection.FieldInfo fi = t.GetField(objName);
                System.ComponentModel.DescriptionAttribute[] arrDesc =
                    (System.ComponentModel.DescriptionAttribute[])fi.GetCustomAttributes(
                        typeof(System.ComponentModel.DescriptionAttribute), false);
                return arrDesc[0].Description;
            }
            catch (Exception ex)
            {
                //MessageBox.Show("出错了->GetDescription");
                return "";
            }
            
        }

        public static void GetEnum<T>(string a, ref T t)
        {
            foreach (T b in Enum.GetValues(typeof(T)))
            {
                if (GetDescription(b as Enum) == a)
                    t = b;
            }
        }
       
    }

}
