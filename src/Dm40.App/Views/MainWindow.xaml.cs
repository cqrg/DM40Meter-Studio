using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Dm40.App.ViewModels;
using Dm40.Core;
using Microsoft.Web.WebView2.Core;

namespace Dm40.App.Views
{
    /// <summary>
    /// 主窗口 code-behind。中栏波形由 <see cref="Microsoft.Web.WebView2.Wpf.WebView2"/> 渲染：
    /// Assets/waveform.html（内联 echarts.min.js）——所有呈现与交互均用 ECharts 默认控件
    /// （tooltip / toolbox 区域缩放·还原·保存 / dataZoom inside 滚轮缩放+拖拽平移 / dataZoom slider 缩略条）。
    /// C# 仅经 ExecuteScriptAsync 推数据（setSeries/setMode/setWindow/setUnit）。其余 UI 全为原生 WPF。
    /// </summary>
    public partial class MainWindow : Window
    {
        private readonly MainViewModel _vm;
        private readonly DispatcherTimer _plotTimer;
        private Border _alarmToast;                 // 右下角报警 Toast

        // —— WebView2 桥接状态 ——
        private bool _webViewReady;
        private string _html;                       // 内联 echarts 后的波形 HTML（缓存）
        private bool _plotPaused;
        private bool _lastReplay = false;           // 上一次模式（检测切换 → setMode）
        private string _lastUnit;                   // 上一次单位（检测变化 → setUnit）

        public MainWindow() : this(new Dm40.Transport.BleMeterSource()) { }

        public MainWindow(Dm40.Core.IMeterDataSource source)
        {
            InitializeComponent();

            _vm = new MainViewModel(source);
            DataContext = _vm;
            Closed += (s, e) => _vm.Dispose();

            InitWaveformWebView();

            // 波形数据推送节流（~30fps）
            _plotTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(33) };
            _plotTimer.Tick += (s, e) => RefreshPlot();
            _plotTimer.Start();

            // 报警触发 → 声音 / Toast（报警判定在 Core 层，波形不再画上下限线）
            _vm.AlarmNotify += OnAlarmNotify;
            // 窗口秒数变化 → 推 setWindow（实时 X 轴固定窗口联动）
            _vm.PropertyChanged += (s, e) =>
            {
                if (e.PropertyName == nameof(MainViewModel.Alarm)) UpdateLimitLines();
                else if (e.PropertyName == nameof(MainViewModel.WindowSeconds))
                    Eval("dm40.setWindow(" + _vm.WindowSeconds + ")");
            };
            // P 暂停波形
            PreviewKeyDown += (s, e) => { if (e.Key == Key.P) { _plotPaused = !_plotPaused; e.Handled = true; } };
            // Ctrl+S 导出 / Ctrl+C 复制读数 / 报文页 Ctrl+F 聚焦查找
            KeyDown += OnGlobalKey;
            // 报文自动滚动：新增帧跟随顶部（暂停时停止）
            _vm.PacketLog.CollectionChanged += OnPacketLogChanged;
            // 一键截图：渲染整个窗口为 PNG 保存到 ScreenshotDir
            _vm.ScreenshotRequested += CaptureWindow;
        }

        // ===================== 一键截图（FR：渲染窗口 → PNG 落盘） =====================

        private void CaptureWindow()
        {
            try
            {
                // 先强制布局，确保 WebView2 宿主等元素进入可视树
                MainRoot.UpdateLayout();
                var bounds = VisualTreeHelper.GetDescendantBounds(MainRoot);
                int w = (int)Math.Ceiling(MainRoot.ActualWidth);
                int h = (int)Math.Ceiling(MainRoot.ActualHeight);
                if (w <= 0 || h <= 0) return;
                var rtb = new RenderTargetBitmap(w, h, 96, 96, PixelFormats.Pbgra32);
                var dv = new DrawingVisual();
                using (var dc = dv.RenderOpen())
                {
                    // 透明窗口（AllowsTransparency=True）渲染时无底色，PNG 查看器把透明当白色 → 四周白边。
                    // 先铺窗口根 Border 的实际背景色，再叠加内容，保证不透明、保留圆角。
                    dc.DrawRectangle(Background, null, new Rect(0, 0, w, h));
                    dc.DrawRectangle(new VisualBrush(MainRoot), null, new Rect(0, 0, w, h));
                }
                rtb.Render(dv);

                Directory.CreateDirectory(_vm.ScreenshotDir);
                string file = Path.Combine(_vm.ScreenshotDir, "DM40_" + DateTime.Now.ToString("yyyyMMdd_HHmmss") + ".png");
                var enc = new PngBitmapEncoder();
                enc.Frames.Add(BitmapFrame.Create(rtb));
                using (var fs = new FileStream(file, FileMode.Create))
                    enc.Save(fs);
                ShowShotToast("📷 已保存截图", file, openPath: file);
            }
            catch (System.Exception ex)
            {
                ShowShotToast("截图失败", ex.Message, openPath: null);
            }
        }

