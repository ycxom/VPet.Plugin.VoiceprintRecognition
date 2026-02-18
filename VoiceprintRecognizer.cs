using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;

namespace VPet.Plugin.VoiceprintRecognition
{
    /// <summary>
    /// 声纹验证结果
    /// </summary>
    public class VoiceprintVerificationResult
    {
        /// <summary>
        /// 是否验证通过
        /// </summary>
        public bool IsVerified { get; set; }

        /// <summary>
        /// 置信度 (0-1)
        /// </summary>
        public float Confidence { get; set; }

        /// <summary>
        /// 匹配的用户 ID（如果有多用户）
        /// </summary>
        public string MatchedUserId { get; set; }

        /// <summary>
        /// 错误信息
        /// </summary>
        public string Error { get; set; }
    }

    /// <summary>
    /// 声纹特征
    /// </summary>
    public class VoiceprintEmbedding
    {
        /// <summary>
        /// 用户 ID
        /// </summary>
        public string UserId { get; set; }

        /// <summary>
        /// 唤醒词（注册时填写）
        /// </summary>
        public string UserName { get; set; }

        /// <summary>
        /// 特征向量
        /// </summary>
        public float[] Embedding { get; set; }

        /// <summary>
        /// 唤醒词能量包络（用于 DTW 模式匹配）
        /// </summary>
        public List<WakeWordEnvelope> WakeWordEnvelopes { get; set; } = new List<WakeWordEnvelope>();

        /// <summary>
        /// 创建时间
        /// </summary>
        public DateTime CreatedAt { get; set; }
    }

    /// <summary>
    /// ONNX 声纹识别引擎
    /// 支持声纹提取、注册和验证
    /// </summary>
    public class VoiceprintRecognizer : IDisposable
    {
        private InferenceSession _session;
        private readonly VoiceprintSettings _settings;
        private readonly List<VoiceprintEmbedding> _registeredVoiceprints;
        private readonly string _voiceprintDataPath;
        private bool _disposed = false;

        private string _inputName;
        private string _outputName;

        public int EmbeddingDimension { get; private set; }

        // Fbank 参数 (CAM++ 模型)
        private const int FBANK_SAMPLE_RATE = 16000;
        private const int FBANK_FFT_SIZE = 512;
        private const int FBANK_WIN_SIZE = 400;  // 25ms
        private const int FBANK_HOP_SIZE = 160;  // 10ms
        private const int FBANK_N_MELS = 80;

        private float[,] _melFilters;

        // 日志回调
        private Action<string> _logInfo;
        private Action<string> _logDebug;

        public VoiceprintRecognizer(string modelPath, VoiceprintSettings settings,
            Action<string> logInfo = null, Action<string> logDebug = null)
        {
            _settings = settings;
            _registeredVoiceprints = new List<VoiceprintEmbedding>();
            _logInfo = logInfo ?? (s => Console.WriteLine($"[声纹识别] {s}"));
            _logDebug = logDebug ?? (s => Console.WriteLine($"[声纹识别][DEBUG] {s}"));

            var modelDir = Path.GetDirectoryName(modelPath);
            _voiceprintDataPath = Path.Combine(Path.GetDirectoryName(modelDir), "data", "voiceprints.json");

            LoadModel(modelPath);
            LoadRegisteredVoiceprints();
        }

        /// <summary>
        /// 加载 ONNX 模型
        /// </summary>
        private void LoadModel(string modelPath)
        {
            try
            {
                if (!File.Exists(modelPath))
                    throw new FileNotFoundException($"模型文件不存在: {modelPath}");

                var options = new SessionOptions();

                if (_settings.UseGPU)
                {
                    try { options.AppendExecutionProvider_CUDA(); _logDebug("使用 CUDA GPU"); }
                    catch
                    {
                        try { options.AppendExecutionProvider_DML(); _logDebug("使用 DirectML GPU"); }
                        catch { _logDebug("GPU 不可用，使用 CPU"); }
                    }
                }

                options.AppendExecutionProvider_CPU();
                options.InterOpNumThreads = _settings.NumThreads;
                options.IntraOpNumThreads = _settings.NumThreads;

                _session = new InferenceSession(modelPath, options);

                _inputName = _session.InputMetadata.Keys.First();
                _outputName = _session.OutputMetadata.Keys.First();

                var outputMeta = _session.OutputMetadata[_outputName];
                EmbeddingDimension = (int)outputMeta.Dimensions.Last();

                _logDebug($"输入: {_inputName} [{string.Join(", ", _session.InputMetadata[_inputName].Dimensions)}]");
                _logDebug($"输出: {_outputName} [{string.Join(", ", outputMeta.Dimensions)}]");
                _logDebug($"特征维度: {EmbeddingDimension}");

                // 初始化 Mel 滤波器组
                _melFilters = CreateMelFilterbank(FBANK_FFT_SIZE, FBANK_N_MELS, FBANK_SAMPLE_RATE);
                _logDebug($"Mel 滤波器组: {FBANK_N_MELS} x {FBANK_FFT_SIZE / 2 + 1}");
            }
            catch (Exception ex)
            {
                throw new Exception($"加载模型失败: {ex.Message}", ex);
            }
        }

