using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;

namespace VPet.Plugin.VoiceprintRecognition
{
    /// <summary>
    /// 声纹注册独立对话框
    /// 引导用户输入唤醒词并录制语音样本
    /// 基础模式: 3 次正常录音
    /// 增强模式: 远距离/近距离/大声/小声 各 3 次 = 12 次
    /// </summary>
    public class VoiceprintRegistrationDialog : Window
    {
        private readonly VoiceprintPlugin _plugin;
        private const int SamplesPerPhase = 3;

        // 增强模式的阶段定义
        private static readonly (string Label, string Condition, string Prompt)[] EnhancedPhases = new[]
        {
            ("远距离", "far", "请在离麦克风较远的位置朗读唤醒词"),
            ("近距离", "close", "请在离麦克风较近的位置朗读唤醒词"),
            ("大声", "loud", "请大声朗读唤醒词"),
            ("小声", "quiet", "请小声朗读唤醒词"),
        };

        // UI 元素
        private readonly TextBox _tbWakeWord;
        private readonly CheckBox _cbEnhanced;
        private readonly TextBlock _tbPhaseLabel;
        private readonly TextBlock _tbStatus;
        private readonly TextBlock _tbProgress;
        private readonly Button _btnRecord;
        private readonly Button _btnConfirm;
        private readonly Button _btnCancel;
        private readonly StackPanel _sampleIndicators;
        private readonly ProgressBar _progressBar;

        // 录音状态
        private readonly List<byte[]> _audioSamples = new List<byte[]>();
        private readonly List<string> _sampleConditions = new List<string>();
        private bool _isRecording = false;
        private DispatcherTimer _recordingTimer;
        private DateTime _recordingStartTime;

        // 进度追踪
        private int _currentPhase = 0;        // 当前阶段 (增强模式下 0~3)
        private int _currentSampleInPhase = 0; // 当前阶段内的录音序号 (0~2)

        /// <summary>
        /// 注册是否成功
        /// </summary>
        public bool RegistrationSuccess { get; private set; } = false;

        private bool IsEnhanced => _cbEnhanced.IsChecked == true;
        private int TotalPhases => IsEnhanced ? EnhancedPhases.Length : 1;
        private int TotalSamples => TotalPhases * SamplesPerPhase;
        private int CompletedSamples => _audioSamples.Count;

