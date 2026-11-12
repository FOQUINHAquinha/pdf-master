# Histórico de alterações

## 0.8.1 — 12/11/2026

### Adicionado
- Ícone próprio do programa (folha com "PDF" sobre o verde do programa), em todos os tamanhos do
  Windows (16 a 256 px; nos tamanhos pequenos, versão simplificada). Aparece no executável, na
  janela, na barra de tarefas, nos atalhos, no instalador, no menu de contexto e no cabeçalho do
  programa. Gerado por `assets/gerar-icone.py`.

## 0.8.0 — 11/11/2026 — Fase 8: Finalização (as 8 fases concluídas)

### Adicionado
- Marcadores, links e anexos: marcadores com subníveis, página de sumário com links, links
  automáticos para endereços escritos no texto, remoção de links, anexar/remover/salvar anexos.
- PDF para Excel (uma aba por página, texto em linhas e colunas, números reconhecidos) e para
  PowerPoint (um slide por página com a imagem da página).
- Padrões de conversão nas Configurações (PDF → imagem e imagem → PDF).
- Teste com documento de 600 páginas (abrir, renderizar sob demanda, dividir).

### Alterado
- Todas as ferramentas do catálogo estão disponíveis; nenhuma aparece mais como "Prevista".

## 0.7.1 — 11/11/2026 — Fase 8 (primeira parte)

### Adicionado
- Mesclar PDF aceita fotos e imagens junto com os PDFs (como no PDF24), com tamanho de página
  escolhido para as imagens.
- Instalador (Inno Setup) com escolha de pasta, atalhos, integração opcional com o Windows e
  desinstalação completa; a versão portátil é testada no CI.
- Extrair conteúdo: imagens, texto, anexos, fontes e metadados.
- Converter para preto e branco mantendo o texto como texto, com conferência das páginas.
- Limpeza de arquivos temporários (automática e em Configurações); comandos `--register-menu`,
  `--register-openwith`, `--unregister-all` e `--clean-temp`.

## 0.7.0 — 11/11/2026 — Fase 7: Automação

### Adicionado
- **Fila de tarefas**: arquivos e pastas (PDF, imagens, TXT, HTML, Office) entram na fila com um
  fluxo; colunas de nome, operação, estado, progresso, tamanho original e final, destino, tempo e
  erro; estados Aguardando, Processando, Pausado, Concluído, Falhou e Cancelado; Iniciar, Pausar
  (ao fim da etapa atual), Retomar, Cancelar tudo, Repetir, Remover, Cancelar e Abrir por tarefa,
  Reprocessar somente com erro e Limpar concluídas; tarefas simultâneas configuráveis.
- **Fluxos encadeados** com editor visual: OCR, comprimir, numerar páginas, marca d'água, girar,
  excluir páginas, inverter ordem, remover páginas em branco, recortar margens, várias páginas por
  folha/livreto, remover metadados, reparar e PDF/A, em qualquer ordem e com as opções de cada
  etapa; quatro fluxos de exemplo (entre eles OCR → comprimir → numerar → PDF/A).
- **Monitorar pasta**: opcional, configurável e pausável; cada documento que chega é processado
  uma vez, depois de terminar de ser copiado; contadores e registro de atividade.
- **Linha de comando sem janela**: `--merge`, `--compress`, `--ocr`, `--split`, `--pdfa`,
  `--rotate`, `--to-image`, `--flow`, `--watch`, `--list-flows`, `--help`, `--version`, com
  `--output`, `--overwrite`, `--log` e códigos de saída.
- **Integração com o Windows** (só para o usuário, sem administrador): itens no menu de contexto
  dos PDFs (Abrir, Mesclar selecionados, Comprimir, Converter para imagem, Extrair páginas,
  Proteger), com vários arquivos reunidos numa só janela; registro em "Abrir com"; atalho para a
  tela de aplicativos padrão.
- Ferramentas de página: **inverter ordem**, **páginas em branco e repetidas**, **recortar
  margens**, **2 ou 4 páginas por folha e livreto**.
- **Repetir operação** no histórico (abre a ferramenta com os mesmos arquivos de entrada).
- Configurações: perfil de compressão padrão, idiomas e resolução padrão do OCR, tarefas
  simultâneas da fila, integração com o Windows.

### Alterado
- Histórico: esquema do banco na versão 2 (ferramenta e arquivos de entrada de cada operação);
  bancos da versão 1 são atualizados automaticamente.
- Tarefas da fila e das pastas monitoradas são registradas no histórico.
- Marcadores/links/anexos, PDF → Excel/PowerPoint, extrair conteúdo e preto e branco foram
  replanejados para a Fase 8 (continuam desabilitados).

