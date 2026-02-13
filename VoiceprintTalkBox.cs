using System;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Panuon.WPF.UI;
using VPet_Simulator.Core;
using VPet_Simulator.Windows.Interface;

namespace VPet.Plugin.VoiceprintRecognition
{
    /// <summary>
    /// 声纹识别语音输入 TalkBox
    /// 继承 TalkBox 基类，添加声纹识别功能
    /// </summary>
    public class VoiceprintTalkBox : TalkBox
    {
        private readonly VoiceprintPlugin _plugin;

        /// <summary>
        /// 语音输入按钮
        /// </summary>
        private Button _btnVoice;

        /// <summary>
        /// 声纹验证状态指示器
        /// </summary>
        private Border _voiceprintIndicator;

        /// <summary>
        /// 是否正在录音
        /// </summary>
        private bool _isRecording = false;

        /// <summary>
        /// 是否正在处理
        /// </summary>
        private bool _isProcessing = false;

        public override string APIName => "VoiceprintRecognition";

        public VoiceprintTalkBox(VoiceprintPlugin plugin) : base(plugin)
        {
            _plugin = plugin;
            InitializeVoiceButton();
        }

        /// <summary>
        /// 初始化语音按钮
        /// </summary>
        private void InitializeVoiceButton()
        {
            try
            {
                // 检查 MainGrid 是否可用
                if (MainGrid == null)
                {
                    _plugin.LogMessage("MainGrid 为空，无法初始化语音按钮");
                    return;
                }

                // 添加新列用于放置语音按钮
                MainGrid.ColumnDefinitions.Add(new ColumnDefinition() { Width = new GridLength(0, GridUnitType.Auto) });

                // 创建声纹验证状态指示器
                _voiceprintIndicator = new Border
                {
                    Width = 8,
                    Height = 8,
                    CornerRadius = new CornerRadius(4),
                    Background = Brushes.Gray,
                    VerticalAlignment = VerticalAlignment.Center,
                    Margin = new Thickness(5, 0, 0, 0),
                    ToolTip = "声纹未验证"
                };
                Grid.SetColumn(_voiceprintIndicator, 3);
                MainGrid.Children.Add(_voiceprintIndicator);

                // 添加语音按钮列
                MainGrid.ColumnDefinitions.Add(new ColumnDefinition() { Width = new GridLength(0, GridUnitType.Auto) });

                // 创建语音输入按钮
                _btnVoice = new Button
                {
                    Content = "MIC",
                    BorderThickness = new Thickness(2),
                    ToolTip = "长按进行语音输入（支持声纹验证）",
                    Cursor = Cursors.Hand,
                    FontSize = 14,
                    Padding = new Thickness(8, 4, 8, 4),
                    Margin = new Thickness(5, 0, 0, 0),
                    Visibility = _plugin.Settings.EnableVoiceInput ? Visibility.Visible : Visibility.Collapsed
                };

                // 尝试设置样式和颜色
                try
                {
                    var darkBrush = Function.ResourcesBrush(Function.BrushType.DARKPrimaryDarker);
                    var lightBrush = Function.ResourcesBrush(Function.BrushType.SecondaryLight);
                    if (darkBrush != null) _btnVoice.BorderBrush = darkBrush;
                    if (lightBrush != null) _btnVoice.Background = lightBrush;
                }
                catch (Exception ex)
                {
                    _plugin.LogMessage($"设置按钮样式失败: {ex.Message}");
                }

                // 设置按钮圆角
                try
                {
                    ButtonHelper.SetCornerRadius(_btnVoice, new CornerRadius(4));
                }
                catch (Exception ex)
                {
                    _plugin.LogMessage($"设置按钮圆角失败: {ex.Message}");
                }

                // 绑定事件
                _btnVoice.PreviewMouseDown += BtnVoice_PreviewMouseDown;
                _btnVoice.PreviewMouseUp += BtnVoice_PreviewMouseUp;

                Grid.SetColumn(_btnVoice, 4);
                MainGrid.Children.Add(_btnVoice);

                _plugin.LogMessage("语音按钮初始化完成");
            }
            catch (Exception ex)
            {
                _plugin.LogMessage($"初始化语音按钮失败: {ex.Message}");
            }
        }

        /// <summary>
        /// 按下语音按钮 - 开始录音
        /// </summary>
        private async void BtnVoice_PreviewMouseDown(object sender, MouseButtonEventArgs e)
        {
            if (_isProcessing || _isRecording)
                return;

            try
            {
                _isRecording = true;
                UpdateButtonState(true);

                _plugin.LogMessage("开始录音...");

                // 开始音频采集
                _plugin.AudioCapture?.StartCapture();

                // 更新 UI 显示录音状态
                await Dispatcher.InvokeAsync(() =>
                {
                    if (_btnVoice != null)
                    {
                        _btnVoice.Background = Brushes.Red;
                        _btnVoice.ToolTip = "正在录音...松开结束";
                    }
                });
            }
            catch (Exception ex)
            {
                _plugin.LogMessage($"开始录音失败: {ex.Message}");
                _isRecording = false;
                UpdateButtonState(false);
            }
        }

