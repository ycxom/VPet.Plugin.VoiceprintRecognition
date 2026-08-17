using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;

namespace VPet.Plugin.VoiceprintRecognition
{
    /// <summary>
    /// openWakeWord ONNX streaming engine (C# port).
    /// Pipeline: PCM16@16kHz -> melspectrogram.onnx -> embedding_model.onnx -> wakehead.onnx
    /// Ref: https://github.com/dscripka/openWakeWord (Apache-2.0)
    /// </summary>
    public sealed class OpenWakeWordEngine : IDisposable
    {
        public const int SampleRate = 16000;
        public const int FrameSamples = 1280;

        private readonly Action<string> _logInfo;
        private readonly Action<string> _logDebug;
        private readonly object _lock = new object();

        private InferenceSession _melspecSession;
        private InferenceSession _embeddingSession;
        private readonly Dictionary<string, InferenceSession> _wakeSessions = new Dictionary<string, InferenceSession>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, int> _wakeInputFrames = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, string> _wakeInputNames = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        private string _melspecInputName = "input";
        private string _embeddingInputName = "input_1";

        private readonly List<short> _rawBuffer = new List<short>(SampleRate * 10);
        private readonly List<float> _remainder = new List<float>();
        private float[,] _melspecBuffer;
        private int _melspecRows;
        private float[,] _featureBuffer;
        private int _featureRows;
        private int _accumulatedSamples;
        private int _predictWarmup;

        private const int MelBands = 32;
        private const int MelWindow = 76;
        private const int EmbeddingDim = 96;
        private const int MelMaxRows = 970;
        private const int FeatureMaxRows = 120;
        private const int RawMaxSamples = SampleRate * 10;

        public bool IsInitialized { get; private set; }
        public IReadOnlyList<string> LoadedModels => _wakeSessions.Keys.ToList();

        public OpenWakeWordEngine(Action<string> logInfo = null, Action<string> logDebug = null)
        {
            _logInfo = logInfo ?? (_ => { });
            _logDebug = logDebug ?? (_ => { });
        }

        public bool Initialize(string modelDirectory, IEnumerable<string> wakeModelFiles = null)
        {
            DisposeSessionsOnly();

            if (string.IsNullOrWhiteSpace(modelDirectory) || !Directory.Exists(modelDirectory))
            {
                _logInfo("openWakeWord model dir missing: " + modelDirectory);
                return false;
            }

            var melPath = Path.Combine(modelDirectory, "melspectrogram.onnx");
            var embPath = Path.Combine(modelDirectory, "embedding_model.onnx");
            if (!File.Exists(melPath) || !File.Exists(embPath))
            {
                _logInfo("openWakeWord needs melspectrogram.onnx and embedding_model.onnx");
                return false;
            }

            try
            {
                var opts = new SessionOptions();
                opts.InterOpNumThreads = 1;
                opts.IntraOpNumThreads = 1;
                opts.GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL;

                _melspecSession = new InferenceSession(melPath, opts);
                _embeddingSession = new InferenceSession(embPath, opts);
                _melspecInputName = _melspecSession.InputMetadata.Keys.First();
                _embeddingInputName = _embeddingSession.InputMetadata.Keys.First();

                var wakeFiles = new List<string>();
                if (wakeModelFiles != null)
                {
                    foreach (var f in wakeModelFiles)
                    {
                        if (string.IsNullOrWhiteSpace(f)) continue;
                        var p = Path.IsPathRooted(f) ? f : Path.Combine(modelDirectory, f);
                        if (File.Exists(p)) wakeFiles.Add(p);
                    }
                }

                if (wakeFiles.Count == 0)
                {
                    wakeFiles = Directory.GetFiles(modelDirectory, "*.onnx")
                        .Where(p =>
                        {
                            var n = Path.GetFileName(p).ToLowerInvariant();
                            return n != "melspectrogram.onnx" && n != "embedding_model.onnx";
                        }).ToList();
                }

                foreach (var path in wakeFiles)
                {
                    var name = Path.GetFileNameWithoutExtension(path);
                    var session = new InferenceSession(path, opts);
                    var inMeta = session.InputMetadata.First();
                    int nFrames = 16;
                    var dims = inMeta.Value.Dimensions;
                    if (dims != null && dims.Length >= 2 && dims[1] > 0)
                        nFrames = dims[1];

                    _wakeSessions[name] = session;
                    _wakeInputFrames[name] = nFrames;
                    _wakeInputNames[name] = inMeta.Key;
                    _logInfo($"openWakeWord loaded head: {name} (frames={nFrames})");
                }

                if (_wakeSessions.Count == 0)
                {
                    _logInfo("openWakeWord: no wake-word head onnx (train custom model e.g. nihao_luolisi.onnx)");
                    DisposeSessionsOnly();
                    return false;
                }

                ResetBuffers();
                IsInitialized = true;
                _logInfo("openWakeWord ready: " + string.Join(", ", _wakeSessions.Keys));
                return true;
            }
            catch (Exception ex)
            {
                _logInfo("openWakeWord init failed: " + ex.Message);
                _logDebug(ex.ToString());
                DisposeSessionsOnly();
                return false;
            }
        }