        /// <summary>
        /// 从音频数据提取声纹特征
        /// </summary>
        public async Task<float[]> ExtractEmbeddingAsync(byte[] audioData)
        {
            return await Task.Run(() => ExtractEmbedding(audioData));
        }

        /// <summary>
        /// 从音频数据提取声纹特征（同步版本）
        /// </summary>
        public float[] ExtractEmbedding(byte[] audioData)
        {
            if (_session == null)
                throw new InvalidOperationException("模型未加载");

            try
            {
                // PCM 转浮点
                int sampleCount = audioData.Length / 2;
                float[] samples = new float[sampleCount];
                for (int i = 0; i < sampleCount; i++)
                {
                    short s = BitConverter.ToInt16(audioData, i * 2);
                    samples[i] = s / 32768.0f;
                }
                _logDebug($"预处理音频: {sampleCount} 采样, {sampleCount / (float)FBANK_SAMPLE_RATE:F2} 秒");

                var inputMeta = _session.InputMetadata[_inputName];
                var dims = inputMeta.Dimensions.ToArray();

                DenseTensor<float> inputTensor;

                // 判断输入格式: [1, -1, 80] = Fbank, [1, -1] = 原始波形
                bool needsFbank = dims.Length == 3 && (dims[2] == FBANK_N_MELS || dims[2] == 80);

                if (needsFbank)
                {
                    // 计算 Fbank 特征
                    var fbank = ComputeFbank(samples);
                    int numFrames = fbank.Length / FBANK_N_MELS;
                    _logDebug($"Fbank 特征: {numFrames} 帧 x {FBANK_N_MELS} 维");

                    var shape = new int[] { 1, numFrames, FBANK_N_MELS };
                    inputTensor = new DenseTensor<float>(new ReadOnlySpan<int>(shape));
                    for (int i = 0; i < fbank.Length; i++)
                        inputTensor.SetValue(i, fbank[i]);
                }
                else
                {
                    // 原始波形输入
                    var shape = dims.Select(d => (int)d).ToArray();
                    if (shape[0] == -1) shape[0] = 1;
                    if (shape.Length > 1 && shape[1] == -1) shape[1] = sampleCount;
                    if (shape.Length > 2 && shape[2] == -1) shape[2] = sampleCount;

                    _logDebug($"波形输入形状: [{string.Join(", ", shape)}]");
                    inputTensor = new DenseTensor<float>(new ReadOnlySpan<int>(shape));
                    for (int i = 0; i < samples.Length && i < inputTensor.Length; i++)
                        inputTensor.SetValue(i, samples[i]);
                }

                var inputs = new List<NamedOnnxValue>
                {
                    NamedOnnxValue.CreateFromTensor(_inputName, inputTensor)
                };

                using (var results = _session.Run(inputs))
                {
                    var output = results.First();
                    var embedding = output.AsTensor<float>().ToArray();
                    _logDebug($"嵌入维度: {embedding.Length}, 前5值: [{string.Join(", ", embedding.Take(5).Select(v => v.ToString("F4")))}]");
                    return NormalizeEmbedding(embedding);
                }
            }
            catch (Exception ex)
            {
                _logInfo($"提取声纹特征失败: {ex.Message}");
                _logDebug($"详情: {ex}");
                throw;
            }
        }

