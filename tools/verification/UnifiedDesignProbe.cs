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
        }
        var collapse = Walk(window).OfType<Button>().Single(button => Equals(button.Content, "折叠柜体设计"));
        collapse.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); Pump();
        if (host.Visibility != Visibility.Collapsed || !((string)collapse.Content).Contains("展开")) throw new Exception("Collapse failed");
        collapse.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); Pump();
        if (host.Visibility != Visibility.Visible) throw new Exception("Expand failed");
        tabs.SelectedIndex = 0; Pump();
        if (host.ActualWidth < 260 || host.ActualWidth / ((Grid)window.Content).ActualWidth < .27 ||
            host.ActualWidth / ((Grid)window.Content).ActualWidth > .38) throw new Exception("Cabinet panel is not approximately one third");
        var bitmap = new RenderTargetBitmap((int)window.ActualWidth, (int)window.ActualHeight, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(window);
        using (var stream = File.Create(args[1])) { var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap)); encoder.Save(stream); }
        frameType.GetMethod("CloseForAddinShutdown", Internal).Invoke(window, null); Pump();
        Console.WriteLine("三个参数页、独立默认值、安装孔输入移除、基座尺寸随动、三分之一布局与折叠展开均通过。");
    }
}
