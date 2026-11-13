param([string]$UvPath = 'uv')
$ErrorActionPreference = 'Stop'
$runtime = Join-Path $env:LOCALAPPDATA 'SubTerra/SceneAi'
New-Item -ItemType Directory -Force -Path $runtime | Out-Null
if (-not (Get-Command $UvPath -ErrorAction SilentlyContinue)) {
    $archive = Join-Path $runtime 'uv-0.13.7.zip'
    Invoke-WebRequest -Uri 'https://github.com/astral-sh/uv/releases/download/0.13.7/uv-x86_64-pc-windows-msvc.zip' -OutFile $archive
    if ((Get-FileHash -LiteralPath $archive -Algorithm SHA256).Hash -ne '0CDC5B38FE5A8168ED6A25814F5BA43C21D313DD3187314FCCB37B7CEC46E021') {
        throw 'The uv download failed its checksum check.'
    }
    $toolsDirectory = Join-Path $runtime 'tools'
    Expand-Archive -LiteralPath $archive -DestinationPath $toolsDirectory -Force
    $UvPath = Join-Path $toolsDirectory 'uv.exe'
}
$env:UV_PYTHON_INSTALL_DIR = Join-Path $runtime 'python'
$env:UV_CACHE_DIR = Join-Path $runtime 'cache'
$env:HF_HOME = Join-Path $runtime 'models'
$env:PYTHONUTF8 = '1'
$env:HF_HUB_OFFLINE = '0'
$python = Join-Path $runtime '.venv/Scripts/python.exe'
& $UvPath venv --python 3.12 --allow-existing (Join-Path $runtime '.venv')
if ($LASTEXITCODE -ne 0) { throw 'Unable to create the Python environment.' }
& $UvPath pip install --python $python torch==2.10.0 --index-url https://download.pytorch.org/whl/cu128
if ($LASTEXITCODE -ne 0) { throw 'Unable to install PyTorch.' }
& $UvPath pip install --python $python point-transformer-v3==0.1.2 numpy==2.5.4 scipy==1.18.1 huggingface-hub==2.4.0
if ($LASTEXITCODE -ne 0) { throw 'Unable to install the scene model dependencies.' }
$worker = Join-Path $PSScriptRoot '../SubTerra/Assets/SceneAi/segment.py'
& $python $worker --install (Join-Path $runtime 'ready.json')
if ($LASTEXITCODE -ne 0) { throw 'Unable to download and verify the model.' }
Write-Output 'Local scene model is ready. Restart SubTerra to use it.'
