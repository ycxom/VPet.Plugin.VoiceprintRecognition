using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Panuon.WPF.UI;

namespace VPet.Plugin.VoiceprintRecognition
{
    /// <summary>
    /// winSetting.xaml 的交互逻辑
    /// </summary>
    public partial class winSetting : WindowX
    {
        private readonly VoiceprintPlugin _plugin;
        private VoiceprintSettings _settings;
        private VoiceprintSettings _originalSettings;
        private List<AudioDeviceInfo> _audioDevices;
        private DispatcherTimer _logUpdateTimer;

        public winSetting(VoiceprintPlugin plugin)
        {
            InitializeComponent();

            _plugin = plugin;
            _settings = plugin.Settings.Clone();
            _originalSettings = plugin.Settings.Clone();

            // 加载设置到 UI
            LoadSettings();

            // 更新模型路径显示
            UpdateModelPath();

            // 更新模型状态信息
            UpdateModelInfo();

            // 启动日志更新定时器
            _logUpdateTimer = new DispatcherTimer();
            _logUpdateTimer.Interval = TimeSpan.FromSeconds(1);
            _logUpdateTimer.Tick += LogUpdateTimer_Tick;
            _logUpdateTimer.Start();
        }

        private void LogUpdateTimer_Tick(object sender, EventArgs e)
        {
            try
            {
                if (TextBoxLog == null) return;

                var logs = _plugin.GetLogMessages();
                if (logs.Count > 0)
                {
                    TextBoxLog.Text = string.Join(Environment.NewLine, logs);
                    LogScrollViewer?.ScrollToEnd();
                }
                else if (TextBoxLog.Text == "日志将显示在这里...")
                {
                    TextBoxLog.Text = "暂无日志记录。\n\n提示：\n- 插件运行时会自动记录日志\n- 操作设置、加载模型等都会产生日志";
                }
            }
            catch { }
        }

        /// <summary>
        /// 加载设置到 UI
        /// </summary>
        private void LoadSettings()
        {
            // 基本设置
            CheckEnableVoiceInput.IsChecked = _settings.EnableVoiceInput;
            CheckEnableVoiceprint.IsChecked = _settings.EnableVoiceprintVerification;
            CheckRequireMatch.IsChecked = _settings.RequireVoiceprintMatch;

            // 声纹识别设置
            SliderThreshold.Value = _settings.VoiceprintThreshold;
            TextThreshold.Text = _settings.VoiceprintThreshold.ToString("F2");

            // 加载已注册声纹列表
            LoadVoiceprintList();

            // 模型设置
            TextVoiceprintModel.Text = _settings.VoiceprintModelFile;
            TextWhisperModel.Text = _settings.WhisperModelFile;

            // 语言选择
            foreach (ComboBoxItem item in ComboLanguage.Items)
            {
                if (item.Tag?.ToString() == _settings.Language)
                {
                    ComboLanguage.SelectedItem = item;
                    break;
                }
            }
            if (ComboLanguage.SelectedItem == null && ComboLanguage.Items.Count > 0)
                ComboLanguage.SelectedIndex = 0;

            // 最大解码长度
            SliderMaxDecoding.Value = _settings.MaxDecodingLength;

            // 音频设置
            LoadAudioDevices();

            // 采样率选择
            foreach (ComboBoxItem item in ComboSampleRate.Items)
            {
                if (item.Tag?.ToString() == _settings.SampleRate.ToString())
                {
                    ComboSampleRate.SelectedItem = item;
                    break;
                }
            }
            if (ComboSampleRate.SelectedItem == null && ComboSampleRate.Items.Count > 0)
                ComboSampleRate.SelectedIndex = 0;

            // 录音参数
            SliderMinDuration.Value = _settings.MinRecordingDuration;
            SliderMaxDuration.Value = _settings.MaxRecordingDuration;
            SliderSilenceTimeout.Value = _settings.SilenceTimeout;

            // 性能设置
            CheckUseGPU.IsChecked = _settings.UseGPU;
            SliderThreads.Value = _settings.NumThreads;

            // 调试设置
            CheckDebugMode.IsChecked = _settings.DebugMode;
            CheckSaveRecordings.IsChecked = _settings.SaveRecordings;

            // 更新模型文件状态
            UpdateModelFileStatus();
        }

