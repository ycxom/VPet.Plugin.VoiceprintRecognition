# 训练数据：MiMo TTS v2.5 批量合成

基于小米 [MiMo 语音合成 v2.5](https://mimo.mi.com/docs/zh-CN/quick-start/usage-guide/audio/speech-synthesis-v2.5) 生成 **通用唤醒词训练音频**（正/负样本），供 openWakeWord 等 KWS 训练使用。

目录位置：仓库根下的 `training/`（不是系统盘符根目录）。

## 目录结构

```
training/
  .venv/                 # 本地虚拟环境（setup 后生成）
  .env                   # MIMO_API_KEY（勿提交）
  .env.example
  requirements.txt
  config/
    wakeword_nihao_luolisi.yaml
  scripts/
    setup_venv.ps1
    setup_venv.sh
    generate_mimo_tts.py
  data/
    positive/            # 唤醒词正样本 wav (16k mono)
    negative/            # 负样本 wav
    meta/manifest.jsonl  # 生成清单
```

## 1. 创建 venv 并安装依赖

PowerShell：

```powershell
cd training
.\scripts\setup_venv.ps1
.\.venv\Scripts\Activate.ps1
```

或手动：

```powershell
cd training
python -m venv .venv
.\.venv\Scripts\python.exe -m pip install -U pip
.\.venv\Scripts\python.exe -m pip install -r requirements.txt
copy .env.example .env
```

## 2. 配置 API Key

1. 在 [MiMo 开放平台](https://mimo.mi.com/) 获取 API Key（见文档「首次调用 API」）。
2. 编辑 `training/.env`：

```env
MIMO_API_KEY=你的密钥
# MIMO_BASE_URL=https://api.xiaomimimo.com/v1
# MIMO_TTS_MODEL=mimo-v2.5-tts
```

## 3. API 约定（与官方文档一致）

| 项 | 值 |
|----|-----|
| base_url | `https://api.xiaomimimo.com/v1` |
| model | `mimo-v2.5-tts`（预置音色） |
| 合成文本 | `messages` 里 **role=assistant** |
| 风格指令 | **role=user**（自然语言） |
| 音色 | `audio.voice`：冰糖 / 茉莉 / 苏打 / 白桦 / mimo_default … |
| 格式 | `audio.format=wav` |

也可用标签风格：assistant 内容写成 `(活泼)你好萝莉丝`。

## 4. 生成数据

先 dry-run 看任务量：

```powershell
python scripts\generate_mimo_tts.py --dry-run
```

小批量试跑：

```powershell
python scripts\generate_mimo_tts.py --limit 8
```

按配置全量生成：

```powershell
python scripts\generate_mimo_tts.py
```

自定义配置：

```powershell
python scripts\generate_mimo_tts.py --config config\wakeword_nihao_luolisi.yaml
```

输出：

- `data/positive/*.wav` — 16 kHz / mono / PCM16  
- `data/negative/*.wav`  
- `data/meta/manifest.jsonl` — 每行一条 JSON（文本、音色、风格、路径、时长）

## 5. 配置说明（YAML）

编辑 `config/wakeword_nihao_luolisi.yaml`：

- `positive_texts` / `negative_texts`：正负句  
- `voices`：多音色（通用模型务必多样）  
- `style_prompts`：user 侧自然语言风格  
- `tag_styles`：assistant 侧 `(风格)` 标签  
- `max_positive` / `max_negative`：上限，防止费用爆炸  
- `sleep_between_requests_sec`：限速  

组合数约：

```
texts × voices × style_prompts × tag_styles × samples_per_* 
```

再被 `max_*` 截断。

## 6. 费用与合规

- TTS 按平台计费，先 `--limit` 试跑。  
- 确认 MiMo / 小米 API 条款是否允许用合成音训练并分发模型。  
- **不要把 `.env`、大量 wav 误提交到 git**（见下方 gitignore 建议）。

## 7. 接到 openWakeWord 训练

1. 用 `data/positive` 作唤醒词正样本，`data/negative` 作负样本。  
2. 按 openWakeWord 官方 Colab/训练脚本导出 `nihao_luolisi.onnx`。  
3. 拷贝到：

```
1103_VoiceprintRecognition/models/openwakeword/nihao_luolisi.onnx
```

（需同时保留 `melspectrogram.onnx`、`embedding_model.onnx`）

4. 插件唤醒模式选 **openWakeWord ONNX**。

更稳的通用模型仍建议：多音色 + 噪声增强（可在训练脚本侧再做 SpecAug/加噪）。

## 8. 常见问题

| 现象 | 处理 |
|------|------|
| `MIMO_API_KEY` missing | 检查 `training/.env` |
| `message.audio` missing | 确认 model 为 `mimo-v2.5-tts`，assistant 文本非空 |
| 401/403 | Key 无效或无 TTS 权限 |
| 中文发音怪 | 换 `冰糖/茉莉/苏打/白桦`，或加强 style_prompt |
| 任务太多 | 降低 `max_positive` 或减少 voices/styles |

## 参考

- [语音合成 MiMo-V2.5-TTS](https://mimo.mi.com/docs/zh-CN/quick-start/usage-guide/audio/speech-synthesis-v2.5)


## 请求限速（保护 API Key）

脚本内置客户端限速，避免短时间打爆配额导致 Key 被限流：

| 配置项 | 默认 | 含义 |
|--------|------|------|
| `min_interval_sec` | 1.5 | 两次请求最小间隔（秒） |
| `requests_per_minute` | 20 | 任意 60 秒窗口最多请求数 |
| `max_requests_per_hour` | 600 | 每小时上限，`0` 关闭 |
| `rate_limit_backoff_base_sec` | 10 | 遇到 429 时基础退避 |
| `rate_limit_backoff_max_sec` | 120 | 单次退避上限 |
| `stop_on_repeated_rate_limit` | true | 连续限流 N 次后停止（已生成文件保留） |
| `max_consecutive_rate_limits` | 5 | 连续限流中止阈值 |

命令行覆盖：

```powershell
# 更保守：每分钟最多 10 次，间隔至少 3 秒
python scripts\generate_mimo_tts.py --rpm 10 --min-interval 3

# 每小时最多 200 次
python scripts\generate_mimo_tts.py --rph 200 --rpm 12 --min-interval 2
```

`--dry-run` 会打印限速参数与粗略 ETA。若日志出现 `RATE_LIMIT`，脚本会自动按 `Retry-After`/指数退避等待；连续多次则安全退出，可稍后从已有 wav 断点续跑（默认跳过已存在文件）。

## 本地训练 openWakeWord 分类头

训练器固定复用插件随附的 `melspectrogram.onnx` 与 `embedding_model.onnx`，只训练约 5 万参数的分类头。训练环境与 MiMo 数据生成环境分开：

```powershell
cd training
uv venv --python 3.11 .train-venv
uv pip install --python .\.train-venv\Scripts\python.exe -r requirements-train.txt

# 可选但推荐：使用本机 Microsoft Huihui 补齐短语和硬负样本，不调用网络 API
.\scripts\generate_windows_tts.ps1 -OutputDirectory data\windows_tts_v2

.\.train-venv\Scripts\python.exe scripts\train_openwakeword.py `
  --config config\train_nihao_luolisi.yaml
```

输出：

- `output/nihao_luolisi/nihao_luolisi.onnx`：训练产物
- `output/nihao_luolisi/metrics.json`：数据审计、拆分、指标、模型哈希和环境版本
- `output/nihao_luolisi/features.npz`：确定性增强后的特征缓存
- `../1103_VoiceprintRecognition/models/openwakeword/nihao_luolisi.onnx`：校验通过后自动部署的副本

固定接口为 `float32[batch,16,96] -> float32[batch,1]`，输出已包含 Sigmoid。验证集和测试集按 TTS 音色整体隔离，增强只应用于训练源文件。默认阈值先用 `0.5`；`metrics.json` 中的建议阈值只来自合成验证集，仍需用真实麦克风和数小时背景音频校准。

当前数据仍是实验性基线：MiMo 旧数据的正/负文本各只有一种，Windows TTS 虽补足了文本覆盖，但不能替代真实说话人、远场、噪声、混响和连续背景语音测试。
