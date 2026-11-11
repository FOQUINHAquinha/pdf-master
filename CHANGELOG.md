# Histórico de alterações

## 0.4.1 — 10/11/2026 — Extra: compressão de vídeo e crédito do autor

### Adicionado
- **Comprimir vídeo** (aba própria no menu lateral): qualquer formato que o FFmpeg abra (MP4, MOV,
  AVI, MKV, WEBM, WMV, FLV, MPG, 3GP, GIF animado e outros) vira MP4 com H.264 + AAC em 360p —
  lado menor com 360 pixels, proporção, orientação de celular e pixels não quadrados respeitados,
  sem ampliar vídeos menores, no máximo 30 quadros por segundo e índice no início do arquivo.
  Fila de vários vídeos, progresso por vídeo com velocidade e tempo restante, cancelamento sem
  deixar arquivo parcial, original nunca alterado (`nome_360p.mp4`).
- FFmpeg LGPL embutido (extraído na primeira utilização e conferido por SHA-256), com a opção
  "Localizar ffmpeg.exe…" para usar outro FFmpeg instalado.
- Crédito "Criado por Gustavo Farias Medeiros" no rodapé do menu lateral, na nova seção "Sobre"
  das Configurações (com os componentes de terceiros e suas licenças) e nas propriedades do
  executável (empresa, autor e direitos autorais).
- Testes com vídeos reais em vários formatos, gerados pelo próprio FFmpeg; o CI executa a
  compressão pelo executável publicado.

### Alterado
- O executável ficou cerca de 50 MB maior por causa do FFmpeg embutido.

## 0.4.0 — 10/11/2026 — Fase 4: OCR e digitalização

### Adicionado
- **OCR** (Tesseract 5, local) para PDFs digitalizados e imagens, em português, inglês e espanhol,
  com instalação de outros idiomas por arquivo `.traineddata`.
- **PDF pesquisável**: camada de texto invisível posicionada sobre cada palavra; a aparência da
  página não muda e o original é preservado (`nome_ocr.pdf`).
- **Extração do texto reconhecido** para TXT e Word.
- Pré-processamento próprio: orientação automática (90°/180°/270°), correção de inclinação,
  realce de contraste e redução de ruído.
- **OCR em lote** com fila, progresso por página, tempo decorrido/estimado e erros por arquivo.
- **Digitalizar**: páginas vindas de scanner WIA ou de imagens, reordenação e PDF com OCR opcional.
  A parte do scanner não pôde ser testada sem um aparelho e está marcada assim no programa.
- Testes novos de OCR, geometria da camada de texto e scanner; o CI executa o OCR pelo executável.

### Alterado
- A ferramenta "Digitalizar" passou a informar que usa WIA (TWAIN não implementado).

## 0.3.0 — 10/11/2026 — Fase 3: Conversão e compressão

### Adicionado
- **PDF para imagem**: PNG, JPG, TIFF (inclusive um único arquivo de várias páginas) e BMP;
  resolução de 36 a 600 DPI, qualidade JPG, escala de cinza e seleção de páginas.
- **Imagem para PDF**: JPG, PNG, BMP, TIFF de várias páginas e GIF; página do tamanho da imagem,
  A4 ou Carta; orientação, margem, um PDF por imagem ou todas em um; JPG incorporado sem
  recompressão; orientação EXIF das fotos respeitada.
- **Texto para PDF** feito pelo próprio programa (A4, quebra automática de linhas, texto pesquisável).
- **Office para PDF** (Word, Excel, PowerPoint, ODF, RTF) e **HTML para PDF**, usando o LibreOffice
  instalado no computador; a tela informa quando ele não está presente.
- **PDF para Word**: modo texto corrido (interno) e modo "manter a aparência" (LibreOffice).
- **PDF para TXT e HTML**.
- **Comprimir PDF**: perfis sem perda, leve, equilibrada, forte, máxima e personalizada; regravação
  e redução de imagens, compactação de streams, remoção de metadados; tamanhos, economia e
  comparação da primeira página antes/depois. Se o resultado não fica menor, nada é gravado.
- Conversão em lote em todas as telas: um arquivo com problema não interrompe os demais.
- 28 testes novos (175 no total); o CI instala o LibreOffice e executa todas as ferramentas novas
  pelo executável publicado.

