using System;
using System.Collections.Generic;
using System.IO;
using NAudio.Wave;

namespace VPet.Plugin.VoiceprintRecognition
{
    /// <summary>
    /// 音频采集器
    /// 使用 NAudio 进行麦克风音频采集
    /// </summary>
    public class AudioCapture : IDisposable
    {
        private WaveInEvent _waveIn;
        private MemoryStream _audioStream;
        private readonly VoiceprintSettings _settings;
        private bool _isRecording = false;
        private bool _disposed = false;

        /// <summary>
        /// 采样率
        /// </summary>
        public int SampleRate => _settings.SampleRate;

        /// <summary>
        /// 通道数
        /// </summary>
        public int Channels => _settings.Channels;

        /// <summary>
        /// 位深度
        /// </summary>
        public int BitsPerSample => _settings.BitsPerSample;

        /// <summary>
        /// 是否正在录音
        /// </summary>
        public bool IsRecording => _isRecording;

        /// <summary>
        /// 音频数据可用事件
        /// </summary>
        public event EventHandler<byte[]> AudioDataAvailable;

        /// <summary>
        /// 录音停止事件
        /// </summary>
        public event EventHandler RecordingStopped;

        public AudioCapture(VoiceprintSettings settings)
        {
            _settings = settings;
            Initialize();
        }

        /// <summary>
        /// 初始化音频采集
        /// </summary>
        private void Initialize()
        {
            try
            {
                _waveIn = new WaveInEvent
                {
                    DeviceNumber = _settings.InputDeviceIndex,
                    WaveFormat = new WaveFormat(SampleRate, BitsPerSample, Channels),
                    BufferMilliseconds = 100
                };

                _waveIn.DataAvailable += WaveIn_DataAvailable;
                _waveIn.RecordingStopped += WaveIn_RecordingStopped;

                Console.WriteLine($"[音频采集] 初始化完成: {SampleRate}Hz, {BitsPerSample}bit, {Channels}ch");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[音频采集] 初始化失败: {ex.Message}");
                throw;
            }
        }

        /// <summary>
        /// 开始录音
        /// </summary>
        public void StartCapture()
        {
            if (_isRecording)
                return;

            try
            {
                _audioStream = new MemoryStream();
                _waveIn.StartRecording();
                _isRecording = true;

                Console.WriteLine("[音频采集] 开始录音");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[音频采集] 开始录音失败: {ex.Message}");
                throw;
            }
        }

        /// <summary>
        /// 停止录音并返回音频数据
        /// </summary>
        public byte[] StopCapture()
        {
            if (!_isRecording)
                return null;

            try
            {
                _waveIn.StopRecording();
                _isRecording = false;

                var audioData = _audioStream?.ToArray();
                _audioStream?.Dispose();
                _audioStream = null;

                Console.WriteLine($"[音频采集] 停止录音，数据大小: {audioData?.Length ?? 0} bytes");
                return audioData;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[音频采集] 停止录音失败: {ex.Message}");
                return null;
            }
        }

        /// <summary>
        /// 音频数据到达事件
        /// </summary>
        private void WaveIn_DataAvailable(object sender, WaveInEventArgs e)
        {
            if (_isRecording && e.BytesRecorded > 0)
            {
                // 写入到内存流
                _audioStream?.Write(e.Buffer, 0, e.BytesRecorded);

                // 触发事件
                AudioDataAvailable?.Invoke(this, e.Buffer);
            }
        }

        /// <summary>
        /// 录音停止事件
        /// </summary>
        private void WaveIn_RecordingStopped(object sender, StoppedEventArgs e)
        {
            _isRecording = false;

            if (e.Exception != null)
            {
                Console.WriteLine($"[音频采集] 录音异常停止: {e.Exception.Message}");
            }

            RecordingStopped?.Invoke(this, EventArgs.Empty);
        }

        /// <summary>
        /// 获取可用的输入设备列表
        /// </summary>
        public static List<AudioDeviceInfo> GetInputDevices()
        {
            var devices = new List<AudioDeviceInfo>();

            for (int i = 0; i < WaveIn.DeviceCount; i++)
            {
                var capabilities = WaveIn.GetCapabilities(i);
                devices.Add(new AudioDeviceInfo
                {
                    Index = i,
                    Name = capabilities.ProductName,
                    Channels = capabilities.Channels
                });
            }

            return devices;
        }

        /// <summary>
        /// 更新输入设备
        /// </summary>
        public void UpdateInputDevice(int deviceIndex)
        {
            bool wasRecording = _isRecording;

            if (wasRecording)
                StopCapture();

            _waveIn?.Dispose();

            _settings.InputDeviceIndex = deviceIndex;
            Initialize();

            if (wasRecording)
                StartCapture();
        }

        public void Dispose()
        {
            if (!_disposed)
            {
                if (_isRecording)
                    StopCapture();

                _waveIn?.Dispose();
                _audioStream?.Dispose();
                _disposed = true;
            }
        }
    }

    /// <summary>
    /// 音频设备信息
    /// </summary>
    public class AudioDeviceInfo
    {
        public int Index { get; set; }
        public string Name { get; set; }
        public int Channels { get; set; }

        public override string ToString() => Name;
    }
}
