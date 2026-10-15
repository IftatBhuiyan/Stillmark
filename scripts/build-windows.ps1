[CmdletBinding()]
param([string]$OutputDirectory = "dist")
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
Set-Location (Join-Path $PSScriptRoot '..')
$output = [IO.Path]::GetFullPath($OutputDirectory)
$publish = Join-Path $output 'Stillmark-Windows-x64'
Push-Location app
try {
    dotnet restore src/WinShot.App/WinShot.App.csproj --runtime win-x64 -p:Platform=x64 --locked-mode
    if ($LASTEXITCODE -ne 0) { throw 'Locked restore failed.' }
    dotnet publish src/WinShot.App/WinShot.App.csproj --configuration Release --runtime win-x64 --self-contained true -p:Platform=x64 --no-restore --output $publish
    if ($LASTEXITCODE -ne 0) { throw 'Windows publish failed.' }
} finally { Pop-Location }
Copy-Item LICENSE (Join-Path $publish 'LICENSE.txt')
Copy-Item app/THIRD-PARTY-NOTICES.md (Join-Path $publish 'THIRD-PARTY-NOTICES.md')
$archive = Join-Path $output 'Stillmark-Windows-x64.zip'
Compress-Archive -Path $publish -DestinationPath $archive -Force
$hash = (Get-FileHash $archive -Algorithm SHA256).Hash.ToLowerInvariant()
"$hash  Stillmark-Windows-x64.zip" | Set-Content -Encoding ascii "$archive.sha256"
Write-Output "Built $archive (portable, unsigned)"
