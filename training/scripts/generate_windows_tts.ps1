param(
    [string]$Config = "config\wakeword_nihao_luolisi.yaml",
    [string]$OutputDirectory = "data\windows_tts"
)

$ErrorActionPreference = "Stop"
[Console]::OutputEncoding = [Text.UTF8Encoding]::new($false)
$env:PYTHONIOENCODING = "utf-8"
$trainingRoot = Split-Path -Parent $PSScriptRoot
$configPath = if ([IO.Path]::IsPathRooted($Config)) { $Config } else { Join-Path $trainingRoot $Config }
$outputRoot = if ([IO.Path]::IsPathRooted($OutputDirectory)) { $OutputDirectory } else { Join-Path $trainingRoot $OutputDirectory }
$python = Join-Path $trainingRoot ".train-venv\Scripts\python.exe"
if (-not (Test-Path -LiteralPath $python)) {
    $python = Join-Path $trainingRoot ".venv\Scripts\python.exe"
}
if (-not (Test-Path -LiteralPath $python)) {
    throw "Python environment not found under training/.train-venv or training/.venv"
}

$configJson = & $python -c "import json,sys,yaml; print(json.dumps(yaml.safe_load(open(sys.argv[1], encoding='utf-8')), ensure_ascii=False))" $configPath
if ($LASTEXITCODE -ne 0) { throw "Failed to read YAML config" }
$configData = $configJson | ConvertFrom-Json

$voice = New-Object -ComObject SAPI.SpVoice
$tokens = $voice.GetVoices()
$voiceToken = $null
for ($index = 0; $index -lt $tokens.Count; $index++) {
    $candidate = $tokens.Item($index)
    if ($candidate.GetDescription() -like "*Chinese*") {
        $voiceToken = $candidate
        break
    }
}
if ($null -eq $voiceToken) { throw "No Chinese SAPI voice is installed" }
$voice.Voice = $voiceToken
$voiceName = $voiceToken.GetDescription()

$positiveDirectory = Join-Path $outputRoot "positive"
$negativeDirectory = Join-Path $outputRoot "negative"
New-Item -ItemType Directory -Force -Path $positiveDirectory, $negativeDirectory | Out-Null
$manifestPath = Join-Path $outputRoot "manifest.jsonl"
$rates = @(-4, -2, 0, 2, 4)
$volumes = @(70, 100)

$jobs = @()
foreach ($entry in @(@{ Split = "positive"; Label = 1; Texts = $configData.positive_texts }, @{ Split = "negative"; Label = 0; Texts = $configData.negative_texts })) {
    foreach ($text in $entry.Texts) {
        foreach ($rate in $rates) {
            foreach ($volume in $volumes) {
                $hashInput = "$($entry.Split)|$text|$voiceName|$rate|$volume"
                $hashBytes = [Text.Encoding]::UTF8.GetBytes($hashInput)
                $sha = [Security.Cryptography.SHA256]::Create()
                try { $hash = ([BitConverter]::ToString($sha.ComputeHash($hashBytes))).Replace("-", "").Substring(0, 12).ToLowerInvariant() }
                finally { $sha.Dispose() }
                $fileName = "$($entry.Split)_sapi_$hash.wav"
                $directory = if ($entry.Label -eq 1) { $positiveDirectory } else { $negativeDirectory }
                $jobs += [pscustomobject]@{
                    Split = $entry.Split
                    Label = $entry.Label
                    Text = [string]$text
                    Rate = $rate
                    Volume = $volume
                    Path = Join-Path $directory $fileName
                }
            }
        }
    }
}

$manifestRows = [Collections.Generic.List[string]]::new()
foreach ($job in $jobs) {
    if (-not (Test-Path -LiteralPath $job.Path) -or (Get-Item -LiteralPath $job.Path).Length -le 100) {
        $stream = New-Object -ComObject SAPI.SpFileStream
        $format = New-Object -ComObject SAPI.SpAudioFormat
        try {
            $format.Type = 18 # SAFT16kHz16BitMono
            $stream.Format = $format
            $stream.Open($job.Path, 3, $false) # SSFMCreateForWrite
            $voice.AudioOutputStream = $stream
            $voice.Rate = $job.Rate
            $voice.Volume = $job.Volume
            [void]$voice.Speak($job.Text)
            $stream.Close()
        }
        finally {
            try { $stream.Close() } catch { }
            try { $voice.AudioOutputStream = $null } catch { }
            [Runtime.InteropServices.Marshal]::FinalReleaseComObject($format) | Out-Null
            [Runtime.InteropServices.Marshal]::FinalReleaseComObject($stream) | Out-Null
        }
    }
    $trainingPrefix = [IO.Path]::GetFullPath($trainingRoot).TrimEnd("\") + "\"
    $fullJobPath = [IO.Path]::GetFullPath($job.Path)
    $relativePath = if ($fullJobPath.StartsWith($trainingPrefix, [StringComparison]::OrdinalIgnoreCase)) {
        $fullJobPath.Substring($trainingPrefix.Length).Replace("\", "/")
    }
    else {
        $fullJobPath
    }
    $row = [ordered]@{
        ok = $true
        split = $job.Split
        label = $job.Label
        text = $job.Text
        voice = $voiceName
        rate = $job.Rate
        volume = $job.Volume
        path = $relativePath
        engine = "Windows SAPI"
    }
    $manifestRows.Add(($row | ConvertTo-Json -Compress))
}

[IO.File]::WriteAllLines($manifestPath, $manifestRows, [Text.UTF8Encoding]::new($false))
[Runtime.InteropServices.Marshal]::FinalReleaseComObject($voice) | Out-Null
Write-Output "Generated $($jobs.Count) Windows TTS clips with '$voiceName'"
Write-Output "Manifest: $manifestPath"
