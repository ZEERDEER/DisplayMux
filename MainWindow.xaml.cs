using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using System;
using System.Collections.ObjectModel;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using WinRT.Interop;

namespace displaymux
{
    // 升级后的数据模型类
    public class DisplayMonitor
    {
        public string Name { get; set; }
        public IntPtr Handle { get; set; }
        // 新增：存储当前输入的数字代码 (例如 15)
        public uint CurrentInputCode { get; set; }

        // 新增：一个辅助属性，把数字代码自动翻译成给人看的文字
        // 界面会直接绑定这个属性
        public string CurrentInputName
        {
            get
            {
                switch (CurrentInputCode)
                {
                    case 15: return "DisplayPort";
                    case 17: return "HDMI 1";
                    case 18: return "HDMI 2";
                    case 27: return "Type-C";
                    case 0: return "未知/正在获取..."; // 默认值
                    default: return $"未知接口 ({CurrentInputCode})";
                }
            }
        }
    }

    public sealed partial class MainWindow : Window
    {
        public ObservableCollection<DisplayMonitor> Monitors { get; set; } = new();

        public MainWindow()
        {
            this.InitializeComponent();
            this.SystemBackdrop = new MicaBackdrop();
            this.ExtendsContentIntoTitleBar = true;
            // 稍微调高一点窗口，因为加了 Padding
            SetWindowSize(680, 500);
            LoadMonitors();
        }

        private void SetWindowSize(int width, int height)
        {
            IntPtr hWnd = WindowNative.GetWindowHandle(this);
            WindowId wndId = Win32Interop.GetWindowIdFromWindow(hWnd);
            AppWindow appWindow = AppWindow.GetFromWindowId(wndId);
            appWindow.Resize(new Windows.Graphics.SizeInt32(width, height));
        }

        private void OnRefreshClick(object sender, RoutedEventArgs e)
        {
            LoadMonitors();
        }

        // 升级后的扫描逻辑：扫描的同时读取当前状态
        // 注意：读取操作可能会让扫描过程稍微变慢一点点，这是正常的
        private void LoadMonitors()
        {
            StatusText.Text = "正在扫描显示器并读取当前状态...";
            Monitors.Clear();

            MonitorController.EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero,
                delegate (IntPtr hMonitor, IntPtr hdcMonitor, ref MonitorController.Rect lprcMonitor, IntPtr dwData)
                {
                    uint count;
                    if (MonitorController.GetNumberOfPhysicalMonitorsFromHMONITOR(hMonitor, out count) && count > 0)
                    {
                        var physicalMonitors = new MonitorController.PHYSICAL_MONITOR[count];
                        if (MonitorController.GetPhysicalMonitorsFromHMONITOR(hMonitor, count, physicalMonitors))
                        {
                            foreach (var pm in physicalMonitors)
                            {
                                // 1. 创建基本信息
                                var newMonitor = new DisplayMonitor
                                {
                                    Name = pm.szPhysicalMonitorDescription.Replace("\0", ""),
                                    Handle = pm.hPhysicalMonitor,
                                    CurrentInputCode = 0 // 先设为0
                                };

                                // 2. 【关键新增】尝试读取当前的输入源 (VCP Code 0x60)
                                uint currentValue = 0;
                                uint maxValue = 0;
                                // 调用底层 API 读取
                                bool readSuccess = MonitorController.GetVCPFeatureAndVCPFeatureReply(
                                    pm.hPhysicalMonitor,
                                    0x60, // 0x60 是输入源寄存器地址
                                    out _, // 我们不需要类型信息
                                    out currentValue, // 读取到的当前值
                                    out maxValue // 读取到的最大值（也不需要）
                                );

                                if (readSuccess)
                                {
                                    // 如果读取成功，更新模型，界面会自动变化
                                    newMonitor.CurrentInputCode = currentValue;
                                }

                                Monitors.Add(newMonitor);
                            }
                        }
                    }
                    return true;
                }, 0);

            MonitorListView.ItemsSource = Monitors;
            StatusText.Text = $"扫描完成，共找到 {Monitors.Count} 台显示器。";
        }

        private async void OnApplyClick(object sender, RoutedEventArgs e)
        {
            var button = sender as Button;
            if (button == null) return;
            var monitor = button.Tag as DisplayMonitor;
            if (monitor == null) return;

            var parent = VisualTreeHelper.GetParent(button) as Grid;
            // 因为前面加了一列图标，所以下拉框现在是第3个元素 (index 2)
            var comboBox = parent.Children[2] as ComboBox;

            if (comboBox != null && comboBox.SelectedItem is ComboBoxItem selectedItem)
            {
                uint targetCode = uint.Parse(selectedItem.Tag.ToString());
                string inputName = selectedItem.Content.ToString();

                StatusText.Text = $"正在将 [{monitor.Name}] 切换至 {inputName}...";
                button.IsEnabled = false;

                bool success = await Task.Run(() =>
                    MonitorController.SetVCPFeature(monitor.Handle, 0x60, targetCode));

                if (success)
                {
                    StatusText.Text = $"[{monitor.Name}] 切换指令已发送！";
                    // 切换成功后，手动更新一下界面显示的“当前输入”，让体验更流畅
                    // (虽然显示器自己反应过来需要几秒钟)
                    monitor.CurrentInputCode = targetCode;
                    // 通知界面数据变了 (简单粗暴的方法：刷新一下列表项)
                    int index = Monitors.IndexOf(monitor);
                    Monitors[index] = monitor;
                }
                else
                {
                    StatusText.Text = $"[{monitor.Name}] 切换失败，可能不支持 DDC/CI。";
                }

                button.IsEnabled = true;
            }
        }
    }

    // ==========================================
    // 底层 API 搬运工 (升级版)
    // ==========================================
    public static class MonitorController
    {
        [DllImport("user32.dll")]
        public static extern bool EnumDisplayMonitors(IntPtr hdc, IntPtr lprcClip, MonitorEnumDelegate lpfnEnum, int dwData);

        public delegate bool MonitorEnumDelegate(IntPtr hMonitor, IntPtr hdcMonitor, ref Rect lprcMonitor, IntPtr dwData);

        [StructLayout(LayoutKind.Sequential)]
        public struct Rect { public int left; public int top; public int right; public int bottom; }

        [DllImport("dxva2.dll")]
        public static extern bool GetPhysicalMonitorsFromHMONITOR(IntPtr hMonitor, uint dwPhysicalMonitorArraySize, [Out] PHYSICAL_MONITOR[] pPhysicalMonitorArray);

        [DllImport("dxva2.dll")]
        public static extern bool GetNumberOfPhysicalMonitorsFromHMONITOR(IntPtr hMonitor, out uint pdwNumberOfPhysicalMonitors);

        // 写指令 (切换输入用这个)
        [DllImport("dxva2.dll")]
        public static extern bool SetVCPFeature(IntPtr hMonitor, uint bVCPCode, uint dwNewValue);

        // 【新增】读指令 (获取当前输入用这个)
        [DllImport("dxva2.dll")]
        public static extern bool GetVCPFeatureAndVCPFeatureReply(IntPtr hMonitor, uint bVCPCode, out uint pvct, out uint pdwCurrentValue, out uint pdwMaximumValue);

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        public struct PHYSICAL_MONITOR
        {
            public IntPtr hPhysicalMonitor;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
            public string szPhysicalMonitorDescription;
        }
    }
}