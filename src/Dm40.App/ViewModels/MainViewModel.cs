using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;
using Dm40.Core;
using Dm40.Protocol;

namespace Dm40.App.ViewModels
{
    /// <summary>
    /// 主界面 ViewModel：聚合 MeterSession（统计/报警/波形/录制）为可绑定状态。
    /// 帧回调在后台线程，所有 UI 状态更新经 Dispatcher marshal 到 UI 线程；
    /// 读数刷新按 ~30fps 节流合并，防高频闪烁（PRD FR-003、§15.1 约束4）。
    /// </summary>
    public sealed class MainViewModel : ViewModelBase, IDisposable
    {
        private readonly MeterSession _session;
        private readonly IMeterDataSource _source;
        private readonly Dispatcher _dispatcher;
        private readonly DispatcherTimer _uiTimer;   // 30fps UI 刷新节流
        private readonly DispatcherTimer _recTimer;  // 录制每秒 flush + REC 计时

        private Measurement _latest;
        private bool _dirty;

        // 帧率/延迟统计
        private int _frameCountWindow;
        private DateTime _frameWindowStart;
        private DateTime _lastFrameTime;

        // 录制计时
        private DateTime _recStartTime;

        // ===================== 数据回放（FR-010） =====================
        private readonly WaveformBuffer _replayWaveform = new WaveformBuffer();
        private readonly DispatcherTimer _replayTimer;
        private System.Collections.Generic.List<CsvRecord> _replayRecords;
        private int _replayIndex;              // 下一条待播记录的索引
        private double _replaySpeed = 1.0;     // 倍速 0.5/1/2/4/10
        private DateTime _replayWallStart;     // 播放起始墙钟
        private double _replayVirtualStartMs;  // 播放起始时的虚拟进度（毫秒，相对首条记录）
        private bool _replayPlaying;

        public MainViewModel(IMeterDataSource source, Dispatcher dispatcher = null)
        {
            _source = source ?? throw new ArgumentNullException(nameof(source));
            _dispatcher = dispatcher ?? Application.Current?.Dispatcher ?? Dispatcher.CurrentDispatcher;
            _session = new MeterSession(source, SystemClock.Instance);

            _session.ReadingAvailable += OnReading;
            _session.RawFrame += OnRawFrame;
            _session.AlarmTriggered += OnAlarm;
            _session.ModeChanged += OnModeChanged;
            _source.StateChanged += OnStateChanged;
            _source.Diagnostic += OnDiagnostic;

            Devices = new ObservableCollection<DiscoveredDevice>();
            PacketLog = new ObservableCollection<PacketEntry>();
            AlarmLog = new ObservableCollection<string>();

            ScanCommand = new RelayCommand(StartScan, () => State == ConnectionState.Disconnected);
            ConnectCommand = new RelayCommand(Connect, () => SelectedDevice != null);
            DisconnectCommand = new RelayCommand(Disconnect, () => State != ConnectionState.Disconnected);
            ConnectDisconnectCommand = new RelayCommand(() => { if (IsConnected) Disconnect(); else Connect(); }, () => SelectedDevice != null || IsConnected);
            ToggleRecordingCommand = new RelayCommand(ToggleRecording);
            ResetStatsCommand = new RelayCommand(() => _session.Statistics.Reset());
            ClearPacketLogCommand = new RelayCommand(() => { PacketLog.Clear(); PacketFailCount = 0; RaisePacketStatsChanged(); });
            HoldCommand = new RelayCommand(() => { Send(HoldActive ? ProtocolConstants.CmdHoldOff : ProtocolConstants.CmdHoldOn); HoldActive = !HoldActive; }, () => CanOperateDevice);
            AutoCommand = new RelayCommand(() => { Send(AutoActive ? ProtocolConstants.CmdAutoOff : ProtocolConstants.CmdAutoOn); AutoActive = !AutoActive; }, () => CanOperateDevice);
            RelCommand = new RelayCommand(() => { Send(ProtocolConstants.CmdRelative); RelActive = !RelActive; }, () => CanOperateDevice);
            SendModeCommand = new RelayCommand(p => SendMode(p as string), _ => CanOperateDevice);

            _frameWindowStart = DateTime.UtcNow;

            _uiTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(33) }; // ~30fps
            _uiTimer.Tick += (s, e) => FlushLatest();
            _uiTimer.Start();

            _recTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
            _recTimer.Tick += (s, e) => { _session.TickRecorder(); TickRecStatus(); };
            _recTimer.Start();

            // 回放推进定时器（~20ms tick，按倍速换算虚拟进度）
            _replayTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(20) };
            _replayTimer.Tick += (s, e) => ReplayTick();

