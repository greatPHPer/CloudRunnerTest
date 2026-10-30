$ErrorActionPreference = 'Stop'

$installRoot = Join-Path $env:LOCALAPPDATA 'CloudRunner\Streaming'
$noVncVersion = '1.7.0'
$websockifyVersion = '0.13.0'
$tempRoot = Join-Path $env:TEMP ('CloudRunner-Setup-' + [guid]::NewGuid().ToString('N'))
$zipPath = Join-Path $tempRoot 'noVNC.zip'
$extractPath = Join-Path $tempRoot 'extract'
$pythonPath = Join-Path $installRoot '.venv\Scripts\python.exe'

New-Item -ItemType Directory -Path $installRoot -Force | Out-Null
New-Item -ItemType Directory -Path $tempRoot -Force | Out-Null

try {
    Write-Host 'CloudRunner local streaming tools setup'
    Write-Host "Install folder: $installRoot"

    $pythonCommand = Get-Command 'py.exe' -ErrorAction SilentlyContinue
    if ($pythonCommand) {
        $pythonLauncher = $pythonCommand.Source
        Write-Host 'Creating a private Python virtual environment with the Python launcher...'
        & $pythonLauncher -3 -m venv (Join-Path $installRoot '.venv')
    }
    else {
        $pythonCommand = Get-Command 'python.exe' -ErrorAction SilentlyContinue
        if (-not $pythonCommand) {
            throw 'Python 3 was not found. Install Python 3 from https://www.python.org/downloads/windows/ and enable the Python launcher, then run this script again.'
        }
        $pythonExe = $pythonCommand.Source
        Write-Host 'Creating a private Python virtual environment...'
        & $pythonExe -m venv (Join-Path $installRoot '.venv')
    }

    if ($LASTEXITCODE -ne 0 -or -not (Test-Path -LiteralPath $pythonPath)) {
        throw 'Could not create the CloudRunner Python virtual environment.'
    }

    Write-Host "Installing websockify $websockifyVersion into the private environment..."
    & $pythonPath -m pip install --upgrade pip
    if ($LASTEXITCODE -ne 0) { throw 'Could not update pip.' }

    & $pythonPath -m pip install "websockify==$websockifyVersion"
    if ($LASTEXITCODE -ne 0) { throw 'Could not install websockify.' }

    Write-Host "Downloading noVNC $noVncVersion..."
    $downloadUrl = "https://github.com/novnc/noVNC/archive/refs/tags/v$noVncVersion.zip"
    Invoke-WebRequest -Uri $downloadUrl -OutFile $zipPath

    Expand-Archive -LiteralPath $zipPath -DestinationPath $extractPath -Force
    $sourceFolder = Join-Path $extractPath "noVNC-$noVncVersion"
    if (-not (Test-Path -LiteralPath (Join-Path $sourceFolder 'vnc.html'))) {
        throw 'The downloaded noVNC archive did not contain vnc.html.'
    }

    $noVncDestination = Join-Path $installRoot 'noVNC'
    if (Test-Path -LiteralPath $noVncDestination) {
        Remove-Item -LiteralPath $noVncDestination -Recurse -Force
    }
    New-Item -ItemType Directory -Path $noVncDestination -Force | Out-Null
    Copy-Item -Path (Join-Path $sourceFolder '*') -Destination $noVncDestination -Recurse -Force

    @(
        "noVNC=$noVncVersion"
        "websockify=$websockifyVersion"
        "InstalledUtc=$([DateTime]::UtcNow.ToString('o'))"
    ) | Set-Content -LiteralPath (Join-Path $installRoot 'versions.txt') -Encoding utf8

    Write-Host ''
    Write-Host 'Setup completed.'
    Write-Host "noVNC: $noVncDestination"
    Write-Host "Python: $pythonPath"
    Write-Host 'Next: install TightVNC 4, start the Server in application mode, set a password, and restrict incoming VNC access to loopback only.'
}
finally {
    if (Test-Path -LiteralPath $tempRoot) {
        Remove-Item -LiteralPath $tempRoot -Recurse -Force -ErrorAction SilentlyContinue
    }
}
