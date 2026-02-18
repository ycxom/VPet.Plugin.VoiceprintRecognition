using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
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
        private bool _isMonitoring = false;
        private bool _disposed = false;
        private readonly ManualResetEventSlim _stoppedEvent = new ManualResetEventSlim(true);

        // 环形缓冲区：监听模式下保留最近 N 秒音频，用于声纹验证
        private const int RING_BUFFER_SECONDS = 5;
        private readonly Queue<byte[]> _monitoringRingBuffer = new Queue<byte[]>();
        private int _ringBufferTotalBytes = 0;
        private readonly object _ringBufferLock = new object();

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
        /// 是否正在监听模式
        /// </summary>
        public bool IsMonitoring => _isMonitoring;

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

            // 如果正在监听，先停止监听
            if (_isMonitoring)
                StopMonitoring();

            try
            {
                WaitForStopped();
                _audioStream = new MemoryStream();
                _stoppedEvent.Reset();
                _waveIn.StartRecording();
                _isRecording = true;

                Console.WriteLine("[音频采集] 开始录音");
            }
            catch (Exception ex)
            {
                _stoppedEvent.Set();
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
                _isRecording = false;
                _waveIn.StopRecording();

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
        /// 开始监听模式（不累积数据，仅触发事件）
        /// </summary>
        public void StartMonitoring()
        {
            if (_isMonitoring)
                return;

            // 如果正在录音，不能切换到监听
            if (_isRecording)
            {
                Console.WriteLine("[音频采集] 正在录音中，无法启动监听模式");
                return;
            }

            try
            {
                WaitForStopped();
                _stoppedEvent.Reset();
                _waveIn.StartRecording();
                _isMonitoring = true;
                Console.WriteLine("[音频采集] 开始监听模式");
            }
            catch (Exception ex)
            {
                _stoppedEvent.Set();
                Console.WriteLine($"[音频采集] 开始监听失败: {ex.Message}");
                throw;
            }
        }

        /// <summary>
        /// 停止监听模式
        /// </summary>
        public void StopMonitoring()
        {
            if (!_isMonitoring)
                return;

            try
            {
                _isMonitoring = false;
                _waveIn.StopRecording();
                Console.WriteLine("[音频采集] 停止监听模式");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[音频采集] 停止监听失败: {ex.Message}");
            }
        }

        /// <summary>
        /// 等待 WaveIn 完全停止（解决 NAudio StopRecording 异步问题）
        /// </summary>
        private void WaitForStopped()
        {
            if (!_stoppedEvent.Wait(2000))
            {
                Console.WriteLine("[音频采集] 等待停止超时，强制重置");
                // 强制重新初始化 WaveIn
                try
                {
                    _waveIn.DataAvailable -= WaveIn_DataAvailable;
                    _waveIn.RecordingStopped -= WaveIn_RecordingStopped;
                    _waveIn.Dispose();
                }
                catch { }
                Initialize();
                _stoppedEvent.Set();
            }
        }

        /// <summary>
        /// 音频数据到达事件
        /// </summary>
        private void WaveIn_DataAvailable(object sender, WaveInEventArgs e)
        {
            if (e.BytesRecorded > 0)
            {
                // 录音模式：写入到内存流
                if (_isRecording)
                {
                    _audioStream?.Write(e.Buffer, 0, e.BytesRecorded);
                }

                // 监听模式：写入环形缓冲区
                if (_isMonitoring)
                {
                    var chunk = new byte[e.BytesRecorded];
                    Array.Copy(e.Buffer, chunk, e.BytesRecorded);

                    lock (_ringBufferLock)
                    {
                        _monitoringRingBuffer.Enqueue(chunk);
                        _ringBufferTotalBytes += chunk.Length;

                        // 按字节数限制缓冲区大小（采样率 * 字节深度 * 通道 * 秒数）
                        int maxBytes = SampleRate * (BitsPerSample / 8) * Channels * RING_BUFFER_SECONDS;
                        while (_ringBufferTotalBytes > maxBytes && _monitoringRingBuffer.Count > 0)
                        {
                            var removed = _monitoringRingBuffer.Dequeue();
                            _ringBufferTotalBytes -= removed.Length;
                        }
                    }
                }

                // 录音模式和监听模式都触发事件
                if (_isRecording || _isMonitoring)
                {
                    var data = new byte[e.BytesRecorded];
                    Array.Copy(e.Buffer, data, e.BytesRecorded);
                    AudioDataAvailable?.Invoke(this, data);
                }
            }
        }

        /// <summary>
        /// 从环形缓冲区获取最近 N 秒的音频数据（监听模式下使用）
        /// </summary>
        /// <param name="seconds">需要获取的秒数</param>
        /// <returns>PCM 音频数据，如果缓冲区为空则返回 null</returns>
        public byte[] GetRecentAudio(float seconds)
        {
            lock (_ringBufferLock)
            {
                if (_monitoringRingBuffer.Count == 0)
                    return null;

                int bytesPerSecond = SampleRate * (BitsPerSample / 8) * Channels;
                int requestedBytes = (int)(seconds * bytesPerSecond);

                // 从缓冲区尾部向前取数据
                var chunks = _monitoringRingBuffer.ToArray();
                int totalAvailable = 0;
                foreach (var chunk in chunks)
                    totalAvailable += chunk.Length;

                int bytesToCopy = Math.Min(requestedBytes, totalAvailable);
                var result = new byte[bytesToCopy];
                int offset = totalAvailable - bytesToCopy;
                int destPos = 0;
                int srcPos = 0;

                foreach (var chunk in chunks)
                {
                    if (srcPos + chunk.Length <= offset)
                    {
                        srcPos += chunk.Length;
                        continue;
                    }

                    int chunkStart = Math.Max(0, offset - srcPos);
                    int chunkLen = Math.Min(chunk.Length - chunkStart, bytesToCopy - destPos);
                    Array.Copy(chunk, chunkStart, result, destPos, chunkLen);
                    destPos += chunkLen;
                    srcPos += chunk.Length;

                    if (destPos >= bytesToCopy)
                        break;
                }

                return result;
            }
        }

        /// <summary>
        /// 录音停止事件（NAudio 回调）
        /// </summary>
        private void WaveIn_RecordingStopped(object sender, StoppedEventArgs e)
        {
            _isRecording = false;
            _isMonitoring = false;
            _stoppedEvent.Set();

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
            bool wasMonitoring = _isMonitoring;

            if (wasRecording)
                StopCapture();
            if (wasMonitoring)
                StopMonitoring();

            _waveIn?.Dispose();

            _settings.InputDeviceIndex = deviceIndex;
            Initialize();

            if (wasRecording)
                StartCapture();
            if (wasMonitoring)
                StartMonitoring();
        }

        public void Dispose()
        {
            if (!_disposed)
            {
                if (_isRecording)
                    StopCapture();
                if (_isMonitoring)
                    StopMonitoring();

                _waveIn?.Dispose();
                _audioStream?.Dispose();
                _stoppedEvent?.Dispose();
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
