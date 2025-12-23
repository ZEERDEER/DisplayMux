using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using System;
using System.Collections.Generic; // 引用 List 用于排序
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq; // 引用 Linq 用于排序
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using WinRT.Interop;

namespace displaymux
{
    // ==========================================
    // 1. 数据模型类
    // ==========================================
    public class DisplayMonitor : INotifyPropertyChanged
    {
        public string Name { get; set; }
        public IntPtr Handle { get; set; }

        // 显示器编号 (1, 2, 3...)
        public int MonitorId { get; set; }

        // 用于显示的完整名称，例如 "1. Dell U2720Q"
        // ⚠️ 记得在 XAML 里把 Text="{Binding Name}" 改成 Text="{Binding DisplayName}"
        public string DisplayName => $"{MonitorId}. {Name}";

        private uint _currentInputCode;
        public event PropertyChangedEventHandler? PropertyChanged;

        public uint CurrentInputCode
        {
            get => _currentInputCode;
            set
            {
                if (_currentInputCode != value)
                {
                    _currentInputCode = value;
                    OnPropertyChanged();
                    OnPropertyChanged(nameof(CurrentInputName));
                }
            }
        }

        // 只保留您需要的接口
        public string CurrentInputName
        {
            get
            {
                switch (CurrentInputCode)
                {
                    case 15: return "DisplayPort";
                    case 17: return "HDMI 1";
                    case 18: return "HDMI 2";

                    case 0: return "未知/正在获取...";
                    default: return $"未知接口 ({CurrentInputCode})";
                }
            }
        }

        private void OnPropertyChanged([CallerMemberName] string propertyName = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }

        // 辅助字段：用于排序的 X 坐标
        public int ScreenLeft { get; set; }
    }

    // ==========================================
    // 2. 主窗口逻辑
    // ==========================================
    public sealed partial class MainWindow : Window
    {
        public ObservableCollection<DisplayMonitor> Monitors { get; set; } = new();

        public MainWindow()
        {
            this.InitializeComponent();
            this.SystemBackdrop = new MicaBackdrop();
            this.ExtendsContentIntoTitleBar = true;
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

        private void LoadMonitors()
        {
            StatusText.Text = "正在扫描显示器...";

            var tempList = new List<DisplayMonitor>();

            MonitorController.EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero,
                delegate (IntPtr hMonitor, IntPtr hdcMonitor, ref MonitorController.Rect lprcMonitor, IntPtr dwData)
                {
                    // 1. 获取 Windows 编号
                    var mi = new MonitorController.MONITORINFOEX();
                    mi.Size = Marshal.SizeOf(mi);
                    int monitorId = 0;
                    if (MonitorController.GetMonitorInfo(hMonitor, ref mi))
                    {
                        string devName = mi.DeviceName;
                        string numberStr = devName.Replace(@"\\.\DISPLAY", "");
                        int.TryParse(numberStr, out monitorId);
                    }

                    // 2. 获取物理显示器句柄
                    uint count;
                    if (MonitorController.GetNumberOfPhysicalMonitorsFromHMONITOR(hMonitor, out count) && count > 0)
                    {
                        var physicalMonitors = new MonitorController.PHYSICAL_MONITOR[count];
                        if (MonitorController.GetPhysicalMonitorsFromHMONITOR(hMonitor, count, physicalMonitors))
                        {
                            foreach (var pm in physicalMonitors)
                            {
                                var newMonitor = new DisplayMonitor
                                {
                                    Name = pm.szPhysicalMonitorDescription.Replace("\0", ""),
                                    Handle = pm.hPhysicalMonitor,
                                    CurrentInputCode = 0,
                                    MonitorId = monitorId,
                                    ScreenLeft = lprcMonitor.left
                                };

                                // 读取初始状态
                                uint currentValue = 0;
                                if (MonitorController.GetVCPFeatureAndVCPFeatureReply(pm.hPhysicalMonitor, 0x60, out _, out currentValue, out _))
                                {
                                    newMonitor.CurrentInputCode = currentValue;
                                }

                                tempList.Add(newMonitor);
                            }
                        }
                    }
                    return true;
                }, 0);

            // 3. 按屏幕位置从左到右排序
            var sortedList = tempList.OrderBy(x => x.ScreenLeft).ToList();

            Monitors.Clear();
            foreach (var m in sortedList)
            {
                Monitors.Add(m);
            }

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
            var comboBox = parent.Children[2] as ComboBox;

            if (comboBox != null && comboBox.SelectedItem is ComboBoxItem selectedItem)
            {
                uint targetCode = uint.Parse(selectedItem.Tag.ToString());
                string inputName = selectedItem.Content.ToString();

                StatusText.Text = $"正在切换 {monitor.DisplayName} ...";
                button.IsEnabled = false;

                // 1. 发送切换指令
                bool sendSuccess = await Task.Run(() =>
                    MonitorController.SetVCPFeature(monitor.Handle, 0x60, targetCode));

                if (sendSuccess)
                {
                    // 2. 乐观更新：立刻把界面文字改成目标接口 (下拉菜单不会动)
                    monitor.CurrentInputCode = targetCode;

                    StatusText.Text = $"指令已发送，等待显示器确认...";

                    // 3. 【关键延迟】等待 6 秒，给显示器足够的“后悔时间”
                    await Task.Delay(6000);

                    // 4. 回头查岗：读取真实状态
                    uint realCode = 0;
                    bool readSuccess = await Task.Run(() =>
                        MonitorController.GetVCPFeatureAndVCPFeatureReply(monitor.Handle, 0x60, out _, out realCode, out _));

                    if (readSuccess)
                    {
                        // 如果显示器跳回去了，这里会读到旧接口，界面文字会自动变回去
                        monitor.CurrentInputCode = realCode;
                        StatusText.Text = $"[{monitor.Name}] 状态确认完成。";
                    }
                    else
                    {
                        StatusText.Text = $"[{monitor.Name}] 无法确认最终状态。";
                    }
                }
                else
                {
                    StatusText.Text = "切换失败，可能不支持 DDC/CI。";
                }
                button.IsEnabled = true;
            }
        }
    }

    // ==========================================
    // 3. 底层 API
    // ==========================================
    public static class MonitorController
    {
        [DllImport("user32.dll")]
        public static extern bool EnumDisplayMonitors(IntPtr hdc, IntPtr lprcClip, MonitorEnumDelegate lpfnEnum, int dwData);
        public delegate bool MonitorEnumDelegate(IntPtr hMonitor, IntPtr hdcMonitor, ref Rect lprcMonitor, IntPtr dwData);

        [DllImport("user32.dll", CharSet = CharSet.Auto)]
        public static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFOEX lpmi);

        [StructLayout(LayoutKind.Sequential)]
        public struct Rect { public int left; public int top; public int right; public int bottom; }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
        public struct MONITORINFOEX
        {
            public int Size;
            public Rect Monitor;
            public Rect WorkArea;
            public uint Flags;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
            public string DeviceName;
        }

        [DllImport("dxva2.dll")]
        public static extern bool GetPhysicalMonitorsFromHMONITOR(IntPtr hMonitor, uint dwPhysicalMonitorArraySize, [Out] PHYSICAL_MONITOR[] pPhysicalMonitorArray);
        [DllImport("dxva2.dll")]
        public static extern bool GetNumberOfPhysicalMonitorsFromHMONITOR(IntPtr hMonitor, out uint pdwNumberOfPhysicalMonitors);
        [DllImport("dxva2.dll")]
        public static extern bool SetVCPFeature(IntPtr hMonitor, uint bVCPCode, uint dwNewValue);
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