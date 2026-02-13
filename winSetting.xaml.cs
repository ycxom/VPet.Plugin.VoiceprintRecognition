using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
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
        private List<AudioDeviceInfo> _audioDevices;

        public winSetting(VoiceprintPlugin plugin)
        {
            InitializeComponent();

            // 应用资源
            try
            {
                Resources = Application.Current.Resources;
            }
            catch { }

            _plugin = plugin;
            _settings = plugin.Settings.Clone();

            // 加载设置到 UI
            LoadSettings();

            // 绑定事件
            BindEvents();
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
            TextVoiceprintModel.Text = _settings.VoiceprintModelFile;
            SliderThreshold.Value = _settings.VoiceprintThreshold;
            TextThreshold.Text = _settings.VoiceprintThreshold.ToString("F2");

            // 加载已注册声纹列表
            LoadVoiceprintList();

            // 语音转文字设置
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

            // 性能设置
            CheckUseGPU.IsChecked = _settings.UseGPU;
            SliderThreads.Value = _settings.NumThreads;
            TextThreads.Text = _settings.NumThreads.ToString();

            // 调试设置
            CheckDebugMode.IsChecked = _settings.DebugMode;
            CheckSaveRecordings.IsChecked = _settings.SaveRecordings;
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

                // 选择当前设备
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
                        Content = $"{vp.UserName} ({vp.CreatedAt:yyyy-MM-dd})",
                        Tag = vp.UserId
                    });
                }
            }
        }

        /// <summary>
        /// 绑定事件
        /// </summary>
        private void BindEvents()
        {
            // 阈值滑块
            SliderThreshold.ValueChanged += (s, e) =>
            {
                TextThreshold.Text = SliderThreshold.Value.ToString("F2");
            };

            // 线程数滑块
            SliderThreads.ValueChanged += (s, e) =>
            {
                TextThreads.Text = ((int)SliderThreads.Value).ToString();
            };

            // 注册声纹按钮
            BtnRegisterVoiceprint.Click += BtnRegisterVoiceprint_Click;

            // 删除声纹按钮
            BtnDeleteVoiceprint.Click += BtnDeleteVoiceprint_Click;

            // 测试麦克风按钮
            BtnTestMic.Click += BtnTestMic_Click;

            // 保存按钮
            BtnSave.Click += BtnSave_Click;

            // 取消按钮
            BtnCancel.Click += (s, e) => Close();
        }

        /// <summary>
        /// 注册新声纹
        /// </summary>
        private async void BtnRegisterVoiceprint_Click(object sender, RoutedEventArgs e)
        {
            if (_plugin.Recognizer == null)
            {
                MessageBox.Show("声纹识别引擎未初始化，请先配置声纹模型。",
                    "错误", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            // 获取用户名
            var inputDialog = new InputDialog("注册声纹", "请输入用户名:");
            if (inputDialog.ShowDialog() != true || string.IsNullOrWhiteSpace(inputDialog.InputText))
                return;

            string userName = inputDialog.InputText.Trim();
            string userId = Guid.NewGuid().ToString("N").Substring(0, 8);

            try
            {
                // 开始录音
                MessageBox.Show("点击确定后开始录音，请说话 3-5 秒，然后点击确定结束。",
                    "录音", MessageBoxButton.OK, MessageBoxImage.Information);

                _plugin.AudioCapture?.StartCapture();

                // 等待用户确认结束录音
                MessageBox.Show("录音中...点击确定结束录音。",
                    "录音", MessageBoxButton.OK, MessageBoxImage.Information);

                var audioData = _plugin.AudioCapture?.StopCapture();

                if (audioData == null || audioData.Length < 16000 * 2) // 至少 1 秒
                {
                    MessageBox.Show("录音数据不足，请重试。",
                        "错误", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }

                // 注册声纹
                BtnRegisterVoiceprint.IsEnabled = false;
                BtnRegisterVoiceprint.Content = "注册中...";

                bool success = await _plugin.Recognizer.RegisterVoiceprintAsync(userId, userName, audioData);

                if (success)
                {
                    MessageBox.Show($"声纹注册成功！用户: {userName}",
                        "成功", MessageBoxButton.OK, MessageBoxImage.Information);
                    LoadVoiceprintList();
                }
                else
                {
                    MessageBox.Show("声纹注册失败，请重试。",
                        "错误", MessageBoxButton.OK, MessageBoxImage.Error);
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show($"注册失败: {ex.Message}",
                    "错误", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                BtnRegisterVoiceprint.IsEnabled = true;
                BtnRegisterVoiceprint.Content = "注册新声纹";
            }
        }

        /// <summary>
        /// 删除声纹
        /// </summary>
        private void BtnDeleteVoiceprint_Click(object sender, RoutedEventArgs e)
        {
            if (ListVoiceprints.SelectedItem == null)
            {
                MessageBox.Show("请先选择要删除的声纹。",
                    "提示", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            var selectedItem = ListVoiceprints.SelectedItem as ListBoxItem;
            var userId = selectedItem?.Tag?.ToString();

            if (string.IsNullOrEmpty(userId))
                return;

            var result = MessageBox.Show("确定要删除选中的声纹吗？",
                "确认", MessageBoxButton.YesNo, MessageBoxImage.Question);

            if (result == MessageBoxResult.Yes)
            {
                if (_plugin.Recognizer?.RemoveVoiceprint(userId) == true)
                {
                    LoadVoiceprintList();
                    MessageBox.Show("声纹已删除。",
                        "成功", MessageBoxButton.OK, MessageBoxImage.Information);
                }
            }
        }

        /// <summary>
        /// 测试麦克风
        /// </summary>
        private async void BtnTestMic_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                BtnTestMic.IsEnabled = false;
                BtnTestMic.Content = "录音中...";

                // 更新设备设置
                if (ComboInputDevice.SelectedItem is ComboBoxItem selectedDevice)
                {
                    int deviceIndex = int.Parse(selectedDevice.Tag.ToString());
                    _plugin.AudioCapture?.UpdateInputDevice(deviceIndex);
                }

                // 开始录音
                _plugin.AudioCapture?.StartCapture();

                // 录音 2 秒
                await System.Threading.Tasks.Task.Delay(2000);

                var audioData = _plugin.AudioCapture?.StopCapture();

                if (audioData != null && audioData.Length > 0)
                {
                    // 计算音量
                    float maxVolume = 0;
                    for (int i = 0; i < audioData.Length / 2; i++)
                    {
                        short sample = BitConverter.ToInt16(audioData, i * 2);
                        float volume = Math.Abs(sample) / 32768.0f;
                        maxVolume = Math.Max(maxVolume, volume);
                    }

                    MessageBox.Show($"麦克风测试完成！\n录音长度: {audioData.Length / 32000.0:F1} 秒\n最大音量: {maxVolume:P0}",
                        "测试结果", MessageBoxButton.OK, MessageBoxImage.Information);
                }
                else
                {
                    MessageBox.Show("未获取到音频数据，请检查麦克风连接。",
                        "测试失败", MessageBoxButton.OK, MessageBoxImage.Warning);
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show($"测试失败: {ex.Message}",
                    "错误", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                BtnTestMic.IsEnabled = true;
                BtnTestMic.Content = "测试麦克风";
            }
        }

        /// <summary>
        /// 保存设置
        /// </summary>
        private void BtnSave_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                // 收集设置
                _settings.EnableVoiceInput = CheckEnableVoiceInput.IsChecked == true;
                _settings.EnableVoiceprintVerification = CheckEnableVoiceprint.IsChecked == true;
                _settings.RequireVoiceprintMatch = CheckRequireMatch.IsChecked == true;

                _settings.VoiceprintModelFile = TextVoiceprintModel.Text;
                _settings.VoiceprintThreshold = (float)SliderThreshold.Value;

                _settings.WhisperModelFile = TextWhisperModel.Text;
                if (ComboLanguage.SelectedItem is ComboBoxItem langItem)
                    _settings.Language = langItem.Tag?.ToString() ?? "zh";

                if (ComboInputDevice.SelectedItem is ComboBoxItem deviceItem)
                    _settings.InputDeviceIndex = int.Parse(deviceItem.Tag.ToString());

                if (ComboSampleRate.SelectedItem is ComboBoxItem rateItem)
                    _settings.SampleRate = int.Parse(rateItem.Tag.ToString());

                _settings.UseGPU = CheckUseGPU.IsChecked == true;
                _settings.NumThreads = (int)SliderThreads.Value;

                _settings.DebugMode = CheckDebugMode.IsChecked == true;
                _settings.SaveRecordings = CheckSaveRecordings.IsChecked == true;

                // 验证设置
                _settings.Validate();

                // 应用设置
                _plugin.Settings = _settings;
                _plugin.SaveSettings();

                // 重新加载组件
                _plugin.ReloadRecognizer();
                _plugin.ReloadSpeechToText();

                MessageBox.Show("设置已保存！",
                    "成功", MessageBoxButton.OK, MessageBoxImage.Information);

                Close();
            }
            catch (Exception ex)
            {
                MessageBox.Show($"保存设置失败: {ex.Message}",
                    "错误", MessageBoxButton.OK, MessageBoxImage.Error);
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

            try
            {
                Background = (System.Windows.Media.Brush)Application.Current.Resources["DARKPrimary"];
            }
            catch
            {
                Background = System.Windows.Media.Brushes.White;
            }

            var grid = new Grid { Margin = new Thickness(15) };
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            var label = new TextBlock
            {
                Text = prompt,
                Margin = new Thickness(0, 0, 0, 10)
            };

            try
            {
                label.Foreground = (System.Windows.Media.Brush)Application.Current.Resources["DARKPrimaryText"];
            }
            catch { }

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
