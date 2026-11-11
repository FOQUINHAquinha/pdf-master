# PDF Master

Suíte de ferramentas PDF para Windows 10 e 11, gratuita, sem conta, sem anúncios e com
processamento 100% local. Em desenvolvimento por fases — veja [PROJECT_STATUS.md](PROJECT_STATUS.md).

Criado por **Gustavo Farias Medeiros**.

**Estado atual: Fases 1 a 6 concluídas.** Já funcionam:

- visualizador e organização: mesclar, dividir, extrair, reorganizar, girar, excluir, inserir e intercalar páginas;
- conversão: PDF → imagem, imagem → PDF, texto → PDF, PDF → Word, PDF → TXT/HTML e, com o
  LibreOffice instalado, Word/Excel/PowerPoint/HTML → PDF;
- compressão de PDF com perfis e comparação antes/depois;
- OCR local (Tesseract) com PDF pesquisável, extração para TXT/Word e OCR em lote;
- digitalização por scanner WIA ou a partir de imagens (o scanner ainda não foi testado com um aparelho real);
- editor: inserir e editar texto, imagens, anotações, assinatura visual, carimbos, campos de
  formulário; marca d'água, cabeçalhos e rodapés; preenchimento de formulários;
- segurança: proteger com senha (AES de 256 bits) e permissões, remover proteção com a senha,
  propriedades de segurança, metadados (ver, editar, remover), verificação criptográfica de
  assinaturas digitais e reparo de PDFs danificados;
- PDF/A-1b, 2b e 3b: conversão e validação, com relatório (a conformidade só é afirmada quando o
  validador veraPDF, opcional, confirma);
- comparação de dois PDFs lado a lado (texto, páginas, imagens e diferenças visuais) com relatório;
- **extra:** compressão de vídeo — qualquer formato que o FFmpeg abra vira MP4 (H.264 + AAC) em 360p.

Automação (lote, pasta monitorada, menu de contexto) e algumas ferramentas extras aparecem no
programa como "Prevista para a Fase 7".

### Modelos de idioma do OCR

Antes de compilar no seu computador, rode `tools/obter-modelos-ocr.ps1`: ele baixa os modelos
de português, inglês e espanhol (tessdata_fast, Apache-2.0) para `PDFMaster.OCR/tessdata`, de onde
são incorporados ao programa. Sem eles o programa compila, mas a tela de OCR avisa que não há
idiomas instalados. O executável gerado pelo GitHub Actions já vem com os três.

### FFmpeg (compressão de vídeo)

