# Baixa os modelos de idioma do Tesseract usados na compilação (por, eng, spa).
$ErrorActionPreference = 'Stop'
$destino = Join-Path $PSScriptRoot '..\PDFMaster.OCR\tessdata'
New-Item -ItemType Directory -Force $destino | Out-Null
foreach ($idioma in 'por', 'eng', 'spa') {
    $arquivo = Join-Path $destino "$idioma.traineddata"
    if ((Test-Path $arquivo) -and ((Get-Item $arquivo).Length -gt 500000)) { continue }
    Invoke-WebRequest -Uri "https://github.com/tesseract-ocr/tessdata_fast/raw/main/$idioma.traineddata" -OutFile $arquivo
    if ((Get-Item $arquivo).Length -lt 500000) { throw "Download incompleto: $idioma" }
    Write-Host "$idioma.traineddata: $((Get-Item $arquivo).Length) bytes"
}
