# FFmpeg incorporado

Esta pasta recebe, na compilação, o FFmpeg usado pela ferramenta "Comprimir vídeo":

- `ffmpeg.exe.gz` — `ffmpeg.exe` da compilação estática **LGPL** de BtbN/FFmpeg-Builds (win64),
  comprimido com gzip;
- `ffmpeg.manifest.txt` — versão, SHA-256 e origem do executável;
- `LICENSE.txt` — licença que acompanha a compilação.

Os arquivos não ficam no repositório (são grandes). Para gerá-los, rode
`tools/obter-ffmpeg.ps1` no Windows. O GitHub Actions faz isso antes de compilar.

Sem eles o programa compila normalmente; a aba "Comprimir vídeo" então procura um FFmpeg
instalado no computador ou pede para o usuário indicar o `ffmpeg.exe`.

A variante LGPL não contém bibliotecas exclusivamente GPL (como a libx264). O FFmpeg é executado
como um programa separado; o código-fonte correspondente está em https://ffmpeg.org e os scripts
de compilação em https://github.com/BtbN/FFmpeg-Builds.
