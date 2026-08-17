using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using VPet_Simulator.Windows.Interface;

namespace VPet.Plugin.VoiceprintRecognition
{
    /// <summary>
    /// VPet 声纹识别插件主类
    /// 使用 ONNX 模型实现本地化声纹识别
    /// </summary>
    public class VoiceprintPlugin : MainPlugin
    {
        public override string PluginName => "声纹识别";

        /// <summary>
        /// 插件设置
        /// </summary>
        public VoiceprintSettings Settings { get; set; }

        /// <summary>
        /// 声纹识别引擎
        /// </summary>
        public VoiceprintRecognizer Recognizer { get; private set; }

        /// <summary>
        /// 音频采集器
        /// </summary>
        public AudioCapture AudioCapture { get; private set; }

        /// <summary>
        /// 语音转文字服务
        /// </summary>
        public SpeechToTextService SpeechToText { get; private set; }

        /// <summary>
        /// 声纹唤醒服务
        /// </summary>
        public VoiceWakeupService WakeupService { get; private set; }

        /// <summary>
        /// 外部 ASR 服务
        /// </summary>
        public ExternalAsrService ExternalAsr { get; private set; }

        /// <summary>
        /// Windows 语音识别服务
        /// </summary>
        public WindowsSpeechService WindowsSpeech { get; private set; }

        /// <summary>
        /// 日志缓冲区
        /// </summary>
        private readonly ConcurrentQueue<string> _logBuffer = new ConcurrentQueue<string>();

        /// <summary>
        /// 设置窗口
        /// </summary>
        private winSetting _winSetting;

        /// <summary>
        /// TalkBox 引用（用于唤醒文字路由）
        /// </summary>
        private VoiceprintTalkBox _talkBox;

        /// <summary>
        /// 插件根目录
        /// </summary>
        public string PluginPath { get; private set; }

        /// <summary>
        /// 数据目录
        /// </summary>
        public string DataPath { get; private set; }

        /// <summary>
        /// 模型目录
        /// </summary>
        public string ModelsPath { get; private set; }

        public VoiceprintPlugin(IMainWindow mainwin) : base(mainwin)
        {
            // 构造函数中只做最基本的初始化
            // 路径和设置在 LoadPlugin 中初始化
        }

        /// <summary>
        /// 初始化路径
        /// </summary>
        private void InitializePaths()
        {
            try
            {
                var assembly = Assembly.GetExecutingAssembly();
                var location = assembly.Location;
                var directory = Path.GetDirectoryName(location);
                PluginPath = Path.GetDirectoryName(directory); // plugin 的父目录

                if (string.IsNullOrEmpty(PluginPath))
                {
                    LogMessage("无法获取插件路径");
                    return;
                }

                DataPath = Path.Combine(PluginPath, "data");
                ModelsPath = Path.Combine(PluginPath, "models");

                // 确保目录存在
                if (!Directory.Exists(DataPath))
                    Directory.CreateDirectory(DataPath);
                if (!Directory.Exists(ModelsPath))
                    Directory.CreateDirectory(ModelsPath);

                LogMessage($"插件路径: {PluginPath}");
                LogMessage($"数据路径: {DataPath}");
                LogMessage($"模型路径: {ModelsPath}");
            }
            catch (Exception ex)
            {
                LogMessage($"初始化路径失败: {ex.Message}");
                PluginPath = "";
                DataPath = "";
                ModelsPath = "";
            }
        }

        /// <summary>
        /// 注册程序集解析器并预加载运行时 DLL
        /// System.Speech NuGet 包的根目录 DLL 是平台检测 stub，真正的 Windows 实现在 runtimes\ 子目录
        /// </summary>
        private void RegisterAssemblyResolver()
        {
            var pluginDir = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);
            var runtimesDir = Path.Combine(pluginDir, "runtimes");

            // 预加载 System.Speech 运行时 DLL（必须在任何 System.Speech 类型被引用之前）
            var speechDll = Path.Combine(runtimesDir, "System.Speech.dll");
            if (File.Exists(speechDll))
            {
                try
                {
                    var loadContext = System.Runtime.Loader.AssemblyLoadContext.GetLoadContext(
                        Assembly.GetExecutingAssembly()) ?? System.Runtime.Loader.AssemblyLoadContext.Default;
                    loadContext.LoadFromAssemblyPath(speechDll);
                    LogDebug($"预加载 System.Speech 成功: {speechDll}");
                }
                catch (Exception ex)
                {
                    LogMessage($"预加载 System.Speech 失败: {ex.Message}");
                }
            }
            else
            {
                LogMessage($"System.Speech 运行时 DLL 不存在: {speechDll}");
            }

