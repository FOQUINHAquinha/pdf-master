# Modelos de idioma do OCR

Os arquivos `*.traineddata` desta pasta são incorporados ao programa na compilação. Eles não
ficam no repositório (são binários de alguns megabytes). Para obtê-los, execute na raiz:

```
pwsh tools/obter-modelos-ocr.ps1
```

O script baixa `por`, `eng` e `spa` do projeto oficial
[tessdata_fast](https://github.com/tesseract-ocr/tessdata_fast) (licença Apache-2.0). O download
acontece só na compilação; o programa em si nunca acessa a rede.

Sem os arquivos, a solução compila normalmente, mas o OCR avisa que não há idiomas instalados
até que o usuário instale um modelo pela própria tela de OCR.