## 0.6.0 — 11/11/2026 — Fase 6: Segurança e ferramentas avançadas

### Adicionado
- **Proteger com senha**: senha de abertura e de proprietário, criptografia AES de 256 bits e
  permissões (imprimir, imprimir em alta qualidade, copiar, alterar, comentar, preencher
  formulários, montar páginas, acessibilidade), em vários PDFs de uma vez. As senhas nunca são
  gravadas.
- **Remover proteção** com a senha de proprietário informada e confirmação de autorização.
- **Propriedades de segurança**: identifica documentos protegidos, criptografia, permissões,
  integridade estrutural, assinaturas digitais e PDF/A declarado, em vários PDFs.
- **Metadados**: ver, editar (título, autor, assunto, palavras-chave, criador, produtor, datas) e
  remover, com o bloco XMP regravado de acordo.
- **Verificar assinaturas digitais** com validação criptográfica real (PKCS#7/CMS e carimbo do
  tempo RFC 3161): integridade, alterações posteriores, certificado, emissor, validade, cadeia;
  pasta de raízes confiáveis adicionais (preparação para ICP-Brasil, sem alegar conformidade);
  relatório em TXT.
- **Reparar PDF**: diagnóstico da estrutura (inclusive tabelas de referências compactadas),
  reconstrução, cópia com as páginas legíveis e relatório do que foi recuperado e perdido.
- **PDF/A-1b, 2b e 3b**: conversão (fontes incorporadas com substitutas do Windows de mesmas
  medidas, perfil sRGB, XMP, remoção do que o nível proíbe, lista de mudanças) e validação com
  relatório; com o veraPDF instalado, cada arquivo só é salvo se ele aprovar. As conversões dos
  testes foram aprovadas pelo veraPDF 1.30.4 nos três níveis.
- **Comparar PDFs** lado a lado: texto acrescentado, removido e alterado com destaque na página,
  páginas inseridas e removidas, imagens e diferenças visuais, navegação e relatório em HTML.
- Testes com PDFs assinados (escritos byte a byte com certificados gerados no teste), protegidos,
  danificados e com recursos proibidos no PDF/A; o CI instala o veraPDF para validar as conversões.

### Alterado
- Mensagens de "chega na Fase 6" (PDF protegido ou danificado) agora indicam "Remover proteção" e
  "Reparar PDF".
- As ferramentas extras que o catálogo marcava como Fase 6 (recortar, páginas em branco, livreto,
  inverter ordem, marcadores/anexos, PDF para Excel/PowerPoint, extrair conteúdo, preto e branco)
  foram replanejadas para a Fase 7 — não fazem parte da Fase 6 da especificação.
- A compressão de vídeo continua disponível, como pedido.

## 0.5.0 — 11/11/2026 — Fase 5: Editor

### Adicionado
- **Editor de PDF**: página com camada de edição, miniaturas, zoom, 24 ferramentas, painel de
  propriedades e histórico de alterações com desfazer/refazer. Grava sempre num novo arquivo.
- **Texto**: inserir com as fontes do Windows (incorporadas ao PDF), tamanho, cor, negrito,
  itálico, sublinhado, alinhamento e espaçamento; **editar texto existente** dentro do próprio PDF
  quando a fonte permite, ou cobrir e reescrever com aviso claro quando não permite.
- **Imagens**: inserir, mover, redimensionar, recortar, girar, transparência, substituir.
- **Anotações** com aparência própria: marca-texto, sublinhado, tachado, comentário, nota adesiva,
  caixa de texto, seta, linha, retângulo, círculo e desenho livre.
- **Assinatura visual** (desenhada com o mouse ou importada) e **carimbos** (biblioteca local com
  os seis exemplos da especificação), guardados neste computador.
- **Marca d'água** e **cabeçalhos/rodapés** (com campos de página, total, data, hora e nome) em
  vários PDFs de uma vez.
- **Formulários**: criar campos (texto, várias linhas, caixa de seleção, opções, lista, data,
  botão) e preencher formulários existentes.
- Botão "Editar" no visualizador.
- Testes com PDFs reais: texto extraído, pixels renderizados nas quatro rotações de página,
  dicionários de anotações e campos relidos.

### Alterado
- O resolvedor de fontes do PDFsharp passou a ser único (PdfEngine), com 10 famílias do Windows.
- A renderização passou a desenhar os campos de formulário (antes o visualizador mostrava os
  campos vazios, porque o PDFium só os desenha com o ambiente de formulários ativado).
- A compressão de vídeo continua disponível, como pedido.

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
- O executável passou de cerca de 82 MB para 139 MB por causa do FFmpeg embutido.

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