        public void Reset()
        {
            lock (_lock) { ResetBuffers(); }
        }

        public Dictionary<string, float> ProcessPcm16(byte[] pcmBytes)
        {
            if (!IsInitialized || pcmBytes == null || pcmBytes.Length < 2)
                return new Dictionary<string, float>();

            int sampleCount = pcmBytes.Length / 2;
            var samples = new float[sampleCount];
            for (int i = 0; i < sampleCount; i++)
            {
                short s = (short)(pcmBytes[i * 2] | (pcmBytes[i * 2 + 1] << 8));
                samples[i] = s;
            }
            return ProcessSamples(samples);
        }

        public bool TryDetect(byte[] pcmBytes, float threshold, out string modelName, out float score)
        {
            modelName = null;
            score = 0f;
            var scores = ProcessPcm16(pcmBytes);
            if (scores.Count == 0) return false;

            foreach (var kv in scores)
            {
                if (kv.Value > score)
                {
                    score = kv.Value;
                    modelName = kv.Key;
                }
            }
            return modelName != null && score >= threshold;
        }

        private Dictionary<string, float> ProcessSamples(float[] x)
        {
            lock (_lock)
            {
                var predictions = new Dictionary<string, float>(StringComparer.OrdinalIgnoreCase);
                if (x == null || x.Length == 0)
                    return predictions;

                if (_remainder.Count > 0)
                {
                    var merged = new float[_remainder.Count + x.Length];
                    _remainder.CopyTo(merged, 0);
                    Array.Copy(x, 0, merged, _remainder.Count, x.Length);
                    x = merged;
                    _remainder.Clear();
                }

                int processedSamples = 0;

                if (_accumulatedSamples + x.Length >= FrameSamples)
                {
                    int remainder = (_accumulatedSamples + x.Length) % FrameSamples;
                    if (remainder != 0)
                    {
                        int evenLen = x.Length - remainder;
                        BufferRaw(x, 0, evenLen);
                        _accumulatedSamples += evenLen;
                        for (int i = evenLen; i < x.Length; i++)
                            _remainder.Add(x[i]);
                    }
                    else
                    {
                        BufferRaw(x, 0, x.Length);
                        _accumulatedSamples += x.Length;
                    }
                }
                else
                {
                    BufferRaw(x, 0, x.Length);
                    _accumulatedSamples += x.Length;
                }

                if (_accumulatedSamples >= FrameSamples && _accumulatedSamples % FrameSamples == 0)
                {
                    StreamingMelspectrogram(_accumulatedSamples);

                    int frames = _accumulatedSamples / FrameSamples;
                    for (int i = frames - 1; i >= 0; i--)
                    {
                        int sliceEnd = (i == 0) ? _melspecRows : (_melspecRows - 8 * i);
                        int sliceStart = sliceEnd - MelWindow;
                        if (sliceStart < 0 || sliceEnd > _melspecRows || sliceEnd - sliceStart != MelWindow)
                            continue;

                        var emb = RunEmbedding(sliceStart, MelWindow);
                        if (emb != null)
                            AppendFeatureRow(emb);
                    }

                    processedSamples = _accumulatedSamples;
                    _accumulatedSamples = 0;
                }

                if (_featureRows > FeatureMaxRows)
                    TrimFeatureBuffer(FeatureMaxRows);

                if (processedSamples < FrameSamples)
                    return predictions;

                foreach (var name in _wakeSessions.Keys.ToList())
                {
                    int nFrames = _wakeInputFrames[name];
                    float score = 0f;
                    if (_featureRows >= nFrames)
                        score = RunWakeModel(name, nFrames);
                    if (_predictWarmup < 5)
                        score = 0f;
                    predictions[name] = score;
                }

                if (_predictWarmup < 5)
                    _predictWarmup++;

                return predictions;
            }
        }

