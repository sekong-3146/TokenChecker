$ErrorActionPreference = 'Stop'

dotnet publish src/TokenChecker.Tray/TokenChecker.Tray.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true -o artifacts/publish

$publishedExePath = Join-Path $PSScriptRoot 'artifacts\publish\TokenChecker.Tray.exe'
Write-Host "게시된 실행 파일: $publishedExePath"