            // 注册解析器作为后备
            var loadCtx = System.Runtime.Loader.AssemblyLoadContext.GetLoadContext(
                Assembly.GetExecutingAssembly()) ?? System.Runtime.Loader.AssemblyLoadContext.Default;
            loadCtx.Resolving += (context, assemblyName) =>
            {
                var dllPath = Path.Combine(runtimesDir, assemblyName.Name + ".dll");
                if (File.Exists(dllPath))
                {
                    LogDebug($"程序集解析 (runtimes): {assemblyName.Name} -> {dllPath}");
                    return context.LoadFromAssemblyPath(dllPath);
                }

                dllPath = Path.Combine(pluginDir, assemblyName.Name + ".dll");
                if (File.Exists(dllPath))
                {
                    LogDebug($"程序集解析: {assemblyName.Name} -> {dllPath}");
                    return context.LoadFromAssemblyPath(dllPath);
                }
                return null;
            };
            LogDebug($"程序集解析器已注册，runtimes 目录: {runtimesDir}");
        }

        /// <summary>
        /// 加载设置
        /// </summary>
        private void LoadSettings()
        {
            try
            {
                if (string.IsNullOrEmpty(DataPath))
                {
                    Settings = new VoiceprintSettings();
                    return;
                }

                string settingsPath = Path.Combine(DataPath, "settings.json");
                Settings = VoiceprintSettings.LoadFromFile(settingsPath);

                // 同步 DebugMode 到 LogLevel
                LogLevel = Settings.DebugMode ? 1 : 0;

                LogMessage("设置加载完成");
                LogDebug($"设置文件: {settingsPath}");
            }
            catch (Exception ex)
            {
                LogMessage($"加载设置失败，使用默认设置: {ex.Message}");
                Settings = new VoiceprintSettings();
            }
        }

        /// <summary>
        /// 保存设置
        /// </summary>
        public void SaveSettings()
        {
            try
            {
                if (string.IsNullOrEmpty(DataPath))
                {
                    LogMessage("数据路径未初始化，无法保存设置");
                    return;
                }

                string settingsPath = Path.Combine(DataPath, "settings.json");
                Settings.SaveToFile(settingsPath);
                LogMessage("设置已保存");
            }
            catch (Exception ex)
            {
                LogMessage($"保存设置失败: {ex.Message}");
            }
        }

        public override void LoadPlugin()
        {
            try
            {
                LogMessage("开始加载声纹识别插件...");

                // 初始化路径
                InitializePaths();

                // 注册程序集解析器，确保从插件目录加载 System.Speech 等运行时 DLL
                RegisterAssemblyResolver();

                // 加载设置
                LoadSettings();

                // 初始化声纹识别引擎（可选，如果没有模型文件则跳过）
                InitializeRecognizer();

                // 初始化音频采集
                InitializeAudioCapture();

                // 初始化语音转文字服务（可选）
                InitializeSpeechToText();

                // 初始化外部 ASR 服务
                InitializeExternalAsr();

                // 初始化唤醒服务
                InitializeWakeupService();

                // 初始化 Windows 语音识别服务
                InitializeWindowsSpeech();

                // 添加设置菜单
                AddSettingsMenu();

                LogMessage("声纹识别插件加载完成");
            }
            catch (Exception ex)
            {
                LogMessage($"插件加载失败: {ex.Message}");
            }
        }