        private void BufferRaw(float[] x, int offset, int count)
        {
            for (int i = 0; i < count; i++)
            {
                int v = (int)Math.Round(x[offset + i]);
                if (v > short.MaxValue) v = short.MaxValue;
                if (v < short.MinValue) v = short.MinValue;
                _rawBuffer.Add((short)v);
            }
            if (_rawBuffer.Count > RawMaxSamples)
                _rawBuffer.RemoveRange(0, _rawBuffer.Count - RawMaxSamples);
        }

        private void StreamingMelspectrogram(int nSamples)
        {
            int take = nSamples + 160 * 3;
            if (_rawBuffer.Count < 400) return;

            int start = Math.Max(0, _rawBuffer.Count - take);
            int len = _rawBuffer.Count - start;
            var input = new float[len];
            for (int i = 0; i < len; i++)
                input[i] = _rawBuffer[start + i];

            var mel = RunMelspec(input);
            if (mel == null) return;

            int newRows = mel.GetLength(0);
            EnsureMelCapacity(_melspecRows + newRows);
            for (int r = 0; r < newRows; r++)
                for (int c = 0; c < MelBands; c++)
                    _melspecBuffer[_melspecRows + r, c] = mel[r, c];
            _melspecRows += newRows;

            if (_melspecRows > MelMaxRows)
            {
                int keep = MelMaxRows;
                int drop = _melspecRows - keep;
                for (int r = 0; r < keep; r++)
                    for (int c = 0; c < MelBands; c++)
                        _melspecBuffer[r, c] = _melspecBuffer[r + drop, c];
                _melspecRows = keep;
            }
        }

        private float[,] RunMelspec(float[] samples)
        {
            var tensor = new DenseTensor<float>(new[] { 1, samples.Length });
            for (int i = 0; i < samples.Length; i++)
                tensor[0, i] = samples[i];

            var inputs = new List<NamedOnnxValue> { NamedOnnxValue.CreateFromTensor(_melspecInputName, tensor) };
            using (var results = _melspecSession.Run(inputs))
            {
                var output = results.First().AsTensor<float>();
                var dims = output.Dimensions.ToArray();
                var arr = output.ToArray();

                int frames = 0;
                if (dims.Length >= 2)
                {
                    foreach (var d in dims)
                    {
                        if (d > 1 && d != MelBands) { frames = d; break; }
                    }
                }
                if (frames <= 0) frames = arr.Length / MelBands;
                if (frames <= 0) return null;

                var mel = new float[frames, MelBands];
                int usable = Math.Min(frames, arr.Length / MelBands);
                for (int f = 0; f < usable; f++)
                    for (int b = 0; b < MelBands; b++)
                        mel[f, b] = arr[f * MelBands + b] / 10f + 2f;
                return mel;
            }
        }