        // 右下角轻提示（复用报警 Toast 容器位置）。openPath 非空时显示"打开位置"链接——
        // 点击用资源管理器选中该文件，链接常驻不自动消失（路径文字仍 ~4.4s 淡出）。
        private void ShowShotToast(string title, string sub, string openPath)
        {
            RemoveAlarmToast();
            var panel = new StackPanel();
            panel.Children.Add(new TextBlock { Text = title, Foreground = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0xA7, 0xF3, 0xD0)), FontSize = 13, FontWeight = FontWeights.SemiBold });

            if (openPath != null)
            {
                // 链接行：点击 → explorer /select 定位到文件；点击后立即移除 Toast
                var link = new TextBlock
                {
                    Text = "📂 打开位置",
                    Foreground = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x34, 0xD3, 0x97)),
                    FontSize = 11.5, Cursor = Cursors.Hand, Margin = new Thickness(0, 3, 0, 0),
                    ToolTip = openPath,
                };
                link.MouseLeftButtonDown += (s, e) =>
                {
                    try { System.Diagnostics.Process.Start("explorer.exe", "/select,\"" + openPath + "\""); } catch { }
                    RemoveAlarmToast();
                    e.Handled = true;
                };
                panel.Children.Add(link);
            }

            var pathTb = new TextBlock { Text = sub, Foreground = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x6B, 0x72, 0x80)), FontSize = 10.5, Margin = new Thickness(0, 2, 0, 0) };
            panel.Children.Add(pathTb);

            _alarmToast = new Border
            {
                Background = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x14, 0x2A, 0x22)),
                BorderBrush = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x34, 0xD3, 0x97)),
                BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(8),
                Padding = new Thickness(14, 10, 14, 10),
                HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Bottom,
                Margin = new Thickness(0, 0, 22, 46),
                Child = panel,
            };
            MainRoot.Children.Add(_alarmToast);

            // 仅对路径文字淡出，链接常驻；点击链接或下次截图时移除
            var anim = new System.Windows.Media.Animation.DoubleAnimation(1, 0, TimeSpan.FromSeconds(3)) { BeginTime = TimeSpan.FromSeconds(1.4) };
            pathTb.BeginAnimation(OpacityProperty, anim);
        }

        // ===================== WebView2 初始化 =====================

        // 【本地诊断补丁 2026-10-26】原实现所有失败路径均静默（catch{} / !IsSuccess return），
        // 波形区空白时无从判断断在哪一层。此处加入 diag-webview.log 落盘，仅增加观测，不改行为。
        private static void DiagW(string msg)
        {
            try
            {
                File.AppendAllText(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "diag-webview.log"),
                    DateTime.Now.ToString("HH:mm:ss.fff") + "  " + msg + Environment.NewLine,
                    new System.Text.UTF8Encoding(false));
            }
            catch { }
        }

        private async void InitWaveformWebView()
        {
            DiagW("[init] 进入 InitWaveformWebView  IsLoaded=" + WaveformWeb.IsLoaded
                  + "  IsVisible=" + WaveformWeb.IsVisible
                  + "  WinLoaded=" + IsLoaded);

            WaveformWeb.CoreWebView2InitializationCompleted += (s, e) =>
            {
                DiagW("[init] InitializationCompleted IsSuccess=" + e.IsSuccess
                      + (e.IsSuccess ? "" : "  ex=" + (e.InitializationException?.ToString() ?? "null")));
                if (!e.IsSuccess) return;
                var cwv = WaveformWeb.CoreWebView2;
                cwv.Settings.IsZoomControlEnabled = false;
                cwv.Settings.AreDefaultContextMenusEnabled = false;
                cwv.Settings.IsStatusBarEnabled = false;
                var html = BuildWaveformHtml();
                DiagW("[init] BuildWaveformHtml 长度=" + html.Length + "，准备 NavigateToString");
                cwv.NavigateToString(html);
            };
            WaveformWeb.NavigationCompleted += (s, e) =>
            {
                DiagW("[nav] NavigationCompleted IsSuccess=" + e.IsSuccess + "  status=" + e.WebErrorStatus);
                if (!e.IsSuccess) return;
                _webViewReady = true;
                _lastUnit = null;          // 强制首帧推 setUnit
                Eval("dm40.setUnit(" + JsStr(_vm.Unit) + ")");
                Eval("dm40.setWindow(" + _vm.WindowSeconds + ")");
                Eval("dm40.setMode('" + (_vm.IsReplayMode ? "replay" : "live") + "')");
                UpdateLimitLines();         // 推初始上下限标注（若已设）
                _ = ProbeWaveJs();
            };
            try
            {
                DiagW("[init] 调用 EnsureCoreWebView2Async  IsLoaded=" + WaveformWeb.IsLoaded);
                await WaveformWeb.EnsureCoreWebView2Async(null);
                DiagW("[init] EnsureCoreWebView2Async 返回成功  CoreWebView2=" + (WaveformWeb.CoreWebView2 != null));
            }
            catch (Exception ex)
            {
                DiagW("[init] EnsureCoreWebView2Async EXCEPTION: " + ex);
            }
        }

        // 探测 JS 侧真实状态：echarts 是否加载、#chart 是否拿到尺寸
        private async System.Threading.Tasks.Task ProbeWaveJs()
        {
            try
            {
                var r = await WaveformWeb.CoreWebView2.ExecuteScriptAsync(
                    "JSON.stringify({echarts:typeof echarts," +
                    "cw:(document.getElementById('chart')||{}).clientWidth||-1," +
                    "ch:(document.getElementById('chart')||{}).clientHeight||-1," +
                    "bw:document.body.clientWidth,bh:document.body.clientHeight," +
                    "hasDm40:typeof window.dm40})");
                DiagW("[probe] " + r);
            }
            catch (Exception ex) { DiagW("[probe] EX " + ex.Message); }
        }

        // 读取本地 echarts.min.js，内联进 waveform.html 的占位符 → 单文件 NavigateToString（离线可靠）
        private string BuildWaveformHtml()
        {
            if (_html != null) return _html;
            var dir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Assets");
            var js = File.ReadAllText(Path.Combine(dir, "echarts.min.js"));
            var html = File.ReadAllText(Path.Combine(dir, "waveform.html"));
            _html = html.Replace("/*ECHARTS_INLINE*/", js);
            return _html;
        }

        // ===================== 波形数据推送（C# → JS） =====================

        private void RefreshPlot()
        {
            if (!_webViewReady || _plotPaused) return;

            // 模式切换：实时 ↔ 回放 → 重置 JS 端 X 轴基准
            bool replay = _vm.IsReplayMode;
            if (replay != _lastReplay)
            {
                _lastReplay = replay;
                Eval("dm40.setMode('" + (replay ? "replay" : "live") + "')");
                if (!replay) Eval("dm40.setWindow(" + _vm.WindowSeconds + ")");
            }

            var buffer = _vm.ActiveWaveform;
            var pts = buffer.Points;   // 锁内快照（WaveformBuffer 线程安全）
            if (pts.Length == 0) { Eval("dm40.setSeries([])"); return; }

            var now = DateTime.UtcNow;
            var window = TimeSpan.FromSeconds(_vm.WindowSeconds);
            DateTime t0 = pts[0].Timestamp;
            var seriesJson = new StringBuilder("[");
            int n = 0;
            for (int i = 0; i < pts.Length; i++)
            {
                double x;
                if (replay) x = (pts[i].Timestamp - t0).TotalSeconds;
                else { x = (pts[i].Timestamp - now).TotalSeconds; if (x < -window.TotalSeconds) continue; } // 实时只推窗口内
                if (n > 0) seriesJson.Append(',');
                seriesJson.Append('[').Append(ToJs(x)).Append(',').Append(ToJs(pts[i].Value)).Append(']');
                n++;
            }
            seriesJson.Append(']');
            Eval("dm40.setSeries(" + seriesJson + ")");

            // 单位变化 → setUnit（tooltip 显示联动）
            if (_vm.Unit != _lastUnit) { _lastUnit = _vm.Unit; Eval("dm40.setUnit(" + JsStr(_vm.Unit) + ")"); }
        }

        // ===================== JS 桥接小工具 =====================

        // 上下限标注（ECharts markLine）：阈值变化即推送；报警判定在 Core 层，此处仅可视化。
        private void UpdateLimitLines()
        {
            if (!_webViewReady) return;
            var up = _vm.Alarm.UpperLimit;
            var lo = _vm.Alarm.LowerLimit;
            string u = up.HasValue ? ToJs(up.Value) : "null";
            string l = lo.HasValue ? ToJs(lo.Value) : "null";
            Eval($"dm40.setLimits({u},{l},{JsStr(_vm.Unit)})");
        }

        private void Eval(string js)
        {
            if (!_webViewReady) return;
            try { WaveformWeb.CoreWebView2?.ExecuteScriptAsync(js); }   // fire-and-forget（UI 线程）
            catch { /* 忽略瞬时未就绪/脚本异常 */ }
        }

        private static string ToJs(double v) =>
            double.IsNaN(v) || double.IsInfinity(v) ? "0" : v.ToString("R", CultureInfo.InvariantCulture);

        private static string JsStr(string s) => "\"" + (s ?? "").Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";

        // ===================== 自定义标题栏 =====================

        private void TitleBarDrag(object sender, MouseButtonEventArgs e)
        {
            if (e.ChangedButton == MouseButton.Left)
            {
                if (WindowState == WindowState.Maximized) WindowState = WindowState.Normal;
                DragMove();
            }
        }

        private void OnMinimize(object sender, MouseButtonEventArgs e) => WindowState = WindowState.Minimized;
        private void OnMaximize(object sender, MouseButtonEventArgs e) =>
            WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
        private void OnClose(object sender, MouseButtonEventArgs e) => Close();

        // ===================== 报警声音 / 右下角 Toast（FR-007） =====================

        private void OnAlarmNotify(AlarmEvent e)
        {
            if (_vm.AlarmSound) System.Media.SystemSounds.Hand.Play();
            if (_vm.AlarmPopup) ShowAlarmToast(e);
        }

        // 右下角弹出报警 Toast（深红底），3 秒淡出
        private void ShowAlarmToast(AlarmEvent e)
        {
            RemoveAlarmToast();
            string unit = _vm.Unit;
            _alarmToast = new Border
            {
                Background = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x7F, 0x1D, 0x1D)),
                BorderBrush = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0xEF, 0x44, 0x44)),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(8),
                Padding = new Thickness(14, 10, 14, 10),
                HorizontalAlignment = HorizontalAlignment.Right,
                VerticalAlignment = VerticalAlignment.Bottom,
                Margin = new Thickness(0, 0, 22, 46),
                Child = new TextBlock
                {
                    Text = $"⚠ {(e.Direction == AlarmDirection.High ? "超上限" : "低于下限")}  {e.Value:0.####} {unit}",
                    Foreground = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0xFE, 0xCA, 0xCA)),
                    FontSize = 13,
                    FontWeight = FontWeights.SemiBold,
                },
            };
            MainRoot.Children.Add(_alarmToast);
            var anim = new System.Windows.Media.Animation.DoubleAnimation(1, 0, TimeSpan.FromSeconds(3))
            {
                BeginTime = TimeSpan.FromSeconds(1.2),
            };
            anim.Completed += (s, a) => RemoveAlarmToast();
            _alarmToast.BeginAnimation(OpacityProperty, anim);
        }

        private void RemoveAlarmToast()
        {
            if (_alarmToast != null) { MainRoot.Children.Remove(_alarmToast); _alarmToast = null; }
        }

        // ===================== 全局快捷键（P 暂停 / Ctrl+S 导出 / Ctrl+C 复制） =====================

        private void OnGlobalKey(object sender, KeyEventArgs e)
        {
            if (Keyboard.Modifiers == ModifierKeys.Control && e.Key == Key.S)
            {
                ExportCsv(sender, e);
                e.Handled = true;
            }
            else if (Keyboard.Modifiers == ModifierKeys.Control && e.Key == Key.C)
            {
                try { Clipboard.SetText($"{_vm.MainReading} {_vm.Unit}".Trim()); } catch { }
                e.Handled = true;
            }
            else if (Keyboard.Modifiers == ModifierKeys.Control && e.Key == Key.F && TabPacket.IsChecked == true)
            {
                // 报文页 Ctrl+F → 聚焦 HEX 查找框并全选
                PacketSearchBox.Focus();
                PacketSearchBox.SelectAll();
                e.Handled = true;
            }
            else if (Keyboard.Modifiers == ModifierKeys.Control && e.Key == Key.P)
            {
                CaptureWindow();   // Ctrl+P 一键截图
                e.Handled = true;
            }
        }

        // ===================== 页签 / 对话框 =====================

        private void TabChanged(object sender, RoutedEventArgs e)
        {
            if (LiveView == null) return;
            LiveView.Visibility = TabLive.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
            PacketView.Visibility = TabPacket.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
            ReplayView.Visibility = TabReplay.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
            // 报文工具条（查找/暂停滚动/清空）与窗口秒数标签按页签互斥显隐
            bool packet = TabPacket.IsChecked == true;
            PacketToolbar.Visibility = packet ? Visibility.Visible : Visibility.Collapsed;
            WindowLabel.Visibility = packet ? Visibility.Collapsed : Visibility.Visible;
        }

        // ===================== 原始报文页：自动滚动 / 暂停 / 查找 =====================
        // 关键：绝不在 CollectionChanged 处理器里"同步"调 ScrollIntoView——它会 BringItemIntoView →
        // 强制 UpdateLayout，而 ItemContainerGenerator 正处理上一个变更，检测到不一致即抛
        // InvalidOperationException（"ItemsControl 与它的项源不一致"）导致崩溃。所有滚动/选中一律
        // BeginInvoke 延迟到布局稳定后执行。暂停滚动的真正实现是 VM 的 PacketPaused（暂停时停止插入）。

        private bool _packetHoldView; // 查找命中后钉住视口（不被随后新帧的滚到顶覆盖）
        private int _packetSearchIdx = -1; // 上次查找命中项索引（PacketLog 第 0 条=最新）

        // 报文新增（Insert(0)）→ 未钉住时滚到顶跟随最新帧（延迟执行，避开 CollectionChanged 重入）；清空时复位查找位置
        private void OnPacketLogChanged(object sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs e)
        {
            if (e.Action == System.Collections.Specialized.NotifyCollectionChangedAction.Reset)
            {
                _packetSearchIdx = -1;
                return;
            }
            if (_packetHoldView || PacketView.Visibility != Visibility.Visible) return;
            if (e.Action != System.Collections.Specialized.NotifyCollectionChangedAction.Add) return;
            Dispatcher.BeginInvoke(new Action(() =>
            {
                if (PacketList.Items.Count > 0) PacketList.ScrollIntoView(PacketList.Items[0]);
            }), System.Windows.Threading.DispatcherPriority.Background);
        }

        private void OnPacketPause(object sender, RoutedEventArgs e)
        {
            _vm.PacketPaused = !_vm.PacketPaused;   // 暂停=停止向查看器插入新帧（画面冻结）
            _packetHoldView = false;
            PacketPauseBtn.Content = _vm.PacketPaused ? "▶ 继续滚动" : "⏸ 暂停滚动";
        }

        // 输入即搜索（找最新一条命中），无需按 Enter
        private void OnPacketSearchChanged(object sender, TextChangedEventArgs e)
        {
            _packetSearchIdx = -1; // 关键词变了，从最新一条重新找
            PacketFind(backward: true);
        }

        // 查找框键盘：Enter 下一个 / Shift+Enter 上一个 / Esc 清空并移焦
        private void OnPacketSearchKey(object sender, System.Windows.Input.KeyEventArgs e)
        {
            if (e.Key == System.Windows.Input.Key.Enter)
            {
                bool backward = (System.Windows.Input.Keyboard.Modifiers & System.Windows.Input.ModifierKeys.Shift) != 0;
                PacketFind(backward);
                e.Handled = true;
            }
            else if (e.Key == System.Windows.Input.Key.Escape)
            {
                PacketSearchBox.Text = "";
                _vm.PacketFilter = "";
                _packetSearchIdx = -1;
                _packetHoldView = false;
                System.Windows.Input.Keyboard.ClearFocus();
                e.Handled = true;
            }
        }

        // HEX 关键词查找（不区分大小写、忽略空格/连写）。新到旧=向下，旧到新=向上。
        // 选中/滚动一律 BeginInvoke 延迟，避免在输入/集合变更的同一帧内强制布局。
        private void PacketFind(bool backward)
        {
            string raw = _vm.PacketFilter ?? "";
            string key = raw.Replace(" ", "").Replace("-", "");
            if (key.Length == 0 || PacketList.Items.Count == 0) { _packetSearchIdx = -1; return; }

            int count = PacketList.Items.Count;
            int start = _packetSearchIdx;
            for (int step = 1; step <= count; step++)
            {
                int i = backward
                    ? (start + step) % count                       // 向旧（索引增大）
                    : (start - step + count * step) % count;       // 向新（索引减小）
                var pe = PacketList.Items[i] as ViewModels.PacketEntry;
                if (pe == null) continue;
                string hex = (pe.Hex ?? "").Replace(" ", "").Replace("-", "");
                if (hex.IndexOf(key, StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    _packetSearchIdx = i;
                    _packetHoldView = true;   // 钉住视口：命中项不被随后新帧顶走
                    Dispatcher.BeginInvoke(new Action(() =>
                    {
                        PacketList.SelectedItem = pe;
                        PacketList.ScrollIntoView(pe);
                    }), System.Windows.Threading.DispatcherPriority.Background);
                    return;
                }
            }
        }

        private void OpenSettings(object sender, RoutedEventArgs e)
        {
            var dlg = new SettingsWindow(_vm) { Owner = this };
            dlg.ShowDialog();
        }

        private void OpenReplay(object sender, RoutedEventArgs e)
        {
            var ofd = new Microsoft.Win32.OpenFileDialog
            {
                Filter = "CSV 测量文件 (*.csv)|*.csv",
                Title = "打开回放",
            };
            if (ofd.ShowDialog(this) == true)
            {
                var err = _vm.LoadReplay(ofd.FileName);
                if (err != null)
                    MessageBox.Show(this, "无法回放: " + err, "文件格式错误",
                        MessageBoxButton.OK, MessageBoxImage.Error);
                else
                    TabReplay.IsChecked = true; // 切到回放页签
            }
        }

        private void ExitReplay(object sender, RoutedEventArgs e)
        {
            _vm.ExitReplay();
            TabLive.IsChecked = true; // 回到实时页签
        }

        private void ExportCsv(object sender, RoutedEventArgs e)
        {
            if (_vm.Waveform.Count == 0)
            {
                MessageBox.Show(this, "当前没有波形数据可导出。请先连接设备采集。", "导出 CSV",
                    MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }
            var sfd = new Microsoft.Win32.SaveFileDialog
            {
                Filter = "CSV 测量文件 (*.csv)|*.csv",
                FileName = "DM40_" + DateTime.Now.ToString("yyyyMMdd_HHmmss") + ".csv",
                Title = "导出 CSV",
            };
            if (sfd.ShowDialog(this) == true)
            {
                try
                {
                    int n = _vm.ExportCsv(sfd.FileName);
                    MessageBox.Show(this, $"已导出 {n:N0} 条记录到\n{sfd.FileName}", "导出完成",
                        MessageBoxButton.OK, MessageBoxImage.Information);
                }
                catch (System.Exception ex)
                {
                    MessageBox.Show(this, "导出失败: " + ex.Message, "错误",
                        MessageBoxButton.OK, MessageBoxImage.Error);
                }
            }
        }
    }
}