        /// <summary>
        /// 计算 Fbank 特征 (80 维 log mel filterbank)
        /// </summary>
        private float[] ComputeFbank(float[] audio)
        {
            int numFrames = (audio.Length - FBANK_WIN_SIZE) / FBANK_HOP_SIZE + 1;
            if (numFrames <= 0) numFrames = 1;

            int nFreqs = FBANK_FFT_SIZE / 2 + 1;
            var fbank = new float[numFrames * FBANK_N_MELS];

            // Hamming 窗
            var window = new float[FBANK_WIN_SIZE];
            for (int i = 0; i < FBANK_WIN_SIZE; i++)
                window[i] = 0.54f - 0.46f * (float)Math.Cos(2 * Math.PI * i / (FBANK_WIN_SIZE - 1));

            for (int frame = 0; frame < numFrames; frame++)
            {
                int start = frame * FBANK_HOP_SIZE;

                // 加窗 + 零填充到 FFT_SIZE
                var windowed = new float[FBANK_FFT_SIZE];
                for (int i = 0; i < FBANK_WIN_SIZE; i++)
                {
                    int idx = start + i;
                    windowed[i] = (idx < audio.Length ? audio[idx] : 0) * window[i];
                }

                // DFT → 功率谱
                var powerSpec = new float[nFreqs];
                for (int k = 0; k < nFreqs; k++)
                {
                    float real = 0, imag = 0;
                    for (int n = 0; n < FBANK_FFT_SIZE; n++)
                    {
                        float angle = -2.0f * (float)Math.PI * k * n / FBANK_FFT_SIZE;
                        real += windowed[n] * (float)Math.Cos(angle);
                        imag += windowed[n] * (float)Math.Sin(angle);
                    }
                    powerSpec[k] = real * real + imag * imag;
                }

                // 应用 Mel 滤波器 → log
                for (int m = 0; m < FBANK_N_MELS; m++)
                {
                    float val = 0;
                    for (int k = 0; k < nFreqs; k++)
                        val += _melFilters[m, k] * powerSpec[k];

                    fbank[frame * FBANK_N_MELS + m] = (float)Math.Log(Math.Max(val, 1e-10f));
                }
            }

            // CMVN (减均值)
            for (int m = 0; m < FBANK_N_MELS; m++)
            {
                float mean = 0;
                for (int f = 0; f < numFrames; f++)
                    mean += fbank[f * FBANK_N_MELS + m];
                mean /= numFrames;

                for (int f = 0; f < numFrames; f++)
                    fbank[f * FBANK_N_MELS + m] -= mean;
            }

            return fbank;
        }

        /// <summary>
        /// 创建 Mel 滤波器组
        /// </summary>
        private static float[,] CreateMelFilterbank(int nFft, int nMels, int sampleRate)
        {
            int nFreqs = nFft / 2 + 1;
            var filters = new float[nMels, nFreqs];

            float melMin = 2595.0f * (float)Math.Log10(1 + 0f / 700.0f);
            float melMax = 2595.0f * (float)Math.Log10(1 + sampleRate / 2.0f / 700.0f);

            var melPoints = new float[nMels + 2];
            for (int i = 0; i < nMels + 2; i++)
                melPoints[i] = melMin + (melMax - melMin) * i / (nMels + 1);

            var binPoints = new float[nMels + 2];
            for (int i = 0; i < nMels + 2; i++)
            {
                float hz = 700.0f * ((float)Math.Pow(10, melPoints[i] / 2595.0f) - 1);
                binPoints[i] = hz * nFft / sampleRate;
            }

            for (int m = 0; m < nMels; m++)
            {
                float left = binPoints[m];
                float center = binPoints[m + 1];
                float right = binPoints[m + 2];

                for (int k = 0; k < nFreqs; k++)
                {
                    if (k >= left && k <= center && center > left)
                        filters[m, k] = (k - left) / (center - left);
                    else if (k > center && k <= right && right > center)
                        filters[m, k] = (right - k) / (right - center);
                }
            }

            return filters;
        }

        /// <summary>
        /// 归一化特征向量（L2 归一化）
        /// </summary>
        private float[] NormalizeEmbedding(float[] embedding)
        {
            float norm = (float)Math.Sqrt(embedding.Sum(x => x * x));
            if (norm > 0)
            {
                for (int i = 0; i < embedding.Length; i++)
                {
                    embedding[i] /= norm;
                }
            }
            return embedding;
        }