        /// <summary>
        /// 松开语音按钮 - 停止录音并处理
        /// </summary>
        private async void BtnVoice_PreviewMouseUp(object sender, MouseButtonEventArgs e)
        {
            if (!_isRecording)
                return;

            _isRecording = false;

            try
            {
                _isProcessing = true;

                _plugin.LogMessage("停止录音，开始处理...");

                // 停止音频采集
                var audioData = _plugin.AudioCapture?.StopCapture();

                if (audioData == null || audioData.Length == 0)
                {
                    _plugin.LogMessage("未获取到音频数据");
                    UpdateButtonState(false);
                    return;
                }

                // 更新 UI 显示处理状态
                await Dispatcher.InvokeAsync(() =>
                {
                    if (_btnVoice != null)
                    {
                        _btnVoice.Background = Brushes.Orange;
                        _btnVoice.ToolTip = "正在处理...";
                    }
                });

                // 在后台线程处理
                await Task.Run(async () =>
                {
                    await ProcessAudioAsync(audioData);
                });
            }
            catch (Exception ex)
            {
                _plugin.LogMessage($"处理录音失败: {ex.Message}");
            }
            finally
            {
                _isProcessing = false;
                UpdateButtonState(false);
            }
        }

        /// <summary>
        /// 处理音频数据
        /// </summary>
        private async Task ProcessAudioAsync(byte[] audioData)
        {
            try
            {
                // 1. 声纹验证（如果启用）
                bool voiceprintVerified = true;
                float voiceprintScore = 0;

                if (_plugin.Settings.EnableVoiceprintVerification && _plugin.Recognizer != null)
                {
                    _plugin.LogMessage("开始声纹验证...");

                    var result = await _plugin.Recognizer.VerifyAsync(audioData);
                    voiceprintVerified = result.IsVerified;
                    voiceprintScore = result.Confidence;

                    _plugin.LogMessage($"声纹验证结果: {(voiceprintVerified ? "通过" : "未通过")}, 置信度: {voiceprintScore:P2}");

                    // 更新声纹指示器
                    await Dispatcher.InvokeAsync(() =>
                    {
                        UpdateVoiceprintIndicator(voiceprintVerified, voiceprintScore);
                    });

                    if (!voiceprintVerified && _plugin.Settings.RequireVoiceprintMatch)
                    {
                        _plugin.LogMessage("声纹验证未通过，拒绝输入");
                        await Dispatcher.InvokeAsync(() =>
                        {
                            MessageBox.Show("声纹验证未通过，请确认是本人说话。",
                                "声纹验证失败",
                                MessageBoxButton.OK,
                                MessageBoxImage.Warning);
                        });
                        return;
                    }
                }

                // 2. 语音转文字
                if (_plugin.SpeechToText != null)
                {
                    _plugin.LogMessage("开始语音转文字...");

                    var text = await _plugin.SpeechToText.TranscribeAsync(audioData);

                    if (!string.IsNullOrWhiteSpace(text))
                    {
                        _plugin.LogMessage($"识别结果: {text}");

                        // 将识别结果添加到输入框
                        await Dispatcher.InvokeAsync(() =>
                        {
                            tbTalk?.AppendText(text);
                        });
                    }
                    else
                    {
                        _plugin.LogMessage("未识别到语音内容");
                    }
                }
                else
                {
                    _plugin.LogMessage("语音转文字服务未初始化");
                }
            }
            catch (Exception ex)
            {
                _plugin.LogMessage($"处理音频失败: {ex.Message}");
            }
        }

        /// <summary>
        /// 更新按钮状态
        /// </summary>
        private void UpdateButtonState(bool isActive)
        {
            try
            {
                Dispatcher.Invoke(() =>
                {
                    if (_btnVoice == null) return;

                    if (isActive)
                    {
                        _btnVoice.Background = Brushes.Red;
                    }
                    else
                    {
                        try
                        {
                            _btnVoice.Background = Function.ResourcesBrush(Function.BrushType.SecondaryLight);
                        }
                        catch
                        {
                            _btnVoice.Background = Brushes.LightGray;
                        }
                        _btnVoice.ToolTip = "长按进行语音输入（支持声纹验证）";
                    }
                });
            }
            catch (Exception ex)
            {
                _plugin.LogMessage($"更新按钮状态失败: {ex.Message}");
            }
        }

        /// <summary>
        /// 更新声纹指示器
        /// </summary>
        private void UpdateVoiceprintIndicator(bool verified, float confidence)
        {
            if (_voiceprintIndicator == null) return;

            if (verified)
            {
                _voiceprintIndicator.Background = Brushes.LimeGreen;
                _voiceprintIndicator.ToolTip = $"声纹已验证 (置信度: {confidence:P0})";
            }
            else
            {
                _voiceprintIndicator.Background = Brushes.Red;
                _voiceprintIndicator.ToolTip = $"声纹未通过 (置信度: {confidence:P0})";
            }
        }

        /// <summary>
        /// 更新语音按钮可见性
        /// </summary>
        public void UpdateVoiceButtonVisibility()
        {
            try
            {
                Dispatcher.Invoke(() =>
                {
                    if (_btnVoice != null)
                    {
                        _btnVoice.Visibility = _plugin.Settings.EnableVoiceInput
                            ? Visibility.Visible
                            : Visibility.Collapsed;
                    }
                });
            }
            catch (Exception ex)
            {
                _plugin.LogMessage($"更新按钮可见性失败: {ex.Message}");
            }
        }

        public override void Responded(string content)
        {
            if (string.IsNullOrEmpty(content))
                return;

            // 显示思考动画
            DisplayThink();

            try
            {
                _plugin.LogMessage($"收到输入: {content}");

                Dispatcher.Invoke(() => this.IsEnabled = false);

                Task.Run(() =>
                {
                    try
                    {
                        DisplayThinkToSayRnd("收到你的消息：" + content);
                    }
                    finally
                    {
                        Dispatcher.Invoke(() => this.IsEnabled = true);
                    }
                });
            }
            catch (Exception ex)
            {
                _plugin.LogMessage($"处理回复失败: {ex.Message}");
                Dispatcher.Invoke(() => this.IsEnabled = true);
            }
        }

        public override void Setting()
        {
            _plugin.Setting();
        }
    }
}