A aba "Comprimir vídeo" usa o [FFmpeg](https://ffmpeg.org) na variante **LGPL** compilada por
[BtbN/FFmpeg-Builds](https://github.com/BtbN/FFmpeg-Builds), executado como programa separado.
O executável gerado pelo GitHub Actions já o traz embutido (por isso passou de cerca de 82 MB para 139 MB);
na primeira utilização ele é extraído para a pasta de dados (`video-runtime`), com a licença.
Para compilar no seu computador com o FFmpeg embutido, rode `tools/obter-ffmpeg.ps1`. Sem ele, a
aba procura um FFmpeg instalado (PATH, winget, Chocolatey, Scoop) ou deixa você indicar o
`ffmpeg.exe`; a variável `PDFMASTER_FFMPEG` também pode apontar para ele.

### LibreOffice (opcional)

As conversões de documentos do Office e de HTML para PDF, e a opção "manter a aparência" de PDF
para Word, usam o [LibreOffice](https://www.libreoffice.org) instalado no computador. O PDF Master
não o inclui, não o baixa e funciona sem ele — só essas conversões ficam indisponíveis, e a tela
avisa. Para apontar uma instalação fora do local padrão, defina a variável `PDFMASTER_SOFFICE`.

### veraPDF (opcional, validação PDF/A)

A ferramenta "Converter e validar PDF/A" sempre faz uma verificação interna dos principais
requisitos, mas só afirma conformidade quando o [veraPDF](https://verapdf.org) (gratuito,
GPLv3+/MPLv2+, requer Java) confirma. O PDF Master não o inclui nem o baixa: instale-o em
`C:\veraPDF` (ou `Arquivos de Programas\veraPDF`) ou aponte a variável `PDFMASTER_VERAPDF` para o
`verapdf.bat`. Quando ele está presente, cada arquivo convertido só é salvo se for aprovado.

### Certificados raiz adicionais (assinaturas digitais)

A verificação de assinaturas usa as raízes confiáveis do Windows. Para confiar em outras (por
exemplo, as da ICP-Brasil), coloque os certificados `.cer`/`.crt` na pasta
`Certificados confiáveis`, dentro da pasta de dados (o botão "Pasta de raízes confiáveis…" abre
essa pasta). A validação completa ICP-Brasil (revogação, políticas) ainda não é feita.

## Baixar o executável

1. Abra a aba **Actions** do repositório e entre na execução mais recente do workflow **build**.
2. Em **Artifacts**, baixe:
   - `PDFMaster-win-x64` — `PDFMaster.exe`, executável único (não precisa instalar o .NET);
   - `PDFMaster-portatil-win-x64` — o mesmo executável com `portable.flag`, que guarda os dados
     na pasta `Data` ao lado do programa.

O executável ainda não é assinado; o Windows pode pedir confirmação na primeira execução.

## Compilar

Requer o SDK do .NET 10.

```
dotnet build PDFMaster.sln -c Release
dotnet test PDFMaster.Tests/PDFMaster.Tests.csproj -c Release
dotnet publish PDFMaster.App/PDFMaster.App.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o publish
```

## Linha de comando

```
PDFMaster.exe [--theme light|dark] [--page <seção>] [--tool <ferramenta>] [--output <pasta>] [--run] [arquivos e pastas…]
```

| Parâmetro | Efeito |
|---|---|
| `--theme light` / `--theme dark` | Define e salva o tema. |
| `--page <seção>` | Abre direto numa seção: `home`, `organize`, `edit`, `convert`, `compress`, `security`, `ocr`, `video`, `compare`, `batch`, `history`, `settings`. |
| `--tool <ferramenta>` | Abre uma ferramenta com os arquivos informados: `view`, `merge`, `interleave`, `split`, `reorder`, `rotate`, `delete-pages`, `insert-pages`, `extract-pages`, `pdf-to-image`, `image-to-pdf`, `office-to-pdf`, `text-html-to-pdf`, `pdf-to-word`, `pdf-to-text`, `compress`. |
| `--output <pasta>` | Define a pasta de destino da ferramenta aberta com `--tool`. |
| `--run` | Executa a ação principal da ferramenta com as opções padrão (a janela continua aberta para mostrar o resultado). |
| arquivos e pastas | Sem `--tool`, vão para a lista de arquivos abertos (pastas são lidas com as subpastas). |

Exemplos:

```
PDFMaster.exe --tool merge --output C:\Saida --run a.pdf b.pdf
PDFMaster.exe --tool split documento.pdf
PDFMaster.exe --tool view documento.pdf
PDFMaster.exe --tool compress --output C:\Saida --run digitalizado.pdf
PDFMaster.exe --tool pdf-to-image --output C:\Saida --run documento.pdf
PDFMaster.exe --tool ocr --output C:\Saida --run escaneado.pdf
PDFMaster.exe --tool scan --output C:\Saida --run pagina1.png pagina2.jpg
PDFMaster.exe --tool video-compress --output C:\Saida --run ferias.mov
PDFMaster.exe --tool watermark --output C:\Saida --run contrato.pdf
PDFMaster.exe --tool edit-text contrato.pdf
```

A execução sem janela (`--merge`, `--compress`, `--ocr`, `--split` em modo terminal) entra na Fase 7.

## Onde ficam os dados

- Padrão: `%LOCALAPPDATA%\PDFMaster` (banco `pdfmaster.db` e pasta `Logs`).
- Modo portátil: pasta `Data` ao lado do executável.
- Variável de ambiente `PDFMASTER_DATA_DIR`: usa a pasta indicada.

O banco guarda configurações, histórico e arquivos recentes — nunca o conteúdo dos documentos.
O programa não usa a rede (a verificação de assinaturas não consulta revogação na internet). (O LibreOffice, quando usado para converter HTML, pode buscar imagens
que a própria página referencia na internet.)

## Documentação

- [docs/ARQUITETURA.md](docs/ARQUITETURA.md) — arquitetura, bibliotecas e licenças, plano, riscos, testes.
- [PROJECT_STATUS.md](PROJECT_STATUS.md) — o que está pronto, o que falta e como foi testado.
- [CHANGELOG.md](CHANGELOG.md) — histórico de alterações.