        public VoiceprintRegistrationDialog(VoiceprintPlugin plugin)
        {
            _plugin = plugin;

            Title = "注册声纹";
            Width = 500;
            Height = 480;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
            ResizeMode = ResizeMode.NoResize;
            ShowInTaskbar = false;
            Background = new SolidColorBrush(Color.FromRgb(250, 250, 250));

            var mainPanel = new StackPanel { Margin = new Thickness(24) };

            // 标题
            mainPanel.Children.Add(new TextBlock
            {
                Text = "声纹注册",
                FontSize = 20,
                FontWeight = FontWeights.Bold,
                Margin = new Thickness(0, 0, 0, 12)
            });

            // 唤醒词
            mainPanel.Children.Add(new TextBlock { Text = "唤醒词 (录音时请朗读此内容):", Margin = new Thickness(0, 0, 0, 4) });
            _tbWakeWord = new TextBox
            {
                Margin = new Thickness(0, 0, 0, 4),
                Text = "你好小宠",
                FontSize = 16
            };
            mainPanel.Children.Add(_tbWakeWord);

            // 增强识别精度选项
            _cbEnhanced = new CheckBox
            {
                Content = "增强识别精度（录入远距离/近距离/大声/小声各3次，共12次）",
                Margin = new Thickness(0, 4, 0, 4),
                FontSize = 12
            };
            _cbEnhanced.Checked += (s, e) => UpdatePhaseUI();
            _cbEnhanced.Unchecked += (s, e) => UpdatePhaseUI();
            mainPanel.Children.Add(_cbEnhanced);

            mainPanel.Children.Add(new TextBlock
            {
                Text = "每次录音请清晰朗读唤醒词",
                Foreground = Brushes.Gray,
                FontSize = 12,
                Margin = new Thickness(0, 0, 0, 12)
            });

            // 阶段标签（增强模式显示当前阶段）
            _tbPhaseLabel = new TextBlock
            {
                Text = "",
                FontSize = 14,
                FontWeight = FontWeights.SemiBold,
                HorizontalAlignment = HorizontalAlignment.Center,
                Margin = new Thickness(0, 0, 0, 8),
                Visibility = Visibility.Collapsed
            };
            mainPanel.Children.Add(_tbPhaseLabel);

            // 当前阶段的录音进度指示（3 个圆点）
            _sampleIndicators = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Center,
                Margin = new Thickness(0, 0, 0, 12)
            };
            for (int i = 0; i < SamplesPerPhase; i++)
            {
                var border = new Border
                {
                    Width = 32,
                    Height = 32,
                    CornerRadius = new CornerRadius(16),
                    Background = Brushes.LightGray,
                    Margin = new Thickness(8, 0, 8, 0),
                    Child = new TextBlock
                    {
                        Text = (i + 1).ToString(),
                        HorizontalAlignment = HorizontalAlignment.Center,
                        VerticalAlignment = VerticalAlignment.Center,
                        FontWeight = FontWeights.Bold,
                        Foreground = Brushes.White
                    }
                };
                _sampleIndicators.Children.Add(border);
            }
            mainPanel.Children.Add(_sampleIndicators);

            // 进度条（录音时长）
            _progressBar = new ProgressBar
            {
                Height = 6,
                Minimum = 0,
                Maximum = 100,
                Value = 0,
                Margin = new Thickness(0, 0, 0, 8),
                Visibility = Visibility.Collapsed
            };
            mainPanel.Children.Add(_progressBar);

            // 状态文本
            _tbStatus = new TextBlock
            {
                Text = "请填写唤醒词，然后点击\"开始录音\"",
                HorizontalAlignment = HorizontalAlignment.Center,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 0, 0, 4)
            };
            mainPanel.Children.Add(_tbStatus);

            // 进度文本
            _tbProgress = new TextBlock
            {
                Text = $"已完成: 0 / {SamplesPerPhase}",
                HorizontalAlignment = HorizontalAlignment.Center,
                Foreground = Brushes.Gray,
                FontSize = 12,
                Margin = new Thickness(0, 0, 0, 16)
            };
            mainPanel.Children.Add(_tbProgress);

