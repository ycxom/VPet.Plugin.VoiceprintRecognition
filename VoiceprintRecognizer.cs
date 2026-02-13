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
        /// 用户名称
        /// </summary>
        public string UserName { get; set; }

        /// <summary>
        /// 特征向量
        /// </summary>
        public float[] Embedding { get; set; }

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

        /// <summary>
        /// 模型输入名称
        /// </summary>
        private string _inputName;

        /// <summary>
        /// 模型输出名称
        /// </summary>
        private string _outputName;

        /// <summary>
        /// 特征向量维度
        /// </summary>
        public int EmbeddingDimension { get; private set; }

        public VoiceprintRecognizer(string modelPath, VoiceprintSettings settings)
        {
            _settings = settings;
            _registeredVoiceprints = new List<VoiceprintEmbedding>();

            // 设置声纹数据保存路径
            var modelDir = Path.GetDirectoryName(modelPath);
            _voiceprintDataPath = Path.Combine(Path.GetDirectoryName(modelDir), "data", "voiceprints.json");

            // 加载模型
            LoadModel(modelPath);

            // 加载已注册的声纹
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
                {
                    throw new FileNotFoundException($"模型文件不存在: {modelPath}");
                }

                // 创建会话选项
                var options = new SessionOptions();

                // 根据设置选择执行提供程序
                if (_settings.UseGPU)
                {
                    try
                    {
                        // 尝试使用 CUDA
                        options.AppendExecutionProvider_CUDA();
                        Console.WriteLine("[声纹识别] 使用 CUDA GPU 加速");
                    }
                    catch
                    {
                        try
                        {
                            // 尝试使用 DirectML (Windows)
                            options.AppendExecutionProvider_DML();
                            Console.WriteLine("[声纹识别] 使用 DirectML GPU 加速");
                        }
                        catch
                        {
                            Console.WriteLine("[声纹识别] GPU 不可用，使用 CPU");
                        }
                    }
                }

                // CPU 作为后备
                options.AppendExecutionProvider_CPU();

                // 设置线程数
                options.InterOpNumThreads = _settings.NumThreads;
                options.IntraOpNumThreads = _settings.NumThreads;

                // 创建推理会话
                _session = new InferenceSession(modelPath, options);

                // 获取输入输出信息
                _inputName = _session.InputMetadata.Keys.First();
                _outputName = _session.OutputMetadata.Keys.First();

                // 获取输出维度
                var outputMeta = _session.OutputMetadata[_outputName];
                EmbeddingDimension = (int)outputMeta.Dimensions.Last();

                Console.WriteLine($"[声纹识别] 模型加载成功");
                Console.WriteLine($"[声纹识别] 输入: {_inputName}, 输出: {_outputName}");
                Console.WriteLine($"[声纹识别] 特征维度: {EmbeddingDimension}");
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
                // 预处理音频数据
                var processedAudio = PreprocessAudio(audioData);

                // 创建输入张量
                // 假设模型输入格式为 [batch, samples] 或 [batch, channels, samples]
                var inputMeta = _session.InputMetadata[_inputName];
                var inputShapeLong = inputMeta.Dimensions.ToArray();
                var inputShape = inputShapeLong.Select(x => (int)x).ToArray();

                // 动态调整输入形状
                if (inputShape[0] == -1) inputShape[0] = 1; // batch size
                if (inputShape.Length > 2 && inputShape[2] == -1)
                    inputShape[2] = processedAudio.Length;
                else if (inputShape.Length == 2 && inputShape[1] == -1)
                    inputShape[1] = processedAudio.Length;

                // 创建张量并填充数据
                var inputTensor = new DenseTensor<float>(new ReadOnlySpan<int>(inputShape));
                for (int i = 0; i < processedAudio.Length && i < inputTensor.Length; i++)
                {
                    inputTensor.SetValue(i, processedAudio[i]);
                }

                // 创建输入
                var inputs = new List<NamedOnnxValue>
                {
                    NamedOnnxValue.CreateFromTensor(_inputName, inputTensor)
                };

                // 运行推理
                using (var results = _session.Run(inputs))
                {
                    var output = results.First();
                    var embedding = output.AsTensor<float>().ToArray();

                    // 归一化特征向量
                    return NormalizeEmbedding(embedding);
                }
            }
            catch (Exception ex)
            {
                throw new Exception($"提取声纹特征失败: {ex.Message}", ex);
            }
        }

        /// <summary>
        /// 预处理音频数据
        /// </summary>
        private float[] PreprocessAudio(byte[] audioData)
        {
            // 将字节数组转换为浮点数组（假设是 16-bit PCM）
            int sampleCount = audioData.Length / 2;
            float[] samples = new float[sampleCount];

            for (int i = 0; i < sampleCount; i++)
            {
                short sample = BitConverter.ToInt16(audioData, i * 2);
                samples[i] = sample / 32768.0f; // 归一化到 [-1, 1]
            }

            // 可以添加更多预处理：
            // - 重采样（如果需要）
            // - 静音检测和裁剪
            // - 添加预加重
            // - 提取 MFCC 或 mel 频谱（取决于模型需求）

            return samples;
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
                var embedding = ExtractEmbedding(audioData);

                var voiceprint = new VoiceprintEmbedding
                {
                    UserId = userId,
                    UserName = userName,
                    Embedding = embedding,
                    CreatedAt = DateTime.Now
                };

                // 移除同一用户的旧声纹
                _registeredVoiceprints.RemoveAll(v => v.UserId == userId);

                // 添加新声纹
                _registeredVoiceprints.Add(voiceprint);

                // 保存到文件
                SaveRegisteredVoiceprints();

                Console.WriteLine($"[声纹识别] 注册成功: {userName} ({userId})");
                return true;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[声纹识别] 注册失败: {ex.Message}");
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
                        Console.WriteLine($"[声纹识别] 加载了 {voiceprints.Count} 个已注册声纹");
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[声纹识别] 加载声纹数据失败: {ex.Message}");
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
                Console.WriteLine($"[声纹识别] 保存声纹数据失败: {ex.Message}");
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
