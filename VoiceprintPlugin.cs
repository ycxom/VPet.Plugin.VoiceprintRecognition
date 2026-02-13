using System;
using System.IO;
using System.Reflection;
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
        /// 设置窗口
        /// </summary>
        private winSetting _winSetting;

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
                LogMessage("设置加载完成");
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

                // 加载设置
                LoadSettings();

                // 初始化声纹识别引擎（可选，如果没有模型文件则跳过）
                InitializeRecognizer();

                // 初始化音频采集
                InitializeAudioCapture();

                // 初始化语音转文字服务（可选）
                InitializeSpeechToText();

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
                    Recognizer = new VoiceprintRecognizer(modelPath, Settings);
                    LogMessage($"声纹识别引擎已加载: {Settings.VoiceprintModelFile}");
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
                SpeechToText = new SpeechToTextService(whisperModelPath, Settings);
                LogMessage("语音转文字服务初始化完成");
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
        /// 输出日志
        /// </summary>
        public void LogMessage(string message)
        {
            var timestamp = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
            Console.WriteLine($"[声纹识别] [{timestamp}] {message}");
        }
    }
}