        /// <summary>
        /// 初始化声纹识别引擎
        /// </summary>
        private void InitializeRecognizer()
        {
            try
            {
                if (string.IsNullOrEmpty(ModelsPath) || Settings == null)
                {
                    Recognizer = null;
                    return;
                }

                string modelPath = Path.Combine(ModelsPath, Settings.VoiceprintModelFile);

                if (File.Exists(modelPath))
                {
                    LogDebug($"加载声纹模型: {modelPath}");
                    Recognizer = new VoiceprintRecognizer(modelPath, Settings,
                        logInfo: LogMessage, logDebug: LogDebug);
                    LogMessage($"声纹识别引擎已加载: {Settings.VoiceprintModelFile}");
                    LogDebug($"特征维度: {Recognizer.EmbeddingDimension}, 已注册声纹: {Recognizer.GetRegisteredVoiceprints().Count}");
                }
                else
                {
                    LogMessage($"声纹模型文件不存在: {modelPath}");
                    Recognizer = null;
                }
            }
            catch (Exception ex)
            {
                LogMessage($"初始化声纹识别引擎失败: {ex.Message}");
                Recognizer = null;
            }
        }

        /// <summary>
        /// 初始化音频采集
        /// </summary>
        private void InitializeAudioCapture()
        {
            try
            {
                if (Settings == null)
                {
                    AudioCapture = null;
                    return;
                }

                AudioCapture = new AudioCapture(Settings);
                LogMessage("音频采集器初始化完成");
                LogDebug($"音频设备: {Settings.InputDeviceIndex}, 采样率: {Settings.SampleRate}, 通道: {Settings.Channels}");
            }
            catch (Exception ex)
            {
                LogMessage($"初始化音频采集失败: {ex.Message}");
                AudioCapture = null;
            }
        }

        /// <summary>
        /// 初始化语音转文字服务
        /// </summary>
        private void InitializeSpeechToText()
        {
            try
            {
                if (string.IsNullOrEmpty(ModelsPath) || Settings == null)
                {
                    SpeechToText = null;
                    return;
                }

                string whisperModelPath = Path.Combine(ModelsPath, Settings.WhisperModelFile);
                SpeechToText = new SpeechToTextService(whisperModelPath, Settings,
                    logInfo: LogMessage, logDebug: LogDebug);

                if (SpeechToText.IsInitialized)
                    LogMessage($"语音转文字模型已加载: {SpeechToText.ModelName}");
                else
                    LogMessage($"语音转文字模型未加载 (模型文件: {whisperModelPath})");
            }
            catch (Exception ex)
            {
                LogMessage($"初始化语音转文字服务失败: {ex.Message}");
                SpeechToText = null;
            }
        }

        /// <summary>
        /// 添加设置菜单到 MOD 配置
        /// </summary>
        private void AddSettingsMenu()
        {
            try
            {
                if (MW?.Main?.ToolBar?.MenuMODConfig == null)
                {
                    LogMessage("MenuMODConfig 为 null，稍后重试");
                    return;
                }

                var menuItem = new MenuItem()
                {
                    Header = "声纹识别设置",
                    HorizontalContentAlignment = HorizontalAlignment.Center
                };
                menuItem.Click += (s, e) => Setting();

                MW.Main.ToolBar.MenuMODConfig.Visibility = Visibility.Visible;
                MW.Main.ToolBar.MenuMODConfig.Items.Add(menuItem);

                LogMessage("设置菜单已添加到 MOD 配置");
            }
            catch (Exception ex)
            {
                LogMessage($"添加设置菜单失败: {ex.Message}");
            }
        }