            // 按钮区
            var buttonPanel = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Center,
                Margin = new Thickness(0, 8, 0, 0)
            };

            _btnRecord = new Button
            {
                Content = "开始录音 (1/3)",
                Width = 160,
                Height = 36,
                FontSize = 14,
                Margin = new Thickness(0, 0, 12, 0),
                Background = new SolidColorBrush(Color.FromRgb(66, 133, 244)),
                Foreground = Brushes.White,
                BorderThickness = new Thickness(0)
            };
            _btnRecord.Click += BtnRecord_Click;
            buttonPanel.Children.Add(_btnRecord);

            _btnConfirm = new Button
            {
                Content = "完成注册",
                Width = 120,
                Height = 36,
                FontSize = 14,
                Margin = new Thickness(0, 0, 12, 0),
                IsEnabled = false
            };
            _btnConfirm.Click += BtnConfirm_Click;
            buttonPanel.Children.Add(_btnConfirm);

            _btnCancel = new Button
            {
                Content = "取消",
                Width = 80,
                Height = 36,
                FontSize = 14
            };
            _btnCancel.Click += (s, ev) => { DialogResult = false; Close(); };
            buttonPanel.Children.Add(_btnCancel);

            mainPanel.Children.Add(buttonPanel);

            Content = mainPanel;

            // 录音计时器
            _recordingTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(100) };
            _recordingTimer.Tick += RecordingTimer_Tick;

            UpdatePhaseUI();
        }

        /// <summary>
        /// 更新阶段 UI（增强/基础切换时、阶段推进时）
        /// </summary>
        private void UpdatePhaseUI()
        {
            if (IsEnhanced && _currentPhase < EnhancedPhases.Length)
            {
                var phase = EnhancedPhases[_currentPhase];
                _tbPhaseLabel.Text = $"阶段 {_currentPhase + 1}/{EnhancedPhases.Length}: {phase.Label}";
                _tbPhaseLabel.Visibility = Visibility.Visible;
            }
            else if (IsEnhanced)
            {
                _tbPhaseLabel.Text = "所有阶段完成";
                _tbPhaseLabel.Visibility = Visibility.Visible;
            }
            else
            {
                _tbPhaseLabel.Visibility = Visibility.Collapsed;
            }

            // 重置当前阶段的圆点指示器
            for (int i = 0; i < SamplesPerPhase; i++)
            {
                var indicator = (Border)_sampleIndicators.Children[i];
                if (i < _currentSampleInPhase)
                    indicator.Background = new SolidColorBrush(Color.FromRgb(52, 168, 83)); // 已完成
                else
                    indicator.Background = Brushes.LightGray;
            }

            _tbProgress.Text = $"已完成: {CompletedSamples} / {TotalSamples}";

            if (!_isRecording)
                ResetRecordButton();
        }

        private string GetCurrentPrompt()
        {
            if (IsEnhanced && _currentPhase < EnhancedPhases.Length)
                return EnhancedPhases[_currentPhase].Prompt;
            return "请正常朗读唤醒词";
        }

        private string GetCurrentCondition()
        {
            if (IsEnhanced && _currentPhase < EnhancedPhases.Length)
                return EnhancedPhases[_currentPhase].Condition;
            return "normal";
        }

        private void BtnRecord_Click(object sender, RoutedEventArgs e)
        {
            if (_isRecording)
            {
                StopCurrentRecording();
                return;
            }

            if (string.IsNullOrWhiteSpace(_tbWakeWord.Text))
            {
                _tbStatus.Text = "请输入唤醒词";
                _tbStatus.Foreground = Brushes.Red;
                _tbWakeWord.Focus();
                return;
            }

            StartCurrentRecording();
        }

        private void StartCurrentRecording()
        {
            try
            {
                _plugin.AudioCapture?.StartCapture();
                _isRecording = true;
                _recordingStartTime = DateTime.Now;

                _tbWakeWord.IsEnabled = false;
                _cbEnhanced.IsEnabled = false;
                _btnRecord.Content = "停止录音";
                _btnRecord.Background = new SolidColorBrush(Color.FromRgb(234, 67, 53));
                _btnConfirm.IsEnabled = false;
                _progressBar.Visibility = Visibility.Visible;
                _progressBar.Value = 0;

                // 高亮当前录音指示器
                if (_currentSampleInPhase < SamplesPerPhase)
                {
                    var indicator = (Border)_sampleIndicators.Children[_currentSampleInPhase];
                    indicator.Background = new SolidColorBrush(Color.FromRgb(234, 67, 53));
                }

                string prompt = GetCurrentPrompt();
                _tbStatus.Text = $"{prompt}: \"{_tbWakeWord.Text}\"";
                _tbStatus.Foreground = new SolidColorBrush(Color.FromRgb(234, 67, 53));
                _tbStatus.FontWeight = FontWeights.Bold;

                _recordingTimer.Start();

                int globalIndex = CompletedSamples + 1;
                _plugin.LogMessage($"声纹注册: 开始第 {globalIndex}/{TotalSamples} 次录音 ({GetCurrentCondition()})");
            }
            catch (Exception ex)
            {
                _tbStatus.Text = $"录音启动失败: {ex.Message}";
                _tbStatus.Foreground = Brushes.Red;
                _isRecording = false;
            }
        }

        private void StopCurrentRecording()
        {
            _recordingTimer.Stop();
            _isRecording = false;

            try
            {
                var audioData = _plugin.AudioCapture?.StopCapture();

                if (audioData == null || audioData.Length < 16000 * 2)
                {
                    _plugin.LogMessage($"声纹注册: 录音数据不足");
                    _tbStatus.Text = "录音太短，请重新录制这一次";
                    _tbStatus.Foreground = Brushes.Red;
                    _tbStatus.FontWeight = FontWeights.Normal;

                    if (_currentSampleInPhase < SamplesPerPhase)
                    {
                        var indicator = (Border)_sampleIndicators.Children[_currentSampleInPhase];
                        indicator.Background = Brushes.LightGray;
                    }

                    ResetRecordButton();
                    return;
                }

                _audioSamples.Add(audioData);
                _sampleConditions.Add(GetCurrentCondition());
                _currentSampleInPhase++;

                float duration = audioData.Length / (float)(16000 * 2);
                _plugin.LogMessage($"声纹注册: 第 {CompletedSamples}/{TotalSamples} 次录音完成, {duration:F1}s ({GetCurrentCondition()})");

                // 标记已完成的指示器
                var completedIndicator = (Border)_sampleIndicators.Children[_currentSampleInPhase - 1];
                completedIndicator.Background = new SolidColorBrush(Color.FromRgb(52, 168, 83));

                // 检查是否完成当前阶段
                if (_currentSampleInPhase >= SamplesPerPhase)
                {
                    // 尝试进入下一阶段
                    _currentPhase++;
                    _currentSampleInPhase = 0;

                    if (_currentPhase >= TotalPhases)
                    {
                        // 所有阶段完成
                        _tbStatus.Text = "录音采集完成！点击\"完成注册\"进行声纹提取";
                        _tbStatus.Foreground = new SolidColorBrush(Color.FromRgb(52, 168, 83));
                        _tbStatus.FontWeight = FontWeights.Bold;
                        _btnRecord.IsEnabled = false;
                        _btnRecord.Content = "录音完成";
                        _btnConfirm.IsEnabled = true;
                    }
                    else
                    {
                        // 进入下一阶段
                        _tbStatus.Text = $"阶段 {_currentPhase}/{TotalPhases} 完成，继续下一阶段";
                        _tbStatus.Foreground = new SolidColorBrush(Color.FromRgb(52, 168, 83));
                        _tbStatus.FontWeight = FontWeights.Normal;
                        UpdatePhaseUI();
                    }
                }
                else
                {
                    _tbStatus.Text = $"第 {CompletedSamples} 次录音成功，请继续";
                    _tbStatus.Foreground = new SolidColorBrush(Color.FromRgb(52, 168, 83));
                    _tbStatus.FontWeight = FontWeights.Normal;
                    ResetRecordButton();
                }

                _tbProgress.Text = $"已完成: {CompletedSamples} / {TotalSamples}";
            }
            catch (Exception ex)
            {
                _tbStatus.Text = $"录音处理失败: {ex.Message}";
                _tbStatus.Foreground = Brushes.Red;
                _tbStatus.FontWeight = FontWeights.Normal;
                ResetRecordButton();
            }

            _progressBar.Visibility = Visibility.Collapsed;
        }

        private void ResetRecordButton()
        {
            int nextGlobal = CompletedSamples + 1;
            _btnRecord.Content = $"开始录音 ({nextGlobal}/{TotalSamples})";
            _btnRecord.Background = new SolidColorBrush(Color.FromRgb(66, 133, 244));
            _btnRecord.IsEnabled = true;
        }

        private void RecordingTimer_Tick(object sender, EventArgs e)
        {
            var elapsed = (DateTime.Now - _recordingStartTime).TotalSeconds;
            double maxDuration = _plugin.Settings.MaxRecordingDuration;
            _progressBar.Value = Math.Min(100, elapsed / maxDuration * 100);

            if (elapsed >= maxDuration)
            {
                StopCurrentRecording();
            }
        }

        private async void BtnConfirm_Click(object sender, RoutedEventArgs e)
        {
            if (CompletedSamples < TotalSamples)
            {
                _tbStatus.Text = "录音样本不足";
                _tbStatus.Foreground = Brushes.Red;
                return;
            }

            _btnConfirm.IsEnabled = false;
            _btnRecord.IsEnabled = false;
            _btnCancel.IsEnabled = false;
            _tbStatus.Text = "正在提取声纹特征和唤醒词能量包络，请稍候...";
            _tbStatus.Foreground = Brushes.Black;
            _tbStatus.FontWeight = FontWeights.Normal;

            string wakeWord = _tbWakeWord.Text.Trim();
            string userId = Guid.NewGuid().ToString("N").Substring(0, 8);

            try
            {
                // 提取所有录音的能量包络
                var envelopes = new List<WakeWordEnvelope>();
                for (int i = 0; i < _audioSamples.Count; i++)
                {
                    _tbStatus.Text = $"提取能量包络 {i + 1}/{_audioSamples.Count}...";
                    string condition = _sampleConditions[i];
                    var envelope = await Task.Run(() =>
                        WakeWordDetector.ExtractEnvelope(_audioSamples[i], _plugin.Settings.SampleRate, condition));
                    envelopes.Add(envelope);
                    _plugin.LogMessage($"特征 {i + 1}: {envelope.NumFrames} 帧 × {envelope.NumBands} 维, 条件={condition}, 时长={envelope.Duration:F1}s");
                }

                _tbStatus.Text = "正在注册声纹...";

                bool success = await Task.Run(() =>
                    _plugin.Recognizer.RegisterVoiceprintMultiSample(userId, wakeWord, _audioSamples, envelopes));

                if (success)
                {
                    _plugin.LogMessage($"声纹注册成功: {wakeWord} (ID: {userId}, {(IsEnhanced ? "增强模式" : "基础模式")})");
                    _tbStatus.Text = $"注册成功！唤醒词: {wakeWord}";
                    _tbStatus.Foreground = new SolidColorBrush(Color.FromRgb(52, 168, 83));
                    _tbStatus.FontWeight = FontWeights.Bold;
                    RegistrationSuccess = true;

                    await Task.Delay(1000);
                    DialogResult = true;
                    Close();
                }
                else
                {
                    _tbStatus.Text = "注册失败，请重试";
                    _tbStatus.Foreground = Brushes.Red;
                    _btnCancel.IsEnabled = true;
                    ResetAll();
                }
            }
            catch (Exception ex)
            {
                _plugin.LogMessage($"声纹注册异常: {ex.Message}");
                _tbStatus.Text = $"注册失败: {ex.Message}";
                _tbStatus.Foreground = Brushes.Red;
                _btnCancel.IsEnabled = true;
                ResetAll();
            }
        }

        private void ResetAll()
        {
            _audioSamples.Clear();
            _sampleConditions.Clear();
            _currentPhase = 0;
            _currentSampleInPhase = 0;
            _tbWakeWord.IsEnabled = true;
            _cbEnhanced.IsEnabled = true;
            _btnConfirm.IsEnabled = false;

            for (int i = 0; i < SamplesPerPhase; i++)
            {
                var indicator = (Border)_sampleIndicators.Children[i];
                indicator.Background = Brushes.LightGray;
            }

            UpdatePhaseUI();
        }

        protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
        {
            base.OnClosing(e);
            _recordingTimer?.Stop();

            if (_isRecording)
            {
                _isRecording = false;
                try { _plugin.AudioCapture?.StopCapture(); } catch { }
            }
        }
    }
}