        /// <summary>
        /// 计算两个特征向量的余弦相似度
        /// </summary>
        public float ComputeSimilarity(float[] embedding1, float[] embedding2)
        {
            if (embedding1.Length != embedding2.Length)
                throw new ArgumentException("特征向量维度不匹配");

            float dotProduct = 0;
            for (int i = 0; i < embedding1.Length; i++)
            {
                dotProduct += embedding1[i] * embedding2[i];
            }

            // 由于已经 L2 归一化，点积即为余弦相似度
            return dotProduct;
        }

        /// <summary>
        /// 验证声纹
        /// </summary>
        public async Task<VoiceprintVerificationResult> VerifyAsync(byte[] audioData)
        {
            return await Task.Run(() => Verify(audioData));
        }

        /// <summary>
        /// 验证声纹（同步版本）
        /// </summary>
        public VoiceprintVerificationResult Verify(byte[] audioData)
        {
            try
            {
                if (_registeredVoiceprints.Count == 0)
                {
                    return new VoiceprintVerificationResult
                    {
                        IsVerified = false,
                        Confidence = 0,
                        Error = "没有注册的声纹"
                    };
                }

                // 提取当前音频的声纹特征
                var currentEmbedding = ExtractEmbedding(audioData);

                // 与所有注册的声纹比较
                float maxSimilarity = float.MinValue;
                string matchedUserId = null;

                foreach (var registered in _registeredVoiceprints)
                {
                    var similarity = ComputeSimilarity(currentEmbedding, registered.Embedding);

                    if (similarity > maxSimilarity)
                    {
                        maxSimilarity = similarity;
                        matchedUserId = registered.UserId;
                    }
                }

                // 判断是否通过阈值
                bool isVerified = maxSimilarity >= _settings.VoiceprintThreshold;

                return new VoiceprintVerificationResult
                {
                    IsVerified = isVerified,
                    Confidence = Math.Max(0, Math.Min(1, (maxSimilarity + 1) / 2)), // 转换到 [0, 1]
                    MatchedUserId = isVerified ? matchedUserId : null
                };
            }
            catch (Exception ex)
            {
                return new VoiceprintVerificationResult
                {
                    IsVerified = false,
                    Confidence = 0,
                    Error = ex.Message
                };
            }
        }

        /// <summary>
        /// 注册新声纹
        /// </summary>
        public async Task<bool> RegisterVoiceprintAsync(string userId, string userName, byte[] audioData)
        {
            return await Task.Run(() => RegisterVoiceprint(userId, userName, audioData));
        }

        /// <summary>
        /// 注册新声纹（同步版本）
        /// </summary>
        public bool RegisterVoiceprint(string userId, string userName, byte[] audioData)
        {
            try
            {
                _logDebug($"开始提取声纹: {userName}, 音频 {audioData.Length} 字节");
                var embedding = ExtractEmbedding(audioData);
                _logDebug($"声纹特征提取完成, 维度: {embedding.Length}");

                var voiceprint = new VoiceprintEmbedding
                {
                    UserId = userId,
                    UserName = userName,
                    Embedding = embedding,
                    CreatedAt = DateTime.Now
                };

                _registeredVoiceprints.RemoveAll(v => v.UserId == userId);
                _registeredVoiceprints.Add(voiceprint);

                SaveRegisteredVoiceprints();

                _logInfo($"声纹注册完成: {userName} ({userId}), 特征维度: {embedding.Length}");
                return true;
            }
            catch (Exception ex)
            {
                _logInfo($"注册失败: {ex.Message}");
                _logDebug($"详情: {ex}");
                return false;
            }
        }

