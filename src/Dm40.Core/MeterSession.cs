using System;
using System.Collections.Generic;
using Dm40.Protocol;

namespace Dm40.Core
{
    /// <summary>
    /// 测量会话协调器：把 IMeterDataSource 的帧流聚合到 Core 各组件
    /// （解析 → 统计 → 报警 → 波形缓冲 → 录制），是 §5.1 主链路的"核心层"实现。
    /// 与 UI 解耦：通过事件暴露增量，订阅方（App）负责 marshal 到 UI 线程并节流。
    /// 可注入 IClock/ICsvSink，故可完全离线单测。
    /// </summary>
    public sealed class MeterSession
    {
        private readonly IMeterDataSource _source;
        private readonly IClock _clock;

        public MeterSession(IMeterDataSource source, IClock clock)
        {
            _source = source ?? throw new ArgumentNullException(nameof(source));
            _clock = clock ?? throw new ArgumentNullException(nameof(clock));

            Statistics = new MeasurementStatistics();
            Alarm = new ThresholdAlarm(clock);
            Waveform = new WaveformBuffer();

            _source.FrameReceived += OnFrame;
            Alarm.Triggered += e => AlarmTriggered?.Invoke(e);
        }

        /// <summary>统计面板。</summary>
        public MeasurementStatistics Statistics { get; }

        /// <summary>阈值报警。</summary>
        public ThresholdAlarm Alarm { get; }

        /// <summary>波形缓冲（CSV 导出的数据源，FR-005/§5.4）。</summary>
        public WaveformBuffer Waveform { get; }

        /// <summary>录制器（由 App 在启动录制时创建并赋值）。</summary>
        public CsvRecorder Recorder { get; set; }

        /// <summary>当前连接状态（代理自数据源）。</summary>
        public ConnectionState State => _source.State;

        /// <summary>解析出一条有效读数时触发（UI 刷新读数/波形）。</summary>
        public event Action<Measurement> ReadingAvailable;

        /// <summary>收到任意原始帧（含无效帧，供报文查看器留痕，FR-011）。</summary>
        public event Action<byte[], Measurement> RawFrame;

        /// <summary>报警触发（声音/弹窗/红标）。</summary>
        public event Action<AlarmEvent> AlarmTriggered;

        /// <summary>
        /// 测量模式（档位）发生变化。参数：(新模式, 旧模式)。
        /// 覆盖三种来源：GUI 点档位按钮、CLI send、用户在设备上手动切档 —— 因为判定依据是
        /// 设备回帧里的 Kind 而非本地下发的命令，所以不会漏。
        /// 触发时 Statistics 与 Waveform 已按新档位清零，订阅方负责清掉 UI 上的旧显示残留。
        /// </summary>
        public event Action<string, string> ModeChanged;

        /// <summary>上一帧的测量类型，用于检测档位切换。</summary>
        private string _lastKind;

        /// <summary>收到原始帧：CRC/解析 → 有效则统计/报警/波形/录制，无效仅留痕。</summary>
        private void OnFrame(byte[] data)
        {
            var m = MeasurementParser.Parse(data, _source.DeviceCounts);
            RawFrame?.Invoke(data, m);

            // §5.2 规则1：无效帧不参与显示/统计/报警/录制
            if (!m.CrcOk || !m.IsValid)
                return;

            // 档位变化 → 先清零再累计。
            // 不同档位是不同量纲（温度 30°C vs 电压 3.3V vs 电容 nF），混在一起会让
            // 最小值/最大值/平均值全部失去意义；波形也会被旧的 Y 轴量程压成一条平线。
            // 判定放在这里而不是按钮回调里，是为了连「用户在设备上手动切档」也能兜住。
            if (_lastKind != null && m.Kind != _lastKind)
            {
                Statistics.Reset();
                Waveform.Clear();
                ModeChanged?.Invoke(m.Kind, _lastKind);
            }
            _lastKind = m.Kind;

            // 过载/无效主值不参与统计与报警，但仍算一条波形断点由 UI 处理
            if (m.NormValue.HasValue)
            {
                var now = _clock.UtcNow;
                Statistics.Add(m.NormValue.Value);
                Alarm.Feed(m.NormValue.Value);
                Waveform.Add(now, m.NormValue.Value);

                Recorder?.Write(new CsvRecord
                {
                    Timestamp = now.ToLocalTime(),
                    Value = m.NormValue.Value,
                    Unit = m.DisplayUnit,
                    Mode = m.Kind,
                    Range = m.Range,
                });
            }

            ReadingAvailable?.Invoke(m);
        }

        /// <summary>录制 tick（App 每秒调用以 flush）。</summary>
        public void TickRecorder() => Recorder?.Tick();
    }
}
