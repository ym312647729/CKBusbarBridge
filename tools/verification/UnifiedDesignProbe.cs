using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Newtonsoft.Json.Linq;

internal static class UnifiedDesignProbe
{
    private const BindingFlags Internal = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
    [STAThread] private static int Main(string[] args)
    {
        try { Check(args); return 0; }
        catch (Exception error) { Console.Error.WriteLine(error); return 1; }
    }
    private static IEnumerable<DependencyObject> Walk(DependencyObject root)
    {
        yield return root;
        for (int index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++)
            foreach (var child in Walk(VisualTreeHelper.GetChild(root, index))) yield return child;
    }
    private static void Pump()
    {
        var frame = new DispatcherFrame();
        Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.ContextIdle, new Action(() => frame.Continue = false));
        Dispatcher.PushFrame(frame);
    }
    private static void SaveWindow(Window window, string imagePath)
    {
        var bitmap = new RenderTargetBitmap((int)window.ActualWidth, (int)window.ActualHeight, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(window);
        using (var stream = File.Create(imagePath)) { var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap)); encoder.Save(stream); }
    }
    private static string RowName(object row) => (string)row.GetType().GetProperty("Name").GetValue(row);
    private static void CheckFrameMarker(Assembly design)
    {
        var viewportType = design.GetType("CKBusbarBridge.DesignPreview.BridgeOpenGlViewport", true);
        var runtime = (IDisposable)Activator.CreateInstance(design.GetType("CKBusbarBridge.Prototype.BridgeGeometryRuntime", true), true);
        try
        {
            foreach (var rotated in new[] { false, true })
            {
                var packet = JObject.FromObject(new {
                    id = "盖板一", designObjectId = "基架一", groupId = "基架一", memberIds = new[] { "基架一" },
                    category = "frame", appearanceRole = "cover", geometryKey = "盖板", geometrySignature = "盖板测试",
                    positions = new float[] { 0,0,0, 1000,0,0, 1000,0,500, 0,0,500, 0,2,0, 1000,2,0, 1000,2,500, 0,2,500 },
                    quads = new[] { 0,1,2,3, 4,7,6,5, 0,4,5,1, 1,5,6,2, 2,6,7,3, 3,7,4,0 },
                    triangles = new int[0], edges = new int[0],
                    transform = rotated ? new float[] { 0,0,-1,0, 0,1,0,0, 1,0,0,0, 100,200,300,1 }
                        : new float[] { 1,0,0,0, 0,1,0,0, 0,0,1,0, 0,0,0,1 }
                });
                var unrelated = (JObject)packet.DeepClone();
                unrelated["id"] = "其他盖板"; unrelated["designObjectId"] = "其他基架";
                unrelated["groupId"] = "其他基架"; unrelated["memberIds"] = new JArray("其他基架");
                unrelated["transform"] = new JArray(1,0,0,0, 0,1,0,0, 0,0,1,0, 10000,10000,10000,1);
                var response = new JObject { ["instances"] = new JArray(packet, unrelated) };
                var frame = runtime.GetType().GetMethod("CreateFrame", Internal).Invoke(runtime, new object[] { Guid.NewGuid(), 1L, response });
                // 仅调用纯网格锚点方法，不构造原生视口、不绑定 HWND 或 SolidWorks。
                var viewport = System.Runtime.Serialization.FormatterServices.GetUninitializedObject(viewportType);
                viewportType.GetField("_frame", Internal).SetValue(viewport, frame);
                var anchorMethod = viewportType.GetMethod("TryGetFrameCoverCenter", Internal);
                var anchorArgs = new object[] { "基架一", null };
                if (!(bool)anchorMethod.Invoke(viewport, anchorArgs)) throw new Exception("缺少基架盖板标签锚点。");
                var expected = rotated ? new[] { 350d, 202d, -200d } : new[] { 500d, 2d, 250d };
                var actual = new[] { "X", "Y", "Z" }.Select(axis => Convert.ToDouble(anchorArgs[1].GetType().GetField(axis, Internal).GetValue(anchorArgs[1]))).ToArray();
                if (actual.Where((value, axis) => Math.Abs(value - expected[axis]) > .001).Any())
                    throw new Exception("位置标签未跟随盖板上表面中心及实例变换。");
                if ((bool)anchorMethod.Invoke(viewport, new object[] { "不存在的基架", null }))
                    throw new Exception("位置标签错误关联了其他基架的盖板。");
            }
        }
        finally { runtime.Dispose(); }
        Console.WriteLine("盖板上表面中心、旋转平移与基架身份隔离核查通过；未调用原生视口。");
    }
    private static void Check(string[] args)
    {
        var directory = Path.GetFullPath(args[0]);
        AppDomain.CurrentDomain.AssemblyResolve += (sender, request) =>
        {
            var name = new AssemblyName(request.Name).Name + ".dll";
            var file = Path.Combine(directory, name);
            if (!File.Exists(file)) file = Path.Combine(directory, "Worker", name);
            return File.Exists(file) ? Assembly.LoadFrom(file) : null;
        };
        var design = Assembly.LoadFrom(Path.Combine(directory, "CKBusbarBridge.DesignUI.dll"));
        CheckFrameMarker(design);
        var plugin = Assembly.LoadFrom(Path.Combine(directory, "CKBusbarBridge.dll"));
        var frameType = design.GetType("CKBusbarBridge.views.FrameDriverWindow", true);
        var window = (Window)Activator.CreateInstance(frameType);
        var host = (ContentControl)frameType.GetMethod("AddCabinetDesignPanel", Internal).Invoke(window, null);
        var rd = Assembly.LoadFrom(Path.Combine(directory, "RD.UI.Wpf.dll"));
        var tabs = (TabControl)Activator.CreateInstance(rd.GetType("RD.UI.Wpf.Controls.RDTabControl", true));
        host.Content = tabs;
        var scenarios = design.GetType("CKBusbarBridge.Models.DesignScenarioId") ?? plugin.GetType("CKBusbarBridge.Models.DesignScenarioId", true);
        var orientation = design.GetType("CKBusbarBridge.Models.FaceToWallCabinetOrientation") ?? plugin.GetType("CKBusbarBridge.Models.FaceToWallCabinetOrientation", true);
        var panel = plugin.GetType("CKBusbarBridge.views.UnifiedCabinetParameters", true);
        var pages = new List<UserControl>();
        foreach (var name in new[] { "FaceToFace", "FaceToWall", "SideToSide" })
        {
            var pageType = plugin.GetType("CKBusbarBridge.views." +
                (name == "FaceToFace" ? "CabinetDriverWindow" : name + "CabinetDriverWindow"), true);
            var page = (UserControl)Activator.CreateInstance(pageType, name == "FaceToWall"
                ? new object[] { null, Enum.Parse(orientation, "CabinetFrontToWall") } : new object[] { null });
            panel.GetMethod("Apply", BindingFlags.Static | BindingFlags.NonPublic).Invoke(null, new[] { page, Enum.Parse(scenarios, name) });
            var title = name == "FaceToFace" ? "面对面" : name == "FaceToWall" ? "面对墙" : "侧对侧";
            pages.Add(page); tabs.Items.Add(new TabItem { Header = title, Content = page });
        }
        window.Show(); Pump();
        for (int index = 0; index < 3; index++)
        {
            tabs.SelectedIndex = index; Pump();
            var actualParameters = pages[index].DataContext.GetType().GetProperty("Parameters").GetValue(pages[index].DataContext);
            File.WriteAllText(Path.Combine(Path.GetTempPath(), "ckbridge-parameters-" + index + ".json"),
                Newtonsoft.Json.JsonConvert.SerializeObject(actualParameters, new Newtonsoft.Json.Converters.StringEnumConverter()));
            var grid = Walk(pages[index]).OfType<DataGrid>().Single();
            var groups = Walk(pages[index]).OfType<ComboBox>().First(combo => combo.Items.Count > 0 && combo.Items[0] is string);
            bool spacing = false, automaticSize = false;
            foreach (var group in groups.Items.Cast<object>().ToArray())
            {
                groups.SelectedItem = group; Pump();
                foreach (var row in ((IEnumerable)grid.ItemsSource).Cast<object>())
                {
                    if (string.IsNullOrWhiteSpace(RowName(row)) || RowName(row).Contains("{Binding")) throw new Exception("参数名为空或显示了绑定表达式。");
                    var editor = (FrameworkElement)row.GetType().GetProperty("Editor").GetValue(row);
                    var path = editor.Tag as string;
                    if (path != null && (path.EndsWith(".L1") || path.EndsWith(".L2")))
                        throw new Exception("Installation-hole input remains visible");
                    if (editor is ComboBox combo && combo.Items.Count != 0 &&
                        (!(combo.SelectedItem is ComboBoxItem selected) || selected.Content == null ||
                         selected.Content.ToString().Contains("UnifiedCabinetParameters")))
                        throw new Exception("A parameter choice is not displaying its business value: " + path);
                    if (path == (index == 1 ? "General.CabinetToWallDistance" : "General.CabinetSpacing"))
                    {
                        var input = (TextBox)editor;
                        input.GetBindingExpression(TextBox.TextProperty).UpdateTarget();
                        if (double.Parse(input.Text) != new[] { 3500d, 1340d, 1000d }[index]) throw new Exception("Wrong scene default");
                        spacing = true;
                    }
                    if (((string)row.GetType().GetProperty("Name").GetValue(row)).Contains("基座深度（自动）"))
                        automaticSize = true;
                }
            }
            if (!spacing || index != 2 && !automaticSize) throw new Exception("Missing spacing or derived base dimensions");
            groups.SelectedIndex = 0;
            if (index == 1)
            {
                var wallRows = ((IEnumerable)grid.ItemsSource).Cast<object>().ToArray();
                var wallRow = wallRows.Single(row => (string)row.GetType().GetProperty("Path").GetValue(row) == "General.CabinetToWallDistance");
                var wallInput = (TextBox)wallRow.GetType().GetProperty("Editor").GetValue(wallRow);
                if (RowName(wallRow) != "柜前距墙(A)") throw new Exception("面对墙参数名未绑定当前朝向。");
                wallInput.Text = "2000";
                wallInput.GetBindingExpression(TextBox.TextProperty).UpdateSource(); Pump();
                if (RowName(wallRow) != "柜前距墙(A)" || !Equals(wallInput.ToolTip, RowName(wallRow)))
                    throw new Exception("修改尺寸破坏了参数名或提示。");
                var orientationSetter = pages[index].DataContext.GetType().GetMethod("SetOrientation", Internal);
                orientationSetter.Invoke(pages[index].DataContext, new[] { Enum.Parse(orientation, "CabinetRearToWall") }); Pump();
                if (RowName(wallRow) != "柜后距墙(A)") throw new Exception("柜前柜后切换未更新参数名。");
                orientationSetter.Invoke(pages[index].DataContext, new[] { Enum.Parse(orientation, "CabinetFrontToWall") }); Pump();
                if (RowName(wallRow) != "柜前距墙(A)") throw new Exception("切回朝向后参数名不正确。");
            }
        }
        var collapse = Walk(window).OfType<Button>().Single(button => Equals(button.Content, "折叠柜体设计"));
        var rootLayout = (Grid)window.Content;
        var expandedToggle = collapse.TransformToAncestor(rootLayout).Transform(new Point());
        if (expandedToggle.X < host.ActualWidth || expandedToggle.Y > 12)
            throw new Exception("折叠按钮未位于两栏交界处的提示栏左端。");
        collapse.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); Pump();
        if (host.Visibility != Visibility.Collapsed || !((string)collapse.Content).Contains("展开")) throw new Exception("Collapse failed");
        var collapsedToggle = collapse.TransformToAncestor(rootLayout).Transform(new Point());
        if (Math.Abs(collapsedToggle.X) > 1 || collapsedToggle.Y > 12)
            throw new Exception("折叠后展开按钮未紧贴提示栏左侧。");
        SaveWindow(window, Path.ChangeExtension(args[1], ".collapsed.png"));
        collapse.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); Pump();
        if (host.Visibility != Visibility.Visible) throw new Exception("Expand failed");
        tabs.SelectedIndex = 0; Pump();
        if (host.ActualWidth < 260 || host.ActualWidth / ((Grid)window.Content).ActualWidth < .27 ||
            host.ActualWidth / ((Grid)window.Content).ActualWidth > .38) throw new Exception("Cabinet panel is not approximately one third");
        if (window.Icon == null) throw new Exception("未设置 Windows 系统图标。");
        var titleBar = window.Template.FindName("PART_TitleBar", window) as DependencyObject;
        if (titleBar == null || !Walk(titleBar).OfType<Image>().Any(image => image.Source != null) ||
            Walk(titleBar).OfType<TextBlock>().Any(text => text.Text == "铜排大师" || text.Text == "榕迪"))
            throw new Exception("标题栏仍有品牌标记或缺少系统图标。");
        SaveWindow(window, args[1]);
        frameType.GetMethod("CloseForAddinShutdown", Internal).Invoke(window, null); Pump();
        Console.WriteLine("三个参数页、修改尺寸及切换朝向后的参数名、基座尺寸随动、折叠展开位置与系统图标均通过。");
    }
}