        private float[] RunEmbedding(int melStartRow, int rows)
        {
            var tensor = new DenseTensor<float>(new[] { 1, rows, MelBands, 1 });
            for (int r = 0; r < rows; r++)
                for (int b = 0; b < MelBands; b++)
                    tensor[0, r, b, 0] = _melspecBuffer[melStartRow + r, b];

            var inputs = new List<NamedOnnxValue> { NamedOnnxValue.CreateFromTensor(_embeddingInputName, tensor) };
            using (var results = _embeddingSession.Run(inputs))
            {
                var output = results.First().AsTensor<float>().ToArray();
                if (output.Length < EmbeddingDim) return null;
                if (output.Length == EmbeddingDim) return output;
                var emb = new float[EmbeddingDim];
                Array.Copy(output, output.Length - EmbeddingDim, emb, 0, EmbeddingDim);
                return emb;
            }
        }

        private float RunWakeModel(string name, int nFrames)
        {
            var session = _wakeSessions[name];
            var tensor = new DenseTensor<float>(new[] { 1, nFrames, EmbeddingDim });
            int start = _featureRows - nFrames;
            for (int r = 0; r < nFrames; r++)
                for (int c = 0; c < EmbeddingDim; c++)
                    tensor[0, r, c] = _featureBuffer[start + r, c];

            var inputs = new List<NamedOnnxValue> { NamedOnnxValue.CreateFromTensor(_wakeInputNames[name], tensor) };
            using (var results = session.Run(inputs))
            {
                var arr = results.First().AsTensor<float>().ToArray();
                return arr.Length > 0 ? arr[0] : 0f;
            }
        }

        private void AppendFeatureRow(float[] emb)
        {
            EnsureFeatureCapacity(_featureRows + 1);
            for (int c = 0; c < EmbeddingDim; c++)
                _featureBuffer[_featureRows, c] = emb[c];
            _featureRows++;
        }

        private void EnsureMelCapacity(int rows)
        {
            if (_melspecBuffer != null && _melspecBuffer.GetLength(0) >= rows) return;
            int cap = Math.Max(rows, MelMaxRows + 32);
            var nb = new float[cap, MelBands];
            if (_melspecBuffer != null)
            {
                for (int r = 0; r < _melspecRows; r++)
                    for (int c = 0; c < MelBands; c++)
                        nb[r, c] = _melspecBuffer[r, c];
            }
            _melspecBuffer = nb;
        }

        private void EnsureFeatureCapacity(int rows)
        {
            if (_featureBuffer != null && _featureBuffer.GetLength(0) >= rows) return;
            int cap = Math.Max(rows, FeatureMaxRows + 16);
            var nb = new float[cap, EmbeddingDim];
            if (_featureBuffer != null)
            {
                for (int r = 0; r < _featureRows; r++)
                    for (int c = 0; c < EmbeddingDim; c++)
                        nb[r, c] = _featureBuffer[r, c];
            }
            _featureBuffer = nb;
        }

        private void TrimFeatureBuffer(int keep)
        {
            if (_featureRows <= keep) return;
            int drop = _featureRows - keep;
            for (int r = 0; r < keep; r++)
                for (int c = 0; c < EmbeddingDim; c++)
                    _featureBuffer[r, c] = _featureBuffer[r + drop, c];
            _featureRows = keep;
        }

        private void ResetBuffers()
        {
            _rawBuffer.Clear();
            _remainder.Clear();
            _accumulatedSamples = 0;
            _predictWarmup = 0;

            _melspecBuffer = new float[MelWindow + 8, MelBands];
            _melspecRows = MelWindow;
            for (int r = 0; r < MelWindow; r++)
                for (int c = 0; c < MelBands; c++)
                    _melspecBuffer[r, c] = 1f;

            _featureBuffer = new float[32, EmbeddingDim];
            _featureRows = 16;
        }

        private void DisposeSessionsOnly()
        {
            IsInitialized = false;
            foreach (var s in _wakeSessions.Values) s.Dispose();
            _wakeSessions.Clear();
            _wakeInputFrames.Clear();
            _wakeInputNames.Clear();
            _melspecSession?.Dispose();
            _embeddingSession?.Dispose();
            _melspecSession = null;
            _embeddingSession = null;
        }

        public void Dispose()
        {
            lock (_lock) { DisposeSessionsOnly(); }
        }
    }
}