        /// <summary>
        /// 多次录音注册声纹（平均多个样本的嵌入向量以提高准确性）
        /// </summary>
        public bool RegisterVoiceprintMultiSample(string userId, string userName, List<byte[]> audioSamples, List<WakeWordEnvelope> wakeWordEnvelopes = null)
        {
            try
            {
                if (audioSamples == null || audioSamples.Count == 0)
                    throw new ArgumentException("没有音频样本");

                _logInfo($"多样本注册: {userName}, {audioSamples.Count} 个样本");

                // 提取每个样本的嵌入向量
                var embeddings = new List<float[]>();
                for (int i = 0; i < audioSamples.Count; i++)
                {
                    _logDebug($"提取样本 {i + 1}/{audioSamples.Count} 的声纹特征...");
                    var emb = ExtractEmbedding(audioSamples[i]);
                    embeddings.Add(emb);
                }

                // 验证样本间一致性（两两相似度）
                for (int i = 0; i < embeddings.Count; i++)
                {
                    for (int j = i + 1; j < embeddings.Count; j++)
                    {
                        float sim = ComputeSimilarity(embeddings[i], embeddings[j]);
                        _logDebug($"样本 {i + 1} vs {j + 1} 相似度: {sim:F3}");
                    }
                }

                // 求平均嵌入
                int dim = embeddings[0].Length;
                var avgEmbedding = new float[dim];
                foreach (var emb in embeddings)
                {
                    for (int d = 0; d < dim; d++)
                        avgEmbedding[d] += emb[d];
                }
                for (int d = 0; d < dim; d++)
                    avgEmbedding[d] /= embeddings.Count;

                // L2 归一化
                avgEmbedding = NormalizeEmbedding(avgEmbedding);

                var voiceprint = new VoiceprintEmbedding
                {
                    UserId = userId,
                    UserName = userName,
                    Embedding = avgEmbedding,
                    WakeWordEnvelopes = wakeWordEnvelopes ?? new List<WakeWordEnvelope>(),
                    CreatedAt = DateTime.Now
                };

                if (voiceprint.WakeWordEnvelopes.Count > 0)
                {
                    var conditionCounts = voiceprint.WakeWordEnvelopes
                        .GroupBy(e => e.Condition ?? "unknown")
                        .Select(g => $"{g.Key}×{g.Count()}");
                    _logInfo($"唤醒词能量包络: {voiceprint.WakeWordEnvelopes.Count} 个 ({string.Join(", ", conditionCounts)})");
                }

                _registeredVoiceprints.RemoveAll(v => v.UserId == userId);
                _registeredVoiceprints.Add(voiceprint);
                SaveRegisteredVoiceprints();

                _logInfo($"多样本声纹注册完成: {userName} ({userId}), {embeddings.Count} 个样本平均, 维度: {dim}");
                return true;
            }
            catch (Exception ex)
            {
                _logInfo($"多样本注册失败: {ex.Message}");
                _logDebug($"详情: {ex}");
                return false;
            }
        }

        /// <summary>
        /// 删除声纹
        /// </summary>
        public bool RemoveVoiceprint(string userId)
        {
            int removed = _registeredVoiceprints.RemoveAll(v => v.UserId == userId);
            if (removed > 0)
            {
                SaveRegisteredVoiceprints();
                return true;
            }
            return false;
        }

        /// <summary>
        /// 获取所有注册的声纹
        /// </summary>
        public List<VoiceprintEmbedding> GetRegisteredVoiceprints()
        {
            return _registeredVoiceprints.ToList();
        }

        /// <summary>
        /// 加载已注册的声纹
        /// </summary>
        private void LoadRegisteredVoiceprints()
        {
            try
            {
                if (File.Exists(_voiceprintDataPath))
                {
                    var json = File.ReadAllText(_voiceprintDataPath);
                    var voiceprints = System.Text.Json.JsonSerializer.Deserialize<List<VoiceprintEmbedding>>(json);
                    if (voiceprints != null)
                    {
                        _registeredVoiceprints.AddRange(voiceprints);
                        _logInfo($"加载了 {voiceprints.Count} 个已注册声纹");
                    }
                }
            }
            catch (Exception ex)
            {
                _logInfo($"加载声纹数据失败: {ex.Message}");
            }
        }

        /// <summary>
        /// 保存注册的声纹
        /// </summary>
        private void SaveRegisteredVoiceprints()
        {
            try
            {
                var dir = Path.GetDirectoryName(_voiceprintDataPath);
                if (!Directory.Exists(dir))
                    Directory.CreateDirectory(dir);

                var json = System.Text.Json.JsonSerializer.Serialize(_registeredVoiceprints,
                    new System.Text.Json.JsonSerializerOptions { WriteIndented = true });
                File.WriteAllText(_voiceprintDataPath, json);
            }
            catch (Exception ex)
            {
                _logInfo($"保存声纹数据失败: {ex.Message}");
            }
        }

        public void Dispose()
        {
            if (!_disposed)
            {
                _session?.Dispose();
                _disposed = true;
            }
        }
    }
}