### Alterado
- A renderização passou a chamar diretamente uma compilação atual do PDFium
  (bblanchon.PDFium 152), no lugar do Docnet.Core 2.6. O arquivo deixou de ser carregado inteiro
  na memória para renderizar.
- PDFsharp atualizado de 6.2.0 para 6.2.4.
- "PDF para Excel e PowerPoint" foi replanejada para a Fase 6, junto com a extração de tabelas.

## 0.2.0 — 10/11/2026 — Fase 2: Visualizador e organização

### Adicionado
- Motor de PDF (`PDFMaster.PdfEngine`, PDFsharp) atrás da abstração `IPdfEngine`: leitura de páginas,
  tamanhos, rotação e marcadores; montagem de novos documentos a partir de páginas de vários arquivos.
- Renderização de páginas (`PDFMaster.Rendering`, PDFium) atrás da abstração `IPdfRenderer`, com
  cache de miniaturas em memória.
- **Visualizador de PDF**: página central, painel de miniaturas, zoom, ajustar à largura,
  navegação por botões, número da página e teclado.
- **Mesclar PDF**: lista de arquivos com ordem, páginas por arquivo, marcador por arquivo, nome e
  pasta de saída.
- **Intercalar documentos**: alternância 1 a 1 ou em blocos, ordem inversa por documento, sobras no
  final ou descartadas.
- **Dividir PDF**: uma página por arquivo, a cada N páginas, por intervalos, em pontos de corte, por
  tamanho (medido) e por marcadores; padrão de nomes configurável com prévia.
- **Organizar páginas**: miniaturas com arrastar e soltar, mover, girar, duplicar, excluir, página em
  branco, inserir de outro PDF, seleção por intervalo, desfazer/refazer, salvar como novo PDF e
  extrair as páginas selecionadas.
- Cartões de ferramenta clicáveis; ferramentas mais utilizadas ou recomendadas na página inicial;
  botão "Visualizar" na lista de arquivos abertos.
- Linha de comando: `--tool`, `--output` e `--run`.
- 66 testes novos (147 no total) e execução das ferramentas pela interface no CI.

### Alterado
- A nota "As seções Ferramentas mais utilizadas…" saiu da página inicial; a seção agora existe.

### Segurança
- Fixada a versão de `System.Security.Cryptography.Pkcs` (dependência do PDFsharp) para eliminar os
  alertas GHSA-846f-f764-76mx e GHSA-2fmv-rv2c-59x7.

## 0.1.0 — 10/11/2026 — Fase 1: Fundação

### Adicionado
- Solução Visual Studio com 15 projetos seguindo a arquitetura modular da especificação.
- Janela principal com barra superior (logotipo, pesquisa, notificações, configurações, tema, versão),
  menu lateral com as 11 seções e área principal.
- Temas claro e escuro com a paleta da especificação, alternáveis em tempo de execução, e cor de
  destaque personalizável.
- Catálogo das 43 ferramentas previstas, com a fase de entrega de cada uma, e pesquisa por nome,
  descrição e sinônimos (ignora acentos e maiúsculas).
- Página inicial: abrir arquivos, importar pasta, arrastar e soltar, arquivos recentes e últimas operações.
- Validação dos arquivos (tipo, cabeçalho de PDF, vazio, ilegível) e detecção de duplicados e de nomes repetidos.
- Cópia de arquivos para uma pasta com renomeação automática, pular ou substituir (com confirmação),
  subpastas por data ou por operação, progresso e cancelamento.
- Histórico em SQLite com pesquisa, filtros por data e tipo, exclusão e limpeza; pode ser desativado.
- Configurações de aparência, arquivos e privacidade, salvas automaticamente.
- Notificações internas com ações.
- Linha de comando da interface: `--theme`, `--page` e arquivos/pastas para abrir.
- Modo portátil (`portable.flag`).
- Log estruturado local e tratamento central de exceções.
- 81 testes automatizados; workflow do GitHub Actions que compila, testa, publica o executável e faz
  teste de inicialização com capturas de tela.
- Documentos: `docs/ARQUITETURA.md` (análise, licenças, plano, riscos, estratégia de testes) e `PROJECT_STATUS.md`.

### Segurança
- Fixada a versão do SQLite nativo para eliminar o alerta GHSA-2m69-gcr7-jv3q.