            ReplayPlayCommand = new RelayCommand(ReplayPlayPause, () => IsReplayLoaded);
            ReplayStopCommand = new RelayCommand(ReplayStop, () => IsReplayLoaded);
            SetReplaySpeedCommand = new RelayCommand(p => SetReplaySpeed(double.Parse((string)p, System.Globalization.CultureInfo.InvariantCulture)), _ => IsReplayLoaded);
            ScreenshotCommand = new RelayCommand(() => ScreenshotRequested?.Invoke());
        }

        // ===================== 读数面板 =====================

        private string _mainReading = "---";
        public string MainReading { get => _mainReading; private set => Set(ref _mainReading, value); }

        private string _unit = "";
        public string Unit { get => _unit; private set => Set(ref _unit, value); }

        private string _modeLabel = "直流电压 · DCV";
        public string ModeLabel { get => _modeLabel; private set => Set(ref _modeLabel, value); }

        private string _range = "";
        public string Range { get => _range; private set => Set(ref _range, value); }

        private string _secondary1 = "--";
        public string Secondary1 { get => _secondary1; private set => Set(ref _secondary1, value); }

        private string _secondary2 = "--";
        public string Secondary2 { get => _secondary2; private set => Set(ref _secondary2, value); }

        // ---- 状态徽章（电量/HOLD/REL/锁定） ----

        private string _batteryBadge = "🔋 --";
        public string BatteryBadge { get => _batteryBadge; private set => Set(ref _batteryBadge, value); }

        private Visibility _holdBadgeVis = Visibility.Collapsed;
        public Visibility HoldBadgeVis { get => _holdBadgeVis; private set => Set(ref _holdBadgeVis, value); }

        private Visibility _relBadgeVis = Visibility.Collapsed;
        public Visibility RelBadgeVis { get => _relBadgeVis; private set => Set(ref _relBadgeVis, value); }

        private Visibility _lockBadgeVis = Visibility.Collapsed;
        public Visibility LockBadgeVis { get => _lockBadgeVis; private set => Set(ref _lockBadgeVis, value); }

        // ---- 设备控制矩阵 active 态 ----

        private bool _holdActive;
        public bool HoldActive { get => _holdActive; set => Set(ref _holdActive, value); }

        private bool _autoActive = true; // DM40 默认自动量程
        public bool AutoActive { get => _autoActive; set => Set(ref _autoActive, value); }

        private bool _relActive;
        public bool RelActive { get => _relActive; set => Set(ref _relActive, value); }

        // ===================== 连接状态 =====================

        private string _statusLine = "未连接";
        public string StatusLine { get => _statusLine; private set => Set(ref _statusLine, value); }

        private ConnectionState _state = ConnectionState.Disconnected;
        public ConnectionState State
        {
            get => _state;
            private set
            {
                if (Set(ref _state, value))
                {
                    OnPropertyChanged(nameof(IsConnected));
                    OnPropertyChanged(nameof(ConnDotBrush));
                    OnPropertyChanged(nameof(ConnStatusText));
                    OnPropertyChanged(nameof(CanOperateDevice));
                    OnPropertyChanged(nameof(ConnectButtonText));
                    // 连接态变化 → 主动让所有命令重判 canExecute（设备控制键/连接键随即可点）。
                    // 仅靠 CommandManager.RequerySuggested 不会因 ViewModel 属性变化自动触发。
                    ScanCommand.RaiseCanExecuteChanged();
                    ConnectCommand.RaiseCanExecuteChanged();
                    DisconnectCommand.RaiseCanExecuteChanged();
                    ConnectDisconnectCommand.RaiseCanExecuteChanged();
                    HoldCommand.RaiseCanExecuteChanged();
                    AutoCommand.RaiseCanExecuteChanged();
                    RelCommand.RaiseCanExecuteChanged();
                    SendModeCommand.RaiseCanExecuteChanged();
                }
            }
        }
        public bool IsConnected => State == ConnectionState.Measuring || State == ConnectionState.Connected;

        /// <summary>连接按钮文字：已连接显示"断开"，未连接显示"连接"（ConnectDisconnect 命令切换）。</summary>
        public string ConnectButtonText => IsConnected ? "断开" : "⚡ 连接";

        public Brush ConnDotBrush => IsConnected
            ? new SolidColorBrush(Color.FromRgb(0x34, 0xD3, 0x99))
            : new SolidColorBrush(Color.FromRgb(0x6B, 0x72, 0x80));

        public string ConnStatusText => IsConnected
            ? $"已连接 · {(_selectedDevice?.Name ?? "DM40")}"
            : "未连接";

        // ===================== 帧率 / 延迟 =====================

        private string _frameRateText = "帧率 -- Hz";
        public string FrameRateText { get => _frameRateText; private set => Set(ref _frameRateText, value); }

        private string _latencyText = "延迟 -- ms";
        public string LatencyText { get => _latencyText; private set => Set(ref _latencyText, value); }

        private int _windowSeconds = 60;
        /// <summary>实时波形时间窗口（秒）：30 / 60 / 300 / 600。</summary>
        public int WindowSeconds { get => _windowSeconds; set => Set(ref _windowSeconds, value); }

        // ===================== 统计 =====================

        public string StatMin => Fmt(_session.Statistics.Min);
        public string StatMax => Fmt(_session.Statistics.Max);
        public string StatAvg => Fmt(_session.Statistics.Avg);
        public string StatDelta => Fmt(_session.Statistics.Delta);
        public int StatCount => _session.Statistics.Count;

        private string _statSummary = "样本 0 · 时长 00:00:00";
        public string StatSummary { get => _statSummary; private set => Set(ref _statSummary, value); }
        private DateTime? _statStart;

        // ===================== 阈值报警 =====================

        public ThresholdAlarm Alarm => _session.Alarm;

        public string AlarmEnabledText => _session.Alarm.IsEnabled ? "已启用" : "未启用";

        private string _alarmUpperText = "";
        private string _alarmLowerText = "";

        public string AlarmUpper
        {
            get => _alarmUpperText;
            set
            {
                _alarmUpperText = value ?? "";
                _session.Alarm.UpperLimit = ParseLimit(value);
                UpdateAlarmEnabled();
                OnPropertyChanged(nameof(AlarmUpper));
                OnPropertyChanged(nameof(Alarm));
            }
        }

        public string AlarmLower
        {
            get => _alarmLowerText;
            set
            {
                _alarmLowerText = value ?? "";
                _session.Alarm.LowerLimit = ParseLimit(value);
                UpdateAlarmEnabled();
                OnPropertyChanged(nameof(AlarmLower));
                OnPropertyChanged(nameof(Alarm));
            }
        }

        /// <summary>越限触发时是否播放提示音（阈值面板 🔊 声音开关）。</summary>
        private bool _alarmSound = true;
        public bool AlarmSound { get => _alarmSound; set => Set(ref _alarmSound, value); }

        /// <summary>越限触发时是否右下角弹窗提示（阈值面板 🪟 弹窗开关）。</summary>
        private bool _alarmPopup = true;
        public bool AlarmPopup { get => _alarmPopup; set => Set(ref _alarmPopup, value); }

        /// <summary>越限触发通知（UI 线程）。View 订阅以播放声音/右下角 Toast（FR-007）。</summary>
        public event Action<AlarmEvent> AlarmNotify;

        // ===================== 选区统计（波形框选 Min/Max/Δ） =====================

        private string _selMin = "--";
        public string SelMin { get => _selMin; set => Set(ref _selMin, value); }

        private string _selMax = "--";
        public string SelMax { get => _selMax; set => Set(ref _selMax, value); }

        private string _selDelta = "--";
        public string SelDelta { get => _selDelta; set => Set(ref _selDelta, value); }

        // ===================== 录制 =====================

        private bool _isRecording;
        public bool IsRecording
        {
            get => _isRecording;
            private set
            {
                if (Set(ref _isRecording, value))
                {
                    OnPropertyChanged(nameof(RecIndicatorVis));
                    OnPropertyChanged(nameof(RecordButtonContent));
                }
            }
        }

        public Visibility RecIndicatorVis => IsRecording ? Visibility.Visible : Visibility.Collapsed;
        public string RecordButtonContent => IsRecording ? "⏹ 停止录制 (R)" : "⏺ 开始录制 (R)";

        private string _recStatusText = "REC 00:00:00 · 0 样本";
        public string RecStatusText { get => _recStatusText; private set => Set(ref _recStatusText, value); }

        // ===================== 波形 / 集合 =====================

        public WaveformBuffer Waveform => _session.Waveform;

        public ObservableCollection<DiscoveredDevice> Devices { get; }
        public ObservableCollection<PacketEntry> PacketLog { get; }

        private int _packetFailCount;
        /// <summary>原始报文中 CRC 失败帧累计（状态栏显示，清空报文时归零）。</summary>
        public int PacketFailCount { get => _packetFailCount; private set => Set(ref _packetFailCount, value); }

        private string _packetFilter = "";
        /// <summary>报文 HEX 查找关键词（原始报文页 Ctrl+F）。</summary>
        public string PacketFilter { get => _packetFilter; set => Set(ref _packetFilter, value); }

        private bool _packetPaused;
        /// <summary>报文查看器暂停：为 true 时停止向 PacketLog 插入新帧（查看器画面冻结，便于阅读）。
        /// 暂停期间到达的帧不再进查看器（仅影响显示，CRC 失败仍计数）；这是"暂停滚动"的真正实现——
        /// 从源头消除 Insert(0) 顶部重排，而不是插入后再对抗滚动。</summary>
        public bool PacketPaused { get => _packetPaused; set => Set(ref _packetPaused, value); }

        private bool _autoConnect = true;
        /// <summary>扫描到设备后自动连接第一个（可在设置中关闭）。</summary>
        public bool AutoConnect { get => _autoConnect; set => Set(ref _autoConnect, value); }

        /// <summary>报文页底部状态：已缓存帧数。</summary>
        public string PacketCountText => $"已缓存 {PacketLog.Count:N0} 帧";
        /// <summary>报文页底部状态：CRC 失败帧数（0 时不显示）。</summary>
        public string PacketFailText => PacketFailCount > 0 ? $"CRC 失败 {PacketFailCount} 帧" : "";
        /// <summary>报文帧数/失败数变化后刷新底部状态文本。</summary>
        internal void RaisePacketStatsChanged()
        {
            OnPropertyChanged(nameof(PacketCountText));
            OnPropertyChanged(nameof(PacketFailText));
        }
        public ObservableCollection<string> AlarmLog { get; }

        private DiscoveredDevice _selectedDevice;
        public DiscoveredDevice SelectedDevice
        {
            get => _selectedDevice;
            set
            {
                if (Set(ref _selectedDevice, value))
                {
                    OnPropertyChanged(nameof(ConnStatusText));
                    // 连接按钮可用条件含 SelectedDevice，需主动重判（否则扫描后按钮灰，要点屏幕才亮）
                    ConnectCommand.RaiseCanExecuteChanged();
                    ConnectDisconnectCommand.RaiseCanExecuteChanged();
                }
            }
        }

        private string _replayReading = "--";
        public string ReplayReading { get => _replayReading; private set => Set(ref _replayReading, value); }

        private string _replayUnit = "";
        public string ReplayUnit { get => _replayUnit; private set => Set(ref _replayUnit, value); }

        // ---- 回放状态 ----
        public bool IsReplayLoaded => _replayRecords != null && _replayRecords.Count > 0;

        private bool _isReplayMode;
        /// <summary>回放模式开启：波形/读数切到回放数据，设备控制与录制应被禁用（互斥）。</summary>
        public bool IsReplayMode
        {
            get => _isReplayMode;
            private set
            {
                if (Set(ref _isReplayMode, value))
                {
                    OnPropertyChanged(nameof(ActiveWaveform));
                    OnPropertyChanged(nameof(ReplayModeHintVis));
                    OnPropertyChanged(nameof(IsLiveMode));
                    OnPropertyChanged(nameof(CanOperateDevice));
                    // 回放切换 → 设备控制/录制命令重判（回放时禁用）
                    HoldCommand.RaiseCanExecuteChanged();
                    AutoCommand.RaiseCanExecuteChanged();
                    RelCommand.RaiseCanExecuteChanged();
                    SendModeCommand.RaiseCanExecuteChanged();
                }
            }
        }

        public Visibility ReplayModeHintVis => IsReplayMode ? Visibility.Visible : Visibility.Collapsed;

        /// <summary>实时（非回放）模式。</summary>
        public bool IsLiveMode => !IsReplayMode;

        /// <summary>可操作设备：已连接且非回放模式（回放时禁用设备控制/录制，互斥）。</summary>
        public bool CanOperateDevice => IsConnected && IsLiveMode;

        /// <summary>当前应显示的波形：回放模式返回回放缓冲，否则实时缓冲。</summary>
        public WaveformBuffer ActiveWaveform => IsReplayMode ? _replayWaveform : _session.Waveform;

        private double _replayProgress; // 0..100
        public double ReplayProgress { get => _replayProgress; private set => Set(ref _replayProgress, value); }

        private string _replayTimeText = "00:00:00";
        public string ReplayTimeText { get => _replayTimeText; private set => Set(ref _replayTimeText, value); }

        private string _replayStatusText = "未加载";
        public string ReplayStatusText { get => _replayStatusText; private set => Set(ref _replayStatusText, value); }

        private string _replayPlayLabel = "▶ 播放";
        public string ReplayPlayLabel { get => _replayPlayLabel; private set => Set(ref _replayPlayLabel, value); }

        private double _replaySpeedLabel = 1.0;
        public double ReplaySpeedLabel { get => _replaySpeedLabel; private set => Set(ref _replaySpeedLabel, value); }

        // ===================== 命令 =====================

        public RelayCommand ScanCommand { get; }
        public RelayCommand ConnectCommand { get; }
        public RelayCommand DisconnectCommand { get; }
        public RelayCommand ConnectDisconnectCommand { get; }
        public RelayCommand ToggleRecordingCommand { get; }
        public RelayCommand ResetStatsCommand { get; }
        public RelayCommand HoldCommand { get; }
        public RelayCommand ClearPacketLogCommand { get; }
        public RelayCommand AutoCommand { get; }
        public RelayCommand RelCommand { get; }
        public RelayCommand SendModeCommand { get; }
        public RelayCommand ReplayPlayCommand { get; }
        public RelayCommand ReplayStopCommand { get; }
        public RelayCommand SetReplaySpeedCommand { get; }
        public RelayCommand ScreenshotCommand { get; }

        /// <summary>
        /// 默认数据根目录。设了环境变量 DM40_DATA_DIR 就用它（便于把录制/截图统一归到某个盘），
        /// 否则回落到系统「文档」。这样既不写死某台机器的路径，又允许按需重定向。
        /// </summary>
        private static string DefaultDataRoot
        {
            get
            {
                var custom = Environment.GetEnvironmentVariable("DM40_DATA_DIR");
                return !string.IsNullOrWhiteSpace(custom)
                    ? custom.Trim()
                    : Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
            }
        }

        private string _screenshotDir = System.IO.Path.Combine(DefaultDataRoot, "DM40MeterStudio");
        /// <summary>一键截图保存目录（默认 图片\DM40MeterStudio，可在设置修改）。</summary>
        public string ScreenshotDir { get => _screenshotDir; set => Set(ref _screenshotDir, value); }

        /// <summary>请求截图（code-behind 订阅并实际渲染窗口为 PNG）。MVVM 分离：VM 不碰 UI 元素。</summary>
        public event Action ScreenshotRequested;

        // ===================== 数据回放逻辑 =====================

        /// <summary>加载 CSV 进入回放模式（与实时互斥）。返回错误消息，null 表示成功。</summary>
        public string LoadReplay(string path)
        {
            CsvReplaySource src;
            try { src = CsvReplaySource.LoadFromLines(System.IO.File.ReadLines(path)); }
            catch (CsvFormatException ex) { return ex.Message; }
            catch (System.IO.IOException ex) { return "无法读取文件: " + ex.Message; }

            _replayRecords = src.Records.ToList();
            _replayWaveform.Clear();
            _replayIndex = 0;
            _replayPlaying = false;
            _replayTimer.Stop();
            IsReplayMode = true;
            ReplayProgress = 0;
            ReplaySpeedLabel = _replaySpeed;
            ReplayPlayLabel = "▶ 播放";
            ReplayStatusText = $"已加载 {_replayRecords.Count:N0} 条{(src.WasTruncated ? "（截断）" : "")} · 已暂停";
            ShowReplayRecord(0);
            RaiseReplayCanExecute();
            return null;
        }

        /// <summary>退出回放模式，回到实时。</summary>
        public void ExitReplay()
        {
            _replayTimer.Stop();
            _replayPlaying = false;
            _replayRecords = null;
            _replayWaveform.Clear();
            IsReplayMode = false;
            ReplayProgress = 0;
            ReplayStatusText = "未加载";
            ReplayPlayLabel = "▶ 播放";
            RaiseReplayCanExecute();
        }

        private void ReplayPlayPause()
        {
            if (!IsReplayLoaded) return;
            if (_replayPlaying) { PauseReplay(); }
            else
            {
                // 播到结尾后再按播放 → 从头
                if (_replayIndex >= _replayRecords.Count) { _replayIndex = 0; _replayWaveform.Clear(); }
                _replayPlaying = true;
                _replayWallStart = DateTime.UtcNow;
                _replayVirtualStartMs = VirtualMsAt(_replayIndex);
                ReplayPlayLabel = "⏸ 暂停";
                ReplayStatusText = $"回放中 {_replaySpeed:0.#}x · {_replayRecords.Count:N0} 条";
                _replayTimer.Start();
            }
            RaiseReplayCanExecute();
        }

        private void PauseReplay()
        {
            _replayPlaying = false;
            _replayTimer.Stop();
            ReplayPlayLabel = "▶ 播放";
            ReplayStatusText = $"已暂停 · 第 {_replayIndex}/{_replayRecords.Count} 条";
        }

        private void ReplayStop()
        {
            PauseReplay();
            _replayIndex = 0;
            _replayWaveform.Clear();
            ReplayProgress = 0;
            if (IsReplayLoaded) ShowReplayRecord(0);
            ReplayStatusText = $"已停止 · {_replayRecords.Count:N0} 条";
            RaiseReplayCanExecute();
        }

        private void SetReplaySpeed(double speed)
        {
            if (speed <= 0) return;
            // 切倍速时保持当前虚拟进度连续：先结算当前进度再重置基准
            if (_replayPlaying)
            {
                _replayVirtualStartMs = VirtualMsAt(_replayIndex);
                _replayWallStart = DateTime.UtcNow;
            }
            _replaySpeed = speed;
            ReplaySpeedLabel = speed;
            if (_replayPlaying) ReplayStatusText = $"回放中 {_replaySpeed:0.#}x · {_replayRecords.Count:N0} 条";
        }

        // 虚拟进度（毫秒）：记录 idx 相对首条记录的时间偏移
        private double VirtualMsAt(int idx)
        {
            if (_replayRecords == null || _replayRecords.Count == 0) return 0;
            idx = Math.Min(idx, _replayRecords.Count - 1);
            return (_replayRecords[idx].Timestamp - _replayRecords[0].Timestamp).TotalMilliseconds;
        }

        private void ReplayTick()
        {
            if (!_replayPlaying || _replayRecords == null) return;
            double elapsedMs = (DateTime.UtcNow - _replayWallStart).TotalMilliseconds * _replaySpeed;
            double virtualMs = _replayVirtualStartMs + elapsedMs;

            // 推进所有虚拟进度已到的记录
            while (_replayIndex < _replayRecords.Count && VirtualMsAt(_replayIndex) <= virtualMs)
            {
                EmitReplayRecord(_replayRecords[_replayIndex]);
                _replayIndex++;
            }

            if (_replayIndex > 0)
            {
                ReplayProgress = 100.0 * _replayIndex / _replayRecords.Count;
                ShowReplayRecord(_replayIndex - 1);
            }

            if (_replayIndex >= _replayRecords.Count) // 播完
            {
                PauseReplay();
                ReplayProgress = 100;
                ReplayStatusText = $"回放完成 · {_replayRecords.Count:N0} 条";
            }
        }

        // 把一条回放记录喂给回放波形（时间戳归一化到"距首条"相对秒，与实时波形同一 X 基准）
        private void EmitReplayRecord(CsvRecord rec)
        {
            _replayWaveform.Add(rec.Timestamp, rec.Value);
        }

        private void ShowReplayRecord(int idx)
        {
            if (_replayRecords == null || _replayRecords.Count == 0) return;
            idx = Math.Max(0, Math.Min(idx, _replayRecords.Count - 1));
            var r = _replayRecords[idx];
            ReplayReading = r.Value.ToString("0.####");
            ReplayUnit = string.IsNullOrEmpty(r.Unit) ? "" : r.Unit;
            ReplayTimeText = r.Timestamp.ToString("HH:mm:ss");
        }

        private void RaiseReplayCanExecute()
        {
            ReplayPlayCommand.RaiseCanExecuteChanged();
            ReplayStopCommand.RaiseCanExecuteChanged();
            SetReplaySpeedCommand.RaiseCanExecuteChanged();
        }

        private static string Fmt(double? v) => v.HasValue ? v.Value.ToString("0.####") : "--";

        private static double? ParseLimit(string s)
        {
            if (string.IsNullOrWhiteSpace(s)) return null;
            return double.TryParse(s, System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out var v) ? v : (double?)null;
        }

        private void UpdateAlarmEnabled()
        {
            _session.Alarm.IsEnabled = _session.Alarm.UpperLimit.HasValue || _session.Alarm.LowerLimit.HasValue;
            OnPropertyChanged(nameof(AlarmEnabledText));
        }

        // ===================== 动作 =====================

        private void StartScan()
        {
            Devices.Clear();
            _source.DeviceFound += OnDeviceFound;
            _source.StartScan();
            // 自动连接：给 BLE 广播留出发现窗口，1.2s 后若仍未连接则自动连第一个设备（可在设置关闭）
            if (AutoConnect)
            {
                var t = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(1200) };
                t.Tick += (s2, e2) =>
                {
                    t.Stop();
                    if (!IsConnected && Devices.Count > 0)
                    {
                        SelectedDevice = Devices[0];
                        Connect();
                    }
                };
                t.Start();
            }
        }

        private void OnDeviceFound(DiscoveredDevice d) =>
            _dispatcher.Invoke(() =>
            {
                // 按 MAC 去重：同一设备反复广播时只保留一条，更新信号强度（否则扫描列表堆出多条相同设备）
                var existing = Devices.FirstOrDefault(x => x.MacAddress == d.MacAddress);
                if (existing != null) Devices.Remove(existing);
                Devices.Add(d);
                if (SelectedDevice == null) SelectedDevice = d;
                // 设备列表更新后重判连接命令可用性（兜底：选中项虽由 setter 触发，但首个设备插入时确保按钮即时点亮）
                ConnectDisconnectCommand.RaiseCanExecuteChanged();
            });

        // 诊断（连接阶段 / 命令写出状态）→ 报文查看器以"诊断"行展示，给设备控制排查提供可见性。
        // 诊断来自 BLE 后台线程，需 marshal 到 UI 线程再写集合。
        private void OnDiagnostic(string msg) =>
            _dispatcher.BeginInvoke(new Action(() =>
            {
                PacketLog.Insert(0, new PacketEntry
                {
                    Time = DateTime.Now.ToString("HH:mm:ss.fff"),
                    Direction = "诊断",
                    Hex = msg,
                    CrcOk = !msg.Contains("控制命令写出") || msg.Contains("Success"),
                });
                if (PacketLog.Count > 5000) PacketLog.RemoveAt(PacketLog.Count - 1);
            }));

        private void Connect()
        {
            if (SelectedDevice == null) return;
            _source.Connect(SelectedDevice.MacAddress);
        }

        private void Disconnect() => _source.Disconnect();

        private void Send(byte[] cmd)
        {
            if (!IsConnected) return;
            _source.SendCommand(cmd);
            // 报文查看器记一条 TX（即时反馈命令已下发）
            var hex = BitConverter.ToString(cmd).Replace('-', ' ');
            _dispatcher.BeginInvoke(new Action(() =>
            {
                PacketLog.Insert(0, new PacketEntry
                {
                    Time = DateTime.Now.ToString("HH:mm:ss.fff"),
                    Direction = "TX",
                    Hex = hex,
                    CrcOk = true,
                });
                if (PacketLog.Count > 5000) PacketLog.RemoveAt(PacketLog.Count - 1);
            }));
        }

        private void SendMode(string mode)
        {
            switch (mode)
            {
                case "vdc": Send(ProtocolConstants.CmdVdc); break;
                case "vac": Send(ProtocolConstants.CmdVac); break;
                case "adc": Send(ProtocolConstants.CmdAdc); break;
                case "aac": Send(ProtocolConstants.CmdAac); break;
                case "res": Send(ProtocolConstants.CmdRes); break;
                case "cap": Send(ProtocolConstants.CmdCap); break;
                case "diode": Send(ProtocolConstants.CmdDiode); break;
                case "cont": Send(ProtocolConstants.CmdCont); break;
                case "hz": Send(ProtocolConstants.CmdHz); break;
                case "temp": Send(ProtocolConstants.CmdTemp); break;
            }
        }

        private void ToggleRecording()
        {
            if (IsRecording)
            {
                _session.Recorder?.Stop();
                IsRecording = false;
            }
            else
            {
                // 录制默认落在数据根目录（可用环境变量 DM40_DATA_DIR 重定向），文件带时间戳
                var path = System.IO.Path.Combine(
                    DefaultDataRoot,
                    "DM40_" + DateTime.Now.ToString("yyyyMMdd_HHmmss") + ".csv");
                _session.Recorder = new CsvRecorder(new FileCsvSink(path), SystemClock.Instance);
                _session.Recorder.Start();
                _recStartTime = DateTime.Now;
                IsRecording = true;
            }
        }

        private void TickRecStatus()
        {
            if (!IsRecording) return;
            var el = DateTime.Now - _recStartTime;
            RecStatusText = $"REC {el:hh\\:mm\\:ss} · {_session.Waveform.Count:N0} 样本";
        }

        /// <summary>导出当前实时波形缓冲为 CSV（UTF-8 BOM，列序同录制格式）。返回写入条数，0=无数据。</summary>
        public int ExportCsv(string path)
        {
            var pts = _session.Waveform.Points.ToList();
            if (pts.Count == 0) return 0;
            var unit = _latest?.DisplayUnit ?? "";
            var mode = _latest?.Kind ?? "";
            var range = _latest?.Range ?? "";
            var lines = new System.Collections.Generic.List<string> { CsvSerializer.Header };
            foreach (var p in pts)
            {
                lines.Add(CsvSerializer.Serialize(new CsvRecord
                {
                    Timestamp = p.Timestamp.ToLocalTime(),
                    Value = p.Value,
                    Unit = unit,
                    Mode = mode,
                    Range = range,
                }));
            }
            System.IO.File.WriteAllText(path,
                string.Join(Environment.NewLine, lines) + Environment.NewLine,
                new System.Text.UTF8Encoding(true)); // 带 BOM，Excel 直接打开不乱码
            return pts.Count;
        }

        // ===================== 事件（后台线程 → marshal + 节流） =====================

        /// <summary>
        /// 档位切换回调：Core 侧已把统计和波形清零，这里负责清掉 UI 上的旧读数残留，
        /// 避免切档瞬间仍显示上一档位的数值、单位和小数位（例如刚切到电压档还挂着 30.4 °C）。
        /// </summary>
        private void OnModeChanged(string newKind, string oldKind)
        {
            _dispatcher.BeginInvoke(new Action(() =>
            {
                // 读数区立刻置空，等新档位首帧到来再填
                MainReading = "---";
                Unit = "";
                Range = "";
                Secondary1 = "--";
                Secondary2 = "--";

                // 丢弃尚未渲染的旧帧快照，防止一次 dirty 刷新把旧值又贴回来
                _latest = null;
                _dirty = false;

                // 统计面板已清零，主动通知刷新（这些是计算属性，不会自动变）
                _statStart = null;
                StatSummary = "样本 0 · 时长 00:00:00";
                OnPropertyChanged(nameof(StatMin));
                OnPropertyChanged(nameof(StatMax));
                OnPropertyChanged(nameof(StatAvg));
                OnPropertyChanged(nameof(StatDelta));
                OnPropertyChanged(nameof(StatCount));
            }));
        }

        private void OnReading(Measurement m)
        {
            _latest = m;
            _dirty = true;
        }

        private void OnRawFrame(byte[] raw, Measurement m)
        {
            // 帧率统计
            _frameCountWindow++;
            _lastFrameTime = DateTime.UtcNow;

            // 报文查看器（限流：最多 5000 条循环）
            var entry = new PacketEntry
            {
                Time = DateTime.Now.ToString("HH:mm:ss.fff"),
                Direction = "RX",
                Hex = m.Raw,
                CrcOk = m.CrcOk,
            };
            _dispatcher.BeginInvoke(new Action(() =>
            {
                if (!entry.CrcOk) PacketFailCount++;
                if (!PacketPaused)   // 暂停时冻结查看器：不插入新帧（画面停住，便于阅读）
                {
                    PacketLog.Insert(0, entry);
                    if (PacketLog.Count > 5000) PacketLog.RemoveAt(PacketLog.Count - 1);
                }
                RaisePacketStatsChanged();
            }));
        }

        private void FlushLatest()
        {
            if (!_dirty || _latest == null) return;
            _dirty = false;
            var m = _latest;

            MainReading = m.ValueText;
            Unit = m.DisplayUnit;
            Range = m.Range;
            ModeLabel = ModeLabelOf(m.Kind);
            // 副显：有协议副显（VAC 占空比/频率、DIODE 电阻、FREQ 占空比、TEMP 华氏/内部）则显示；
            // 无副显的单槽位模式（VDC/ADC/RES/CAP/CONT）副显位置复用为采样统计 + 高精度读数，
            // 避免 VDC 等常用档副显永远空白（ModeSlotMap 只有 M1 → SecondaryText/ThirdText 恒空）。
            Secondary1 = !string.IsNullOrEmpty(m.SecondaryText)
                ? $"{m.SecondaryText} {m.SecondaryUnit}".Trim()
                : $"采样 {StatCount:N0}";
            Secondary2 = !string.IsNullOrEmpty(m.ThirdText)
                ? $"{m.ThirdText} {m.ThirdUnit}".Trim()
                : (m.DisplayValue.HasValue ? $"{m.DisplayValue.Value:0.####} {m.DisplayUnit}".Trim() : "--");

            // 状态徽章
            BatteryBadge = m.Charging ? $"⚡充电 {m.BatteryLevel}/5" : $"🔋 {m.BatteryLevel}/5";
            HoldBadgeVis = m.Hold ? Visibility.Visible : Visibility.Collapsed;
            LockBadgeVis = m.Locked ? Visibility.Visible : Visibility.Collapsed;
            // REL 是设备本地相对值态，帧里无标志位回读，跟随本地 RelActive（点 REL 切换）
            RelBadgeVis = RelActive ? Visibility.Visible : Visibility.Collapsed;
            // 按钮态与设备实际态双向对齐：设备回读的 Hold 是权威源，直接跟随（含设备侧手动按 HOLD 键的场景）。
            // 修复"点关闭保持后按钮仍亮"——原来只 set true 从不回 false。
            if (m.Hold != HoldActive) HoldActive = m.Hold;

            // 统计随每次刷新同步
            if (_statStart == null && m.NormValue.HasValue) _statStart = DateTime.Now;
            OnPropertyChanged(nameof(StatMin));
            OnPropertyChanged(nameof(StatMax));
            OnPropertyChanged(nameof(StatAvg));
            OnPropertyChanged(nameof(StatDelta));
            if (_statStart.HasValue)
            {
                var el = DateTime.Now - _statStart.Value;
                StatSummary = $"样本 {StatCount:N0} · 时长 {el:hh\\:mm\\:ss}";
            }

            // 帧率（每秒窗口）
            var span = (DateTime.UtcNow - _frameWindowStart).TotalSeconds;
            if (span >= 1.0)
            {
                FrameRateText = $"帧率 {_frameCountWindow / span:0.0} Hz";
                _frameCountWindow = 0;
                _frameWindowStart = DateTime.UtcNow;
            }
        }

        private static string ModeLabelOf(string kind) => kind switch
        {
            "VDC" => "直流电压 · DCV",
            "VAC" => "交流电压 · ACV",
            "ADC" => "直流电流 · DCA",
            "AAC" => "交流电流 · ACA",
            "RES" => "电阻 · Ω",
            "CAP" => "电容 · CAP",
            "FREQ" => "频率 · HZ",
            "TEMP" => "温度 · TEMP",
            "DIODE" => "二极管 · DIODE",
            "CONT" => "通断 · CONT",
            _ => kind,
        };

        // 报警事件值的 SI 基本单位（NormValue 是 SI 值，不能用 DisplayUnit——600mV 档 NormValue 仍是 V）
        private static string SiUnit(string kind)
        {
            switch (kind)
            {
                case "VDC": case "VAC": case "VDC+AC": return "V";
                case "ADC": case "AAC": case "ADC+AC": return "A";
                case "RES": case "RES_ONLINE": case "CONT": return "Ω";
                case "CAP": return "F";
                case "FREQ": return "Hz";
                case "TEMP": return "℃";
                default: return "";
            }
        }

        private void OnAlarm(AlarmEvent e) =>
            _dispatcher.Invoke(() =>
            {
                AlarmLog.Insert(0, $"{e.Timestamp:HH:mm:ss} {(e.Direction == AlarmDirection.High ? "HIGH" : "LOW")} {e.Value:0.####}{SiUnit(_latest?.Kind)}");
                if (AlarmLog.Count > 100) AlarmLog.RemoveAt(AlarmLog.Count - 1);
                // 声音/弹窗提示（View 订阅播放 SystemSounds + 右下角 Toast，FR-007）
                AlarmNotify?.Invoke(e);
            });

        private void OnStateChanged(ConnectionState s) =>
            _dispatcher.Invoke(() =>
            {
                State = s;
                StatusLine = s switch
                {
                    ConnectionState.Disconnected => "未连接",
                    ConnectionState.Scanning => "扫描中…",
                    ConnectionState.DeviceList => "已发现设备",
                    ConnectionState.Connecting => "连接中…",
                    ConnectionState.Connected => "已连接",
                    ConnectionState.Measuring => "测量中",
                    ConnectionState.Reconnecting => "重连中…",
                    _ => s.ToString(),
                };
            });

        public void Dispose()
        {
            _uiTimer.Stop();
            _recTimer.Stop();
            _session.Recorder?.Dispose();
            _source.Disconnect();
        }
    }
}
