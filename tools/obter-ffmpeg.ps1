# Baixa o FFmpeg usado pela compressão de vídeo e prepara os recursos que são incorporados ao
# programa (PDFMaster.Video.Ffmpeg/runtime). Usa a compilação estática LGPL de BtbN/FFmpeg-Builds,
# que não contém bibliotecas exclusivamente GPL (como a libx264).
param([string]$Ramo = '9.0')
$ErrorActionPreference = 'Stop'

$destino = Join-Path $PSScriptRoot '..\PDFMaster.Video.Ffmpeg\runtime'
New-Item -ItemType Directory -Force $destino | Out-Null

$nome = "ffmpeg-n$Ramo-latest-win64-lgpl-$Ramo"
$url = "https://github.com/BtbN/FFmpeg-Builds/releases/download/latest/$nome.zip"
$tmp = Join-Path ([IO.Path]::GetTempPath()) ('pdfm-ffmpeg-' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Force $tmp | Out-Null
try {
    $zip = Join-Path $tmp 'ffmpeg.zip'
    Write-Host "Baixando $url"
    Invoke-WebRequest -Uri $url -OutFile $zip
    Expand-Archive -Path $zip -DestinationPath $tmp

    $exe = Get-ChildItem $tmp -Recurse -Filter 'ffmpeg.exe' | Select-Object -First 1
    if (-not $exe) { throw 'ffmpeg.exe não encontrado no pacote.' }
    $licenca = Get-ChildItem $tmp -Recurse -Filter 'LICENSE.txt' | Select-Object -First 1
    if (-not $licenca) { throw 'LICENSE.txt não encontrado no pacote (a licença precisa acompanhar o FFmpeg).' }

    $versao = (& $exe.FullName -hide_banner -version | Select-Object -First 1)
    $sha = (Get-FileHash $exe.FullName -Algorithm SHA256).Hash.ToLowerInvariant()

    $entrada = [IO.File]::OpenRead($exe.FullName)
    $saida = [IO.File]::Create((Join-Path $destino 'ffmpeg.exe.gz'))
    $gzip = New-Object IO.Compression.GZipStream($saida, [IO.Compression.CompressionLevel]::Optimal)
    try { $entrada.CopyTo($gzip) } finally { $gzip.Dispose(); $saida.Dispose(); $entrada.Dispose() }

    Copy-Item $licenca.FullName (Join-Path $destino 'LICENSE.txt') -Force
    @(
        "version=$versao",
        "sha256=$sha",
        "size=$($exe.Length)",
        "source=$url",
        'variant=win64 lgpl static (BtbN/FFmpeg-Builds)'
    ) | Set-Content (Join-Path $destino 'ffmpeg.manifest.txt') -Encoding utf8

    Write-Host $versao
    Write-Host "ffmpeg.exe: $($exe.Length) bytes, sha256 $sha"
    Write-Host "ffmpeg.exe.gz: $((Get-Item (Join-Path $destino 'ffmpeg.exe.gz')).Length) bytes"
    Write-Host 'Codificadores H.264 e AAC disponíveis:'
    & $exe.FullName -hide_banner -encoders | Select-String -Pattern '264|aac' | ForEach-Object { Write-Host $_.Line }
}
finally {
    Remove-Item $tmp -Recurse -Force -ErrorAction SilentlyContinue
}