        /// <summary>
        /// 更新模型路径显示
        /// </summary>
        private void UpdateModelPath()
        {
            if (TextBlockModelPath != null && _plugin.ModelsPath != null)
            {
                TextBlockModelPath.Text = _plugin.ModelsPath;
            }
        }

        /// <summary>
        /// 更新模型文件状态显示
        /// </summary>
        private void UpdateModelFileStatus()
        {
            if (_plugin.ModelsPath == null) return;

            // 声纹模型状态
            string vpPath = Path.Combine(_plugin.ModelsPath, _settings.VoiceprintModelFile);
            if (File.Exists(vpPath))
            {
                var fi = new FileInfo(vpPath);
                TextBlockVoiceprintModelStatus.Text = $"已找到模型文件 ({fi.Length / 1024.0 / 1024.0:F1} MB)";
                TextBlockVoiceprintModelStatus.Foreground = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0, 128, 0));
            }
            else
            {
                TextBlockVoiceprintModelStatus.Text = "模型文件不存在，请将模型放入 models 目录";
                TextBlockVoiceprintModelStatus.Foreground = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(204, 0, 0));
            }

            // Whisper 模型状态
            string whisperPath = Path.Combine(_plugin.ModelsPath, _settings.WhisperModelFile);
            string encoderPath = whisperPath.Replace(".onnx", "_encoder.onnx");
            string decoderPath = whisperPath.Replace(".onnx", "_decoder.onnx");

            if (File.Exists(encoderPath) && File.Exists(decoderPath))
            {
                var encFi = new FileInfo(encoderPath);
                var decFi = new FileInfo(decoderPath);
                TextBlockWhisperModelStatus.Text = $"已找到编码器 ({encFi.Length / 1024.0 / 1024.0:F1} MB) + 解码器 ({decFi.Length / 1024.0 / 1024.0:F1} MB)";
                TextBlockWhisperModelStatus.Foreground = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0, 128, 0));
            }
            else if (File.Exists(whisperPath))
            {
                var fi = new FileInfo(whisperPath);
                TextBlockWhisperModelStatus.Text = $"已找到单一模型 ({fi.Length / 1024.0 / 1024.0:F1} MB)";
                TextBlockWhisperModelStatus.Foreground = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0, 128, 0));
            }
            else
            {
                TextBlockWhisperModelStatus.Text = "模型文件不存在，请将模型放入 models 目录";
                TextBlockWhisperModelStatus.Foreground = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(204, 0, 0));
            }
        }

        /// <summary>
        /// 更新模型状态信息
        /// </summary>
        private void UpdateModelInfo()
        {
            if (TextBlockModelInfo == null) return;

            var lines = new List<string>();

            // 声纹模型状态
            if (_plugin.Recognizer != null)
            {
                lines.Add($"声纹模型: 已加载 (特征维度: {_plugin.Recognizer.EmbeddingDimension})");
                var voiceprints = _plugin.Recognizer.GetRegisteredVoiceprints();
                lines.Add($"已注册声纹: {voiceprints.Count} 个");
            }
            else
            {
                lines.Add("声纹模型: 未加载");
            }

            // 语音转文字模型状态
            if (_plugin.SpeechToText != null && _plugin.SpeechToText.IsInitialized)
            {
                lines.Add($"语音转文字模型: 已加载 ({_plugin.SpeechToText.ModelName})");
            }
            else
            {
                lines.Add("语音转文字模型: 未加载");
            }

            // 音频捕获状态
            if (_plugin.AudioCapture != null)
            {
                lines.Add("音频捕获: 就绪");
            }
            else
            {
                lines.Add("音频捕获: 未初始化");
            }

            // 模型目录
            if (_plugin.ModelsPath != null)
            {
                lines.Add($"模型目录: {_plugin.ModelsPath}");
            }

            TextBlockModelInfo.Text = string.Join("\n", lines);
        }

        /// <summary>
        /// 加载音频设备列表
        /// </summary>
        private void LoadAudioDevices()
        {
            try
            {
                _audioDevices = AudioCapture.GetInputDevices();

                ComboInputDevice.Items.Clear();
                foreach (var device in _audioDevices)
                {
                    ComboInputDevice.Items.Add(new ComboBoxItem
                    {
                        Content = device.Name,
                        Tag = device.Index
                    });
                }

                if (_settings.InputDeviceIndex < ComboInputDevice.Items.Count)
                {
                    ComboInputDevice.SelectedIndex = _settings.InputDeviceIndex;
                }
                else if (ComboInputDevice.Items.Count > 0)
                {
                    ComboInputDevice.SelectedIndex = 0;
                }
            }
            catch (Exception ex)
            {
                _plugin.LogMessage($"加载音频设备失败: {ex.Message}");
            }
        }

        /// <summary>
        /// 加载已注册声纹列表
        /// </summary>
        private void LoadVoiceprintList()
        {
            ListVoiceprints.Items.Clear();

            if (_plugin.Recognizer != null)
            {
                var voiceprints = _plugin.Recognizer.GetRegisteredVoiceprints();
                foreach (var vp in voiceprints)
                {
                    ListVoiceprints.Items.Add(new ListBoxItem
                    {
                        Content = $"{vp.UserName} ({vp.CreatedAt:yyyy-MM-dd HH:mm})",
                        Tag = vp.UserId
                    });
                }
            }
        }

        #region 基本设置事件

        private void CheckEnableVoiceInput_Changed(object sender, RoutedEventArgs e)
        {
            if (_settings != null && sender is CheckBox cb)
                _settings.EnableVoiceInput = cb.IsChecked == true;
        }

        private void CheckEnableVoiceprint_Changed(object sender, RoutedEventArgs e)
        {
            if (_settings != null && sender is CheckBox cb)
                _settings.EnableVoiceprintVerification = cb.IsChecked == true;
        }

        private void CheckRequireMatch_Changed(object sender, RoutedEventArgs e)
        {
            if (_settings != null && sender is CheckBox cb)
                _settings.RequireVoiceprintMatch = cb.IsChecked == true;
        }

        private void SliderThreshold_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (_settings != null)
            {
                _settings.VoiceprintThreshold = (float)SliderThreshold.Value;
                if (TextThreshold != null)
                    TextThreshold.Text = _settings.VoiceprintThreshold.ToString("F2");
            }
        }

        #endregion

        #region 声纹管理事件

        private async void BtnRegisterVoiceprint_Click(object sender, RoutedEventArgs e)
        {
            if (_plugin.Recognizer == null)
            {
                MessageBox.Show("声纹识别引擎未初始化，请先配置声纹模型。", "错误", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            var inputDialog = new InputDialog("注册声纹", "请输入用户名:");
            if (inputDialog.ShowDialog() != true || string.IsNullOrWhiteSpace(inputDialog.InputText))
                return;

            string userName = inputDialog.InputText.Trim();
            string userId = Guid.NewGuid().ToString("N").Substring(0, 8);

            try
            {
                MessageBox.Show("点击确定后开始录音，请说话 3-5 秒，然后点击确定结束。", "录音", MessageBoxButton.OK, MessageBoxImage.Information);

                _plugin.AudioCapture?.StartCapture();

                MessageBox.Show("录音中...点击确定结束录音。", "录音", MessageBoxButton.OK, MessageBoxImage.Information);

                var audioData = _plugin.AudioCapture?.StopCapture();

                if (audioData == null || audioData.Length < 16000 * 2)
                {
                    MessageBox.Show("录音数据不足，请重试。", "错误", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }

                BtnRegisterVoiceprint.IsEnabled = false;
                string originalContent = BtnRegisterVoiceprint.Content?.ToString();
                BtnRegisterVoiceprint.Content = "注册中...";

                bool success = await _plugin.Recognizer.RegisterVoiceprintAsync(userId, userName, audioData);

                if (success)
                {
                    MessageBox.Show($"声纹注册成功！用户: {userName}", "成功", MessageBoxButton.OK, MessageBoxImage.Information);
                    LoadVoiceprintList();
                    UpdateModelInfo();
                }
                else
                {
                    MessageBox.Show("声纹注册失败，请重试。", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
                }

                BtnRegisterVoiceprint.IsEnabled = true;
                BtnRegisterVoiceprint.Content = originalContent;
            }
            catch (Exception ex)
            {
                MessageBox.Show($"注册失败: {ex.Message}", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
                BtnRegisterVoiceprint.IsEnabled = true;
                BtnRegisterVoiceprint.Content = "\U0001F3A4 注册新声纹";
            }
        }

        private void BtnDeleteVoiceprint_Click(object sender, RoutedEventArgs e)
        {
            if (ListVoiceprints.SelectedItem == null)
            {
                MessageBox.Show("请先选择要删除的声纹。", "提示", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            var selectedItem = ListVoiceprints.SelectedItem as ListBoxItem;
            var userId = selectedItem?.Tag?.ToString();

            if (string.IsNullOrEmpty(userId)) return;

            var result = MessageBox.Show("确定要删除选中的声纹吗？", "确认", MessageBoxButton.YesNo, MessageBoxImage.Question);

            if (result == MessageBoxResult.Yes)
            {
                if (_plugin.Recognizer?.RemoveVoiceprint(userId) == true)
                {
                    LoadVoiceprintList();
                    UpdateModelInfo();
                    MessageBox.Show("声纹已删除。", "成功", MessageBoxButton.OK, MessageBoxImage.Information);
                }
            }
        }

        private void BtnOpenModelFolder_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                if (_plugin.ModelsPath != null && Directory.Exists(_plugin.ModelsPath))
                {
                    Process.Start("explorer.exe", _plugin.ModelsPath);
                }
                else
                {
                    MessageBox.Show("模型目录不存在，请检查插件安装是否正确。", "错误", MessageBoxButton.OK, MessageBoxImage.Warning);
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show($"无法打开目录：{ex.Message}", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        #endregion

        #region 模型设置事件

        private void TextVoiceprintModel_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (_settings != null && sender is TextBox tb)
            {
                _settings.VoiceprintModelFile = tb.Text;
                UpdateModelFileStatus();
            }
        }

        private void TextWhisperModel_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (_settings != null && sender is TextBox tb)
            {
                _settings.WhisperModelFile = tb.Text;
                UpdateModelFileStatus();
            }
        }

        private void ComboLanguage_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_settings != null && sender is ComboBox cb && cb.SelectedItem is ComboBoxItem item)
            {
                _settings.Language = item.Tag?.ToString() ?? "zh";
            }
        }

        private void SliderMaxDecoding_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (_settings != null)
                _settings.MaxDecodingLength = (int)SliderMaxDecoding.Value;
        }

        #endregion

        #region 音频设置事件

        private void ComboInputDevice_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_settings != null && sender is ComboBox cb && cb.SelectedItem is ComboBoxItem item)
            {
                if (int.TryParse(item.Tag?.ToString(), out int index))
                    _settings.InputDeviceIndex = index;
            }
        }

        private void BtnRefreshDevices_Click(object sender, RoutedEventArgs e)
        {
            LoadAudioDevices();
        }

        private void ComboSampleRate_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_settings != null && sender is ComboBox cb && cb.SelectedItem is ComboBoxItem item)
            {
                if (int.TryParse(item.Tag?.ToString(), out int rate))
                    _settings.SampleRate = rate;
            }
        }

        private async void BtnTestMic_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                BtnTestMic.IsEnabled = false;
                string originalContent = BtnTestMic.Content?.ToString();
                BtnTestMic.Content = "录音中...";
                TextBlockMicStatus.Text = "正在录音...";

                // 更新设备
                if (ComboInputDevice.SelectedItem is ComboBoxItem selectedDevice)
                {
                    int deviceIndex = int.Parse(selectedDevice.Tag.ToString());
                    _plugin.AudioCapture?.UpdateInputDevice(deviceIndex);
                }

                _plugin.AudioCapture?.StartCapture();
                await System.Threading.Tasks.Task.Delay(2000);
                var audioData = _plugin.AudioCapture?.StopCapture();

                if (audioData != null && audioData.Length > 0)
                {
                    float maxVolume = 0;
                    for (int i = 0; i < audioData.Length / 2; i++)
                    {
                        short sample = BitConverter.ToInt16(audioData, i * 2);
                        float volume = Math.Abs(sample) / 32768.0f;
                        maxVolume = Math.Max(maxVolume, volume);
                    }

                    TextBlockMicStatus.Text = $"录音 {audioData.Length / 32000.0:F1} 秒, 最大音量 {maxVolume:P0}";
                    MessageBox.Show($"麦克风测试完成！\n录音长度: {audioData.Length / 32000.0:F1} 秒\n最大音量: {maxVolume:P0}",
                        "测试结果", MessageBoxButton.OK, MessageBoxImage.Information);
                }
                else
                {
                    TextBlockMicStatus.Text = "未获取到音频数据";
                    MessageBox.Show("未获取到音频数据，请检查麦克风连接。", "测试失败", MessageBoxButton.OK, MessageBoxImage.Warning);
                }

                BtnTestMic.IsEnabled = true;
                BtnTestMic.Content = originalContent;
            }
            catch (Exception ex)
            {
                TextBlockMicStatus.Text = $"测试失败: {ex.Message}";
                MessageBox.Show($"测试失败: {ex.Message}", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
                BtnTestMic.IsEnabled = true;
                BtnTestMic.Content = "\U0001F3A4 测试麦克风";
            }
        }

        private void SliderMinDuration_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (_settings != null)
                _settings.MinRecordingDuration = (float)SliderMinDuration.Value;
        }

        private void SliderMaxDuration_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (_settings != null)
                _settings.MaxRecordingDuration = (float)SliderMaxDuration.Value;
        }

        private void SliderSilenceTimeout_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (_settings != null)
                _settings.SilenceTimeout = (float)SliderSilenceTimeout.Value;
        }

        private void CheckUseGPU_Changed(object sender, RoutedEventArgs e)
        {
            if (_settings != null && sender is CheckBox cb)
                _settings.UseGPU = cb.IsChecked == true;
        }

        private void SliderThreads_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (_settings != null)
                _settings.NumThreads = (int)SliderThreads.Value;
        }

        #endregion

        #region 调试设置事件

        private void CheckDebugMode_Changed(object sender, RoutedEventArgs e)
        {
            if (_settings != null && sender is CheckBox cb)
                _settings.DebugMode = cb.IsChecked == true;
        }

        private void CheckSaveRecordings_Changed(object sender, RoutedEventArgs e)
        {
            if (_settings != null && sender is CheckBox cb)
                _settings.SaveRecordings = cb.IsChecked == true;
        }

        private void BtnReloadModels_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                BtnReloadModels.IsEnabled = false;
                TextBlockReloadStatus.Text = "正在重新加载...";

                _plugin.ReloadRecognizer();
                _plugin.ReloadSpeechToText();

                UpdateModelInfo();
                TextBlockReloadStatus.Text = "重新加载完成";
                MessageBox.Show("模型已重新加载！", "成功", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                TextBlockReloadStatus.Text = $"重新加载失败: {ex.Message}";
                MessageBox.Show($"重新加载失败: {ex.Message}", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                BtnReloadModels.IsEnabled = true;
            }
        }

        private void BtnClearLog_Click(object sender, RoutedEventArgs e)
        {
            _plugin.ClearLogs();
            if (TextBoxLog != null)
            {
                TextBoxLog.Clear();
                TextBoxLog.Text = "日志已清空。\n";
            }
        }

        #endregion

        #region 底部按钮事件

        private void BtnTestVoice_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                if (_plugin.SpeechToText == null || !_plugin.SpeechToText.IsInitialized)
                {
                    MessageBox.Show("语音转文字模型未加载，请先配置模型。", "提示", MessageBoxButton.OK, MessageBoxImage.Information);
                    return;
                }

                MessageBox.Show("点击确定后开始录音，请说话，然后点击确定结束。", "语音测试", MessageBoxButton.OK, MessageBoxImage.Information);

                _plugin.AudioCapture?.StartCapture();

                MessageBox.Show("录音中...点击确定结束录音。", "语音测试", MessageBoxButton.OK, MessageBoxImage.Information);

                var audioData = _plugin.AudioCapture?.StopCapture();

                if (audioData == null || audioData.Length < 16000 * 2)
                {
                    MessageBox.Show("录音数据不足，请重试。", "错误", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }

                string text = _plugin.SpeechToText.Transcribe(audioData);
                MessageBox.Show($"识别结果:\n{text}", "语音转文字", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                MessageBox.Show($"语音测试失败: {ex.Message}", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void BtnSave_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                // 验证设置
                _settings.Validate();

                // 应用设置
                _plugin.Settings = _settings;
                _plugin.SaveSettings();

                // 重新加载组件
                _plugin.ReloadRecognizer();
                _plugin.ReloadSpeechToText();

                MessageBox.Show("设置已保存！", "成功", MessageBoxButton.OK, MessageBoxImage.Information);

                // 更新原始设置
                _originalSettings = _settings.Clone();
            }
            catch (Exception ex)
            {
                MessageBox.Show($"保存设置失败: {ex.Message}", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void BtnReset_Click(object sender, RoutedEventArgs e)
        {
            var result = MessageBox.Show("确定要重置为默认设置吗？", "确认", MessageBoxButton.YesNo, MessageBoxImage.Question);
            if (result == MessageBoxResult.Yes)
            {
                _settings = new VoiceprintSettings();
                LoadSettings();
            }
        }

        private void BtnCancel_Click(object sender, RoutedEventArgs e)
        {
            Close();
        }

        #endregion

        protected override void OnClosed(EventArgs e)
        {
            base.OnClosed(e);

            if (_logUpdateTimer != null)
            {
                _logUpdateTimer.Stop();
                _logUpdateTimer = null;
            }
        }
    }

    /// <summary>
    /// 简单的输入对话框
    /// </summary>
    public class InputDialog : Window
    {
        private TextBox _textBox;

        public string InputText => _textBox.Text;

        public InputDialog(string title, string prompt)
        {
            Title = title;
            Width = 350;
            Height = 150;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
            ResizeMode = ResizeMode.NoResize;
            Background = System.Windows.Media.Brushes.White;

            var grid = new Grid { Margin = new Thickness(15) };
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            var label = new TextBlock
            {
                Text = prompt,
                Margin = new Thickness(0, 0, 0, 10)
            };
            Grid.SetRow(label, 0);
            grid.Children.Add(label);

            _textBox = new TextBox { Margin = new Thickness(0, 0, 0, 15) };
            Grid.SetRow(_textBox, 1);
            grid.Children.Add(_textBox);

            var buttonPanel = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
            var okButton = new Button { Content = "确定", Width = 80, Margin = new Thickness(0, 0, 10, 0) };
            var cancelButton = new Button { Content = "取消", Width = 80 };

            okButton.Click += (s, e) => { DialogResult = true; Close(); };
            cancelButton.Click += (s, e) => { DialogResult = false; Close(); };

            buttonPanel.Children.Add(okButton);
            buttonPanel.Children.Add(cancelButton);
            Grid.SetRow(buttonPanel, 2);
            grid.Children.Add(buttonPanel);

            Content = grid;
        }
    }
}