        public override void Setting()
        {
            try
            {
                if (_winSetting == null || !_winSetting.IsLoaded)
                {
                    _winSetting = new winSetting(this);

                    if (MW is Window mainWindow)
                    {
                        _winSetting.Owner = mainWindow;
                    }

                    _winSetting.Closed += (s, e) => _winSetting = null;
                    _winSetting.Show();

                    LogMessage("设置窗口已打开");
                }
                else
                {
                    _winSetting.Activate();
                    _winSetting.Topmost = true;
                    _winSetting.Topmost = false;
                }
            }
            catch (Exception ex)
            {
                LogMessage($"打开设置窗口失败: {ex.Message}");
                MessageBox.Show($"无法打开设置窗口: {ex.Message}", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        public override void Save()
        {
            SaveSettings();
        }

        /// <summary>
        /// 初始化外部 ASR 服务
        /// </summary>
        private void InitializeExternalAsr()
        {
            try
            {
                ExternalAsr?.Dispose();
                ExternalAsr = new ExternalAsrService(Settings,
                    logInfo: LogMessage, logDebug: LogDebug);

                if (!string.IsNullOrWhiteSpace(Settings.AsrApiUrl))
                    LogMessage($"外部 ASR 服务已初始化: {Settings.AsrApiUrl}");
                else
                    LogDebug("外部 ASR 未配置 API URL");
            }
            catch (Exception ex)
            {
                LogMessage($"初始化外部 ASR 失败: {ex.Message}");
                ExternalAsr = null;
            }
        }

        /// <summary>
        /// 初始化唤醒服务
        /// </summary>
        private void InitializeWakeupService()
        {
            try
            {
                if (WakeupService != null)
                {
                    WakeupService.StopMonitoring();
                    WakeupService.WakeupDetected -= OnWakeupDetected;
                    WakeupService.DictationStarted -= OnDictationStarted;
                    WakeupService.DictationEnded -= OnDictationEnded;
                    WakeupService.StatusChanged -= OnWakeupStatusChanged;
                }

                // 如果使用 Windows 语音模式，不初始化自定义唤醒服务
                if (Settings.UseWindowsSpeech)
                {
                    WakeupService = null;
                    LogDebug("唤醒服务: 使用 Windows 语音模式，跳过自定义唤醒");
                    return;
                }

                if (Recognizer == null || AudioCapture == null)
                {
                    LogDebug("唤醒服务需要声纹识别引擎和音频采集器");
                    WakeupService = null;
                    return;
                }

                // 至少需要一种 ASR 服务
                bool hasLocalAsr = SpeechToText != null && SpeechToText.IsInitialized;
                bool hasExternalAsr = ExternalAsr != null && !string.IsNullOrWhiteSpace(Settings.AsrApiUrl);
                if (!hasLocalAsr && !hasExternalAsr)
                {
                    LogMessage("唤醒服务需要至少一种 ASR 服务（本地 Whisper 模型或外部 ASR API）");
                    WakeupService = null;
                    return;
                }

                WakeupService = new VoiceWakeupService(Settings, Recognizer, AudioCapture,
                    localAsr: SpeechToText, externalAsr: ExternalAsr,
                    logInfo: LogMessage, logDebug: LogDebug,
                    modelsPath: ModelsPath);
                WakeupService.WakeupDetected += OnWakeupDetected;
                WakeupService.DictationStarted += OnDictationStarted;
                WakeupService.DictationEnded += OnDictationEnded;
                WakeupService.StatusChanged += OnWakeupStatusChanged;

                // 如果已启用且有注册声纹，自动开始监听
                if (Settings.EnableWakeup && Recognizer.GetRegisteredVoiceprints().Count > 0)
                {
                    WakeupService.StartMonitoring();
                }

                LogDebug("唤醒服务已初始化");
            }
            catch (Exception ex)
            {
                LogMessage($"初始化唤醒服务失败: {ex.Message}");
                WakeupService = null;
            }
        }

        /// <summary>
        /// 初始化 Windows 语音识别服务
        /// </summary>
        private void InitializeWindowsSpeech()
        {
            try
            {
                if (WindowsSpeech != null)
                {
                    WindowsSpeech.Stop();
                    WindowsSpeech.WakeupTextReceived -= OnWindowsSpeechTextReceived;
                    WindowsSpeech.WakeupAudioReady -= OnWakeupAudioReady;
                    WindowsSpeech.DictationStarted -= OnDictationStarted;
                    WindowsSpeech.DictationPartialResult -= OnDictationPartialResult;
                    WindowsSpeech.DictationEnded -= OnDictationEnded;
                    WindowsSpeech.Dispose();
                    WindowsSpeech = null;
                }

                if (!Settings.UseWindowsSpeech)
                {
                    LogDebug("Windows 语音识别: 未启用");
                    return;
                }

                if (Recognizer == null || AudioCapture == null)
                {
                    LogDebug("Windows 语音识别需要声纹识别引擎和音频采集器");
                    return;
                }

                if (Recognizer.GetRegisteredVoiceprints().Count == 0)
                {
                    LogDebug("Windows 语音识别需要至少一个已注册声纹");
                    return;
                }

                WindowsSpeech = new WindowsSpeechService(Settings, Recognizer, AudioCapture,
                    localAsr: SpeechToText, externalAsr: ExternalAsr,
                    logInfo: LogMessage, logDebug: LogDebug);
                WindowsSpeech.WakeupTextReceived += OnWindowsSpeechTextReceived;
                WindowsSpeech.WakeupAudioReady += OnWakeupAudioReady;
                WindowsSpeech.DictationStarted += OnDictationStarted;
                WindowsSpeech.DictationPartialResult += OnDictationPartialResult;
                WindowsSpeech.DictationEnded += OnDictationEnded;

                // 如果已启用唤醒，自动启动
                if (Settings.EnableWakeup)
                {
                    WindowsSpeech.Start();
                }

                LogDebug("Windows 语音识别服务已初始化");
            }
            catch (Exception ex)
            {
                LogMessage($"初始化 Windows 语音识别失败: {ex.Message}");
                WindowsSpeech = null;
            }
        }

        /// <summary>
        /// 当前唤醒弹窗引用
        /// </summary>
        private Window _wakeupPopup;

        /// <summary>
        /// 弹窗中的流式文字显示控件
        /// </summary>
        private TextBlock _wakeupPopupText;

        /// <summary>
        /// 听写开始回调 - 弹窗提示用户说话
        /// </summary>
        private void OnWakeupStatusChanged(string status)
        {
            try
            {
                Application.Current?.Dispatcher?.Invoke(() =>
                {
                    if (_wakeupPopupText != null)
                        _wakeupPopupText.Text = status ?? string.Empty;
                });
            }
            catch { }
        }

        private void OnDictationStarted()
        {
            try
            {
                LogDebug("OnDictationStarted 回调触发");
                Application.Current.Dispatcher.Invoke(() =>
                {
                    CloseWakeupPopup();

                    _wakeupPopup = CreateWakeupPopup("", isListening: true);
                    _wakeupPopup.Show();
                    LogDebug($"唤醒弹窗已显示（聆听中），popupText={_wakeupPopupText != null}");
                });
            }
            catch (Exception ex)
            {
                LogMessage($"显示唤醒弹窗失败: {ex.Message}");
            }
        }

        /// <summary>
        /// 听写超时/结束回调 - 关闭弹窗
        /// </summary>
        private void OnDictationEnded()
        {
            try
            {
                Application.Current.Dispatcher.Invoke(() =>
                {
                    CloseWakeupPopup();
                    LogDebug("唤醒弹窗已关闭（超时）");
                });
            }
            catch (Exception ex)
            {
                LogMessage($"关闭唤醒弹窗失败: {ex.Message}");
            }
        }

        /// <summary>
        /// 听写流式部分结果回调 - 实时更新弹窗文字
        /// </summary>
        private void OnDictationPartialResult(string partialText)
        {
            try
            {
                LogDebug($"流式结果: \"{partialText}\"");
                Application.Current.Dispatcher.Invoke(() =>
                {
                    if (_wakeupPopupText != null && _wakeupPopup != null)
                    {
                        _wakeupPopupText.Text = partialText;
                        LogDebug($"弹窗文字已更新: \"{partialText}\"");
                    }
                    else
                    {
                        LogDebug($"弹窗未就绪: popup={_wakeupPopup != null}, text={_wakeupPopupText != null}");
                    }
                });
            }
            catch (Exception ex)
            {
                LogDebug($"更新流式文字失败: {ex.Message}");
            }
        }

        /// <summary>
        /// 关闭唤醒弹窗
        /// </summary>
        private void CloseWakeupPopup()
        {
            if (_wakeupPopup != null)
            {
                try { _wakeupPopup.Close(); } catch { }
                _wakeupPopup = null;
                _wakeupPopupText = null;
            }
        }

        /// <summary>
        /// 创建唤醒弹窗
        /// </summary>
        private Window CreateWakeupPopup(string text, bool isListening)
        {
            var window = new Window
            {
                Title = "语音唤醒",
                Width = 420,
                Height = isListening ? 150 : 200,
                WindowStartupLocation = WindowStartupLocation.CenterScreen,
                ResizeMode = ResizeMode.NoResize,
                Topmost = true,
                ShowInTaskbar = false,
                Background = System.Windows.Media.Brushes.White
            };

            var grid = new Grid { Margin = new Thickness(15) };

            if (isListening)
            {
                // 聆听状态：显示提示 + 流式文字 + 取消按钮
                grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
                grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
                grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

                var header = new TextBlock
                {
                    Text = "已唤醒，请说话...",
                    FontSize = 14,
                    Foreground = System.Windows.Media.Brushes.Gray,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    Margin = new Thickness(0, 0, 0, 6)
                };
                Grid.SetRow(header, 0);
                grid.Children.Add(header);

                var label = new TextBlock
                {
                    Text = text,
                    FontSize = 16,
                    TextWrapping = TextWrapping.Wrap,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center
                };
                Grid.SetRow(label, 1);
                grid.Children.Add(label);

                // 保存引用，供流式更新使用
                _wakeupPopupText = label;

                var btnCancel = new Button
                {
                    Content = "取消",
                    Width = 80,
                    Height = 30,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    Margin = new Thickness(0, 8, 0, 0)
                };
                btnCancel.Click += (s, e) =>
                {
                    LogMessage("唤醒聆听已取消");
                    window.Close();
                };
                Grid.SetRow(btnCancel, 2);
                grid.Children.Add(btnCancel);
            }

            window.Content = grid;
            return window;
        }

        /// <summary>
        /// 唤醒检测回调（自定义模式：VAD + 声纹 + ASR 唤醒词检测）
        /// </summary>
        private void OnWakeupDetected(string text, VoiceprintVerificationResult result)
        {
            try
            {
                LogMessage($"唤醒触发 - 用户: {result?.MatchedUserId}, 余弦: {result?.Similarity:F3}, 显示置信度: {result?.Confidence:P1}, 文字: {text}");

                Application.Current.Dispatcher.Invoke(() => CloseWakeupPopup());

                if (!string.IsNullOrWhiteSpace(text))
                {
                    RouteTextToTalkBox(text);
                }
            }
            catch (Exception ex)
            {
                LogMessage($"唤醒处理失败: {ex.Message}");
            }
        }

        /// <summary>
        /// Windows 语音识别文字结果回调（回退：无外部 ASR 时使用）
        /// </summary>
        private void OnWindowsSpeechTextReceived(string text, VoiceprintVerificationResult result)
        {
            try
            {
                var userId = result?.MatchedUserId ?? "未知";
                var confidence = result?.Confidence ?? 0;
                LogMessage($"Windows 语音回退 - 用户: {userId}, 余弦: {result?.Similarity:F3}, 显示置信度: {confidence:P1}, 文字: {text}");

                Application.Current.Dispatcher.Invoke(() => CloseWakeupPopup());

                RouteTextToTalkBox(text);
            }
            catch (Exception ex)
            {
                LogMessage($"Windows 语音结果处理失败: {ex.Message}");
            }
        }

        /// <summary>
        /// 唤醒音频就绪回调 - 发送到外部 ASR 获取高精度转写
        /// </summary>
        private async void OnWakeupAudioReady(byte[] audioData, VoiceprintVerificationResult result)
        {
            try
            {
                var userId = result?.MatchedUserId ?? "未知";
                LogMessage($"唤醒音频就绪 - 用户: {userId}, 音频: {audioData.Length} 字节");

                // 更新弹窗显示 "正在识别..."
                Application.Current.Dispatcher.Invoke(() =>
                {
                    if (_wakeupPopupText != null)
                        _wakeupPopupText.Text = "正在识别...";
                });

                if (ExternalAsr == null || string.IsNullOrWhiteSpace(Settings.AsrApiUrl))
                {
                    LogMessage("外部 ASR 未配置，无法转写");
                    Application.Current.Dispatcher.Invoke(() => CloseWakeupPopup());
                    DictationEndedHandler();
                    return;
                }

                LogMessage("调用外部 ASR...");
                var text = await ExternalAsr.TranscribeAsync(audioData);

                Application.Current.Dispatcher.Invoke(() => CloseWakeupPopup());

                if (!string.IsNullOrWhiteSpace(text))
                {
                    LogMessage($"外部 ASR 结果: {text}");
                    RouteTextToTalkBox(text);
                }
                else
                {
                    LogMessage("外部 ASR 未返回有效文字");
                }
            }
            catch (Exception ex)
            {
                LogMessage($"外部 ASR 处理失败: {ex.Message}");
                Application.Current.Dispatcher.Invoke(() => CloseWakeupPopup());
            }
        }

        /// <summary>
        /// 听写结束辅助（关闭弹窗）
        /// </summary>
        private void DictationEndedHandler()
        {
            Application.Current.Dispatcher.Invoke(() => CloseWakeupPopup());
        }

        /// <summary>
        /// 将文字路由到 TalkBox
        /// </summary>
        private void RouteTextToTalkBox(string text)
        {
            if (_talkBox != null)
            {
                _talkBox.OnWakeupTextReceived(text, Settings.WakeupAutoSend);
            }
            else
            {
                LogMessage("TalkBox 未初始化，无法路由文字");
            }
        }

        /// <summary>
        /// 设置 TalkBox 引用
        /// </summary>
        public void SetTalkBox(VoiceprintTalkBox talkBox)
        {
            _talkBox = talkBox;
        }

        /// <summary>
        /// 重新加载唤醒服务（模式互斥：Windows Speech 或自定义模式）
        /// </summary>
        public void ReloadWakeupService()
        {
            try
            {
                // 停止旧的 Windows 语音服务
                if (WindowsSpeech != null)
                {
                    WindowsSpeech.Stop();
                    WindowsSpeech.WakeupTextReceived -= OnWindowsSpeechTextReceived;
                    WindowsSpeech.WakeupAudioReady -= OnWakeupAudioReady;
                    WindowsSpeech.DictationStarted -= OnDictationStarted;
                    WindowsSpeech.DictationPartialResult -= OnDictationPartialResult;
                    WindowsSpeech.DictationEnded -= OnDictationEnded;
                    WindowsSpeech.Dispose();
                    WindowsSpeech = null;
                }

                // 停止旧的自定义唤醒服务
                if (WakeupService != null)
                {
                    WakeupService.StopMonitoring();
                    WakeupService.WakeupDetected -= OnWakeupDetected;
                    WakeupService.DictationStarted -= OnDictationStarted;
                    WakeupService.DictationEnded -= OnDictationEnded;
                    WakeupService = null;
                }

                InitializeExternalAsr();

                if (Settings.UseWindowsSpeech)
                {
                    // Windows 语音模式
                    InitializeWindowsSpeech();
                }
                else
                {
                    // 自定义模式
                    InitializeWakeupService();
                }
            }
            catch (Exception ex)
            {
                LogMessage($"重新加载唤醒服务失败: {ex.Message}");
            }
        }

        /// <summary>
        /// 重新加载声纹识别引擎
        /// </summary>
        public void ReloadRecognizer()
        {
            try
            {
                Recognizer?.Dispose();
                InitializeRecognizer();
            }
            catch (Exception ex)
            {
                LogMessage($"重新加载识别引擎失败: {ex.Message}");
            }
        }

        /// <summary>
        /// 重新加载语音转文字服务
        /// </summary>
        public void ReloadSpeechToText()
        {
            try
            {
                SpeechToText?.Dispose();
                InitializeSpeechToText();
            }
            catch (Exception ex)
            {
                LogMessage($"重新加载语音转文字服务失败: {ex.Message}");
            }
        }

        /// <summary>
        /// 当前日志等级 (0=Info, 1=Debug)
        /// </summary>
        public int LogLevel { get; set; } = 0;

        /// <summary>
        /// 输出 Info 日志
        /// </summary>
        public void LogMessage(string message)
        {
            Log("INFO", message);
        }

        /// <summary>
        /// 输出 Debug 日志
        /// </summary>
        public void LogDebug(string message)
        {
            if (LogLevel < 1) return;
            Log("DEBUG", message);
        }

        private void Log(string level, string message)
        {
            var timestamp = DateTime.Now.ToString("HH:mm:ss");
            var line = $"[{timestamp}] [{level}] {message}";
            Console.WriteLine($"[声纹识别] {line}");
            _logBuffer.Enqueue(line);

            while (_logBuffer.Count > 500)
                _logBuffer.TryDequeue(out _);
        }

        /// <summary>
        /// 获取日志消息，按当前等级过滤
        /// </summary>
        public List<string> GetLogMessages()
        {
            if (LogLevel >= 1)
                return _logBuffer.ToList();

            // Info 模式下过滤掉 DEBUG
            return _logBuffer.Where(l => !l.Contains("[DEBUG]")).ToList();
        }

        /// <summary>
        /// 清空日志
        /// </summary>
        public void ClearLogs()
        {
            while (_logBuffer.TryDequeue(out _)) { }
        }
    }
}
