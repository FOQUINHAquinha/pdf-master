# PDF Master — estado do projeto

Atualizado em 11/11/2026 · Versão 0.7.1 (Fase 8 em andamento)

## Fases

| Fase | Conteúdo | Estado |
|---|---|---|
| 1 — Fundação | Solução, arquitetura, interface, temas, menu, pesquisa, SQLite, gerenciamento de arquivos | **Concluída** |
| 2 — Visualizador e organização | Visualizador, miniaturas, mesclar, dividir, extrair, girar, reordenar, excluir, intercalar | **Concluída** |
| 3 — Conversão e compressão | PDF ↔ imagens, Office → PDF, PDF → Word, compressão | **Concluída** |
| 4 — OCR e digitalização | OCR, PDF pesquisável, extração de texto, digitalização, OCR em lote | **Concluída** (digitalização aguardando teste com scanner real) |
| 5 — Editor | Texto, imagens, anotações, assinaturas visuais, carimbos, marca d'água, cabeçalhos, rodapés, formulários | **Concluída** |
| Extra — Compressão de vídeo | Qualquer formato → MP4 (H.264 + AAC) em 360p, com FFmpeg LGPL embutido | **Concluída** |
| 6 — Segurança e ferramentas avançadas | Senhas, permissões, propriedades de segurança, metadados, PDF/A (conversão e validação), comparação, reparo, verificação de assinaturas | **Concluída** |
| 7 — Automação | Lote, filas, fluxos, pasta monitorada, menu de contexto, linha de comando; inverter, páginas em branco, recorte, livreto | **Concluída** |
| 8 — Finalização | Testes gerais, otimização, instalador, versão portátil, documentação; quatro ferramentas extras do catálogo | **Em andamento** (ver abaixo) |

## Como cada item foi verificado

- **Automático**: testes do `PDFMaster.Tests` aprovados no CI, com PDFs reais gerados e relidos.
- **Execução pela interface**: o executável publicado é aberto no CI com `--tool … --run`; a tela
  carrega os arquivos, executa a ação principal e o CI confere os arquivos gerados.
- **Captura de tela**: a tela foi conferida visualmente nas capturas do CI.
- **Manual pendente**: depende de clique, arrasto ou diálogo do Windows e ainda precisa ser
  conferido por uma pessoa.

## Funcionalidades

### Fase 8 — Finalização (em andamento)

| Item | Estado | Testes |
|---|---|---|
| **Mesclar PDF aceita fotos** (JPG, PNG, BMP, TIFF de várias páginas, GIF) misturadas com PDFs, na ordem da lista; página da imagem do tamanho da foto, A4 ou Carta | Concluído | Execução pela interface no CI (2 PDFs + 2 fotos intercalados) + captura de tela |
| **Instalador** (Inno Setup): escolha da pasta, atalho no menu Iniciar e opcional na área de trabalho, menu de contexto e "Abrir com" opcionais, desinstalação completa (programa, atalhos, registro, temporários) que pergunta antes de apagar configurações e histórico e nunca toca em documentos | Concluído | CI: instala em silêncio, confere arquivos, atalho, registro e o programa instalado; desinstala e confere a remoção; repete pedindo a remoção dos dados |
| **Versão portátil** (pasta com `PDFMaster.exe` e `portable.flag`: dados na pasta `Data` ao lado) | Concluído | CI: executa a cópia portátil e confere que nada foi gravado em `%LOCALAPPDATA%` |
| Limpeza de temporários (automática ao abrir, para sobras com mais de um dia; botão em Configurações › Privacidade; comando `--clean-temp`) | Concluído | Automático |
| **Extrair conteúdo**: imagens (JPEG original; demais em PNG), texto por página, anexos (nomes saneados, nada fora da pasta), fontes incorporadas, metadados e XMP | Concluído | Automático (imagens conferidas por pixel, anexo com nome malicioso, fontes, metadados, pasta nova a cada extração) + execução pela interface |
| **Converter para preto e branco** sem rasterizar (texto continua texto): cores RGB/CMYK do conteúdo, imagens RGB/CMYK/indexadas, degradês simples; conferência visual de cada página no fim | Concluído | Automático (cores e imagens conferidas por pixel; página com cor especial apontada) + execução pela interface |
| Marcadores, links e anexos (editor) | Pendente | — |
| PDF para Excel e PowerPoint | Pendente | — |
| Padrões de conversão nas Configurações, cache de miniaturas em disco, revisão geral e documentação final | Pendente | — |

Limitações conhecidas desta etapa: o instalador e o executável não são assinados digitalmente
(o SmartScreen pode avisar); no preto e branco, cores especiais de gráfica (separação/DeviceN),
degradês complexos, JPEG CMYK, JPEG 2000 e imagens embutidas no conteúdo ficam como estão e as
páginas afetadas são informadas; na extração, transparências das imagens não vão para o PNG.

### Fase 7 — Automação

| Funcionalidade | Estado | Testes |
|---|---|---|
| **Fila de tarefas**: arquivos e pastas (PDF, imagens, TXT, HTML, Office) com o fluxo escolhido; colunas nome, operação, estado, progresso, tamanho original e final, destino, tempo e erro | Concluído | Automático + execução pela interface (2 documentos pelo fluxo OCR → comprimir → numerar → PDF/A, ambos aprovados pelo veraPDF) + captura de tela |
| Estados Aguardando, Processando, Pausado, Concluído, Falhou, Cancelado; Iniciar, Pausar (ao fim da etapa atual), Retomar, Cancelar (tarefa e tudo), Repetir, Remover, Reprocessar somente com erro, Limpar concluídas | Concluído | Automático (falha, correção e reprocessamento só da falha; pausa segura as tarefas; cancelamento; repetição; remoção). Botões da tela: manual pendente |
| Controle de simultaneidade (1 a 8 tarefas; automático = metade dos núcleos, até 4) | Concluído | Automático (2 em paralelo) |
| **Fluxos encadeados** com editor visual (lista de fluxos, etapas em ordem, opções de cada etapa, sufixo do nome), salvos em `fluxos.json` | Concluído | Automático (todas as 13 etapas salvas e relidas; fluxo de 5 etapas conferido página a página; TXT importado e comprimido). Editor: manual pendente |
| **Monitorar pasta**: opcional, configurável, pausável; processa cada arquivo uma vez, só depois de terminar de ser copiado; contadores e atividade | Concluído | Automático (arquivo novo processado uma vez, existentes ignorados, nada entra pausado, retoma) + execução real no CI (pasta configurada antes de abrir o programa) + captura de tela |
| **Linha de comando sem janela**: `--merge`, `--compress`, `--ocr`, `--split`, `--pdfa`, `--rotate`, `--to-image`, `--flow`, `--watch`, `--list-flows`, `--help`, `--version`, `--output`, `--overwrite`, `--log`, códigos de saída 0/1/2/3 | Concluído | Automático (análise dos parâmetros, execução, recusa de sobrescrever, sucesso parcial) + execução do programa publicado no CI |
| **Menu de contexto** (Abrir, Mesclar selecionados, Comprimir, Converter para imagem, Extrair páginas, Proteger), vários arquivos numa só janela | Concluído | Automático (registro gravado e removido numa chave de teste; três processos reunidos numa lista) + execução no CI (dois processos `--context merge` → uma janela). Clique no Explorador: manual pendente |
| **Abrir com** e associação de arquivo (ProgId, `OpenWithProgids`, `RegisteredApplications`) e atalho para os aplicativos padrão do Windows | Concluído | Automático (chaves de teste). Escolha como padrão: manual pendente (feita pelo usuário no Windows) |
| **Inverter ordem das páginas** | Concluído | Automático + execução pela interface |
| **Páginas em branco e repetidas** | Concluído | Automático (brancas e repetidas detectadas; documento sem brancas não gera arquivo) + execução pela interface |
| **Recortar margens** (CropBox; o conteúdo não é apagado) | Concluído | Automático (tamanho exibido conferido pelo PDFium; margens maiores que a página recusadas) + execução pela interface |
| **2 ou 4 páginas por folha e livreto** (ordem de dobra, páginas completadas até múltiplo de 4) | Concluído | Automático (ordem do livreto pelo texto de cada lado; páginas com /Rotate desenhadas na posição certa, conferido por pixels) + execução pela interface |
| **Repetir operação** no histórico (mesma ferramenta, mesmos arquivos de entrada) | Concluído | Automático (gravação e leitura; atualização de banco antigo). Botão: manual pendente |
| Padrões nas Configurações: compressão, idiomas e resolução do OCR, tarefas simultâneas | Concluído | Captura de tela; efeito ao abrir as telas: manual pendente |
| Tarefas da fila e das pastas monitoradas no histórico | Concluído | Manual pendente |

### Limitações conhecidas da Fase 7

| Item | Situação |
|---|---|
| Pastas monitoradas | Funcionam só com o programa aberto ou com `PDFMaster.exe --watch` (não há serviço do Windows nem início automático com o sistema). |
| Menu de contexto | No Windows 11 os itens ficam em "Mostrar mais opções" (o menu compacto exige um pacote MSIX assinado). |
| Programa padrão para PDF | O Windows não deixa um programa se tornar padrão sozinho; o PDF Master abre a tela de aplicativos padrão para a pessoa escolher. |
| Linha de comando | O programa é de janela: no `cmd`, o prompt volta antes do fim; use `start /wait` (ou `Start-Process -Wait`) para esperar e ler o código de saída. |
| Pausar | Uma tarefa em andamento para no fim da etapa atual (uma etapa de OCR longa termina antes de pausar); Cancelar interrompe na hora. |
| Repetir operação | Reabre a ferramenta com os mesmos arquivos; as opções escolhidas na época não são guardadas. Operações registradas antes da 0.7 não têm os arquivos e não podem ser repetidas. |
| Páginas em branco | Página com pouquíssimo conteúdo (até 0,1% de pixels escuros e sem texto) conta como branca: confira o resultado. Repetidas = idênticas na imagem renderizada e no texto. |
| Recortar | Só reduz a área visível (CropBox); não altera o tamanho do papel nem apaga o conteúdo de fora. |

### Ferramentas do catálogo replanejadas para a Fase 8

Marcadores/links/anexos, PDF para Excel e PowerPoint, extrair conteúdo e converter para preto e
branco **não foram implementadas** nesta fase e aparecem como "Prevista para a Fase 8", sem botão
ativo. As demais ferramentas extras que estavam na Fase 7 (inverter, páginas em branco, recortar,
livreto) foram entregues.

### Fase 6 — Segurança e ferramentas avançadas

| Funcionalidade | Estado | Testes |
|---|---|---|
| **Proteger com senha**: senha de abertura, senha de proprietário e criptografia **AES de 256 bits**; vários PDFs de uma vez | Concluído | Automático (sem senha não abre, senha errada recusada, algoritmo conferido no arquivo) + captura de tela |
| **Permissões**: imprimir (e em alta qualidade), copiar, alterar, comentar, preencher formulários, montar páginas, acessibilidade | Concluído | Automático (permissões relidas com a senha de abertura e com a de proprietário) |
| **Remover proteção** com a senha informada e confirmação de autorização; exige a senha de proprietário (a de abertura sozinha não libera as restrições) | Concluído | Automático (senha errada, senha só de abertura, cópia sem criptografia com o mesmo texto) + captura de tela |
| **Propriedades de segurança** de vários PDFs: protegido ou não, criptografia, permissões negadas, versão, páginas, integridade estrutural, assinaturas digitais, PDF/A declarado | Concluído | Captura de tela (com PDF comum, assinado, protegido e danificado) |
| **Metadados**: ver e editar título, autor, assunto, palavras-chave, criador, produtor, data de criação e de modificação; o XMP é regravado de acordo | Concluído | Automático (valores relidos exatamente, inclusive acentos e datas) + execução pela interface |
| **Remover metadados** (propriedades e bloco XMP) | Concluído | Automático (nada dos valores antigos fica nos bytes do arquivo) |
| **Verificar assinaturas digitais** com validação criptográfica real (PKCS#7/CMS): resumo do conteúdo assinado, assinatura do signatário, faixa assinada, alterações posteriores, certificado, emissor, validade na data da assinatura, carimbo do tempo (RFC 3161), cadeia de certificação | Concluído | Automático (assinatura íntegra, 1 byte alterado, conteúdo acrescentado depois, cadeia com raiz própria, certificado vencido, PDF sem assinatura) + captura de tela |
| Nunca declara uma assinatura "válida" só porque há certificado: a identidade só é confirmada com cadeia até uma raiz confiável do Windows ou da pasta de raízes do programa | Concluído | Automático (certificado autoassinado = "identidade não confirmada") |
| Pasta "Certificados confiáveis" para raízes adicionais (preparação para ICP-Brasil); relatório em TXT | Concluído | Raiz adicional: automático; botões: manual pendente |
| **Reparar PDF**: diagnóstico (cabeçalho, fim do arquivo, tabela de referências clássica ou compactada, objetos ausentes, páginas que não carregam), reconstrução pelo PDFium, cópia com as páginas legíveis e relatório do que foi recuperado e perdido | Concluído | Automático (deslocamentos errados, arquivo cortado, página destruída, lixo irrecuperável) + execução pela interface |
| **Converter para PDF/A-1b, 2b e 3b**: incorpora fontes não incorporadas (substitutas do Windows com as mesmas medidas: Arial, Times New Roman, Courier New…), perfil de cor sRGB, metadados XMP coerentes, retira JavaScript, ações e anexos proibidos, ajusta anotações, camadas e formulários; cada mudança é listada | Concluído | Automático, **validado pelo veraPDF 1.30.4** nos três níveis (ver abaixo) + execução pela interface (também validada pelo veraPDF) |
| A conversão para quando não dá para cumprir o nível sem mudar a aparência (transparência no PDF/A-1, cores CMYK sem perfil, fontes sem equivalente, PDF protegido) e explica o motivo; nenhum arquivo é gerado | Concluído | Automático |
| **Validar PDF/A** (só validar) com relatório em TXT: verificação interna de 12 grupos de requisitos e, com o veraPDF instalado, a validação completa | Concluído | Automático (PDF comum reprovado com explicações); tela: manual pendente |
| **Comparar PDFs** lado a lado: texto acrescentado, removido e alterado (palavra a palavra, com posição), páginas inseridas e removidas, imagens trocadas, diferenças visuais (pixels); cores por tipo, navegação pelas diferenças e por páginas | Concluído | Automático (posições conferidas, alinhamento de páginas, imagens e desenhos) + execução pela interface |
| Relatório da comparação em HTML | Concluído | Automático |
| Cancelamento em todas as operações; originais nunca alterados; senhas nunca gravadas | Concluído | Automático (originais conferidos byte a byte) |

**Conformidade PDF/A verificada pelo veraPDF** (validador de referência, executado no CI): PDF com
fontes padrão não incorporadas → PDF/A-1b, 2b e 3b; PDF gerado pelo próprio programa (fontes
Unicode incorporadas) → 1b e 2b; PDF com JavaScript e anexo → 2b (anexo removido) e 3b (anexo
mantido e identificado); PDF com transparência → 2b (o 1b é recusado com explicação). Todos
aprovados. Isso comprova a conversão desses casos, não de qualquer PDF: documentos com recursos
fora do que foi testado podem ser recusados pela verificação — e então o arquivo não é gerado.

### Limitações conhecidas da Fase 6

| Item | Situação |
|---|---|
| Revogação de certificados | Não é consultada (o programa não acessa a internet). O relatório diz isso em cada assinatura. |
| ICP-Brasil | A arquitetura aceita raízes adicionais (pasta "Certificados confiáveis") e o relatório identifica cadeias ICP-Brasil, mas **não há validação ICP-Brasil completa** (políticas de assinatura, LCR/OCSP, carimbo do tempo da ICP). Não foi testado com certificados ICP-Brasil reais. |
| Assinar digitalmente | Não implementado (a especificação pede verificação). Qualquer ferramenta que regrave o PDF (proteger, metadados, PDF/A…) invalida assinaturas existentes; a tela de metadados e a conversão PDF/A avisam. |
| Restrições de permissão | Dependem do programa leitor respeitá-las; só a senha de abertura protege de fato o conteúdo. |
| Remover proteção | Só com a senha de proprietário. Não há quebra de senha (e não haverá). |
| PDF/A — cores CMYK | Documentos com cores CMYK sem perfil de saída CMYK são recusados (o Windows não traz um perfil CMYK para declarar). |
| PDF/A — fontes | Fontes não incorporadas só são incorporadas quando há equivalente no Windows e a codificação é WinAnsi ou MacRoman; fontes de símbolos (Symbol, ZapfDingbats) e codificações próprias param a conversão. A substituta tem as mesmas medidas, mas o desenho dos caracteres é o da fonte do Windows. |
| PDF/A-1b | Transparência, camadas e anotações de tipos posteriores ao PDF 1.4 param a conversão (use o 2b). Comentários sem aparência própria são removidos e listados. |
| PDF/A-2b | Anexos são removidos (o 2b só aceita anexos que já sejam PDF/A, e isso não é verificado); use o 3b para mantê-los. |
| Validação interna | Cobre os principais requisitos, não todos (por exemplo, não confere o uso do glifo .notdef). Por isso o programa só fala em "conforme" quando o veraPDF confirma; sem ele, diz "aprovado na verificação interna". |
| Reparo | A recuperação é a da reconstrução do PDFium: conteúdo cujos bytes se perderam não volta. Na cópia de um documento com páginas quebradas, marcadores e formulários do documento não são levados. |
| Comparação | O texto é comparado na ordem de leitura do PDFium; documentos sem texto (digitalizados) são comparados só pela aparência — use o OCR antes. Mudanças só de formatação aparecem como diferença visual. PDFs protegidos por senha de abertura precisam ser desprotegidos antes. |
| Propriedades de segurança | Documentos com senha de abertura são identificados, mas o conteúdo só é analisado com a senha (em "Remover proteção"). |

### Fase 5 — Editor

| Funcionalidade | Estado | Testes |
|---|---|---|
| Área de edição: página central, miniaturas, zoom, navegação, barra de ferramentas, propriedades, histórico com desfazer/refazer | Concluído | Captura de tela; uso com o mouse: manual pendente |
| Edições guardadas na posição exibida e convertidas na gravação, inclusive em páginas giradas (90°, 180°, 270°) e com CropBox | Concluído | Automático (pixels conferidos nas 4 rotações) |
| **Inserir texto**: fonte (10 famílias do Windows, incorporadas), tamanho, cor, negrito, itálico, sublinhado, alinhamento, espaçamento entre linhas, posição livre | Concluído | Automático (texto extraído e posição conferida) |
| **Editar texto existente** no próprio PDF, com a mesma fonte, quando a estrutura permite (fontes simples, trecho num comando de texto, caracteres presentes na fonte) | Concluído | Automático (Tj e TJ; posição mantida) |
| Quando não dá para trocar no PDF: cobrir e escrever por cima, com aviso de que o layout muda e o texto antigo continua no arquivo | Concluído | Automático |
| **Imagens**: inserir, mover, redimensionar, recortar, girar, transparência, excluir, substituir (PNG, JPG, BMP, TIFF, GIF) | Concluído | Automático (recorte, rotação e transparência conferidos nos pixels) |
| **Anotações** com aparência própria: marca-texto, sublinhado, tachado (ajustados às linhas de texto), comentário, nota adesiva, caixa de texto, seta, linha, retângulo, círculo, desenho livre | Concluído | Automático (tipo, aparência e conteúdo de cada anotação) |
| **Assinatura visual**: desenhar com o mouse, importar PNG/JPG, posicionar, redimensionar, modelos guardados localmente; aviso de que não é assinatura digital | Concluído | Biblioteca: automático; desenho com o mouse: manual pendente |
| **Carimbos**: biblioteca local com CONFIDENCIAL, CÓPIA, ORIGINAL, DOCUMENTO ANALISADO, RECEBIDO e RASCUNHO; criar, editar, excluir, restaurar exemplos; data opcional | Concluído | Automático |
| **Marca d'água** de texto ou imagem: transparência, rotação, 9 posições, tamanho, páginas escolhidas, por cima ou por baixo do conteúdo; vários PDFs | Concluído | Automático + execução pela interface |
| **Cabeçalhos e rodapés**: esquerda/centro/direita, campos {arquivo}, {data}, {hora}, {pagina}, {total}, primeiro número, margens, páginas escolhidas; vários PDFs | Concluído | Automático + execução pela interface |
| **Formulários**: criar campo de texto (uma ou várias linhas), caixa de seleção, botões de opção, lista suspensa, campo de data, botão (limpar ou imprimir) | Concluído | Automático (campos relidos com tipo, opções e valores) |
| Preencher formulários existentes e gravar uma cópia preenchida (aparência refeita) | Concluído | Automático + execução pela interface |
| Gravação sempre num novo arquivo (`nome_editado.pdf`); original nunca alterado | Concluído | Automático |
| "Editar" no visualizador abre o documento no editor | Concluído | Manual pendente |

### Limitações conhecidas da Fase 5

| Item | Situação |
|---|---|
| Edição de texto existente | Só troca no próprio PDF quando a fonte é simples (WinAnsi/ASCII), o trecho está num único comando de texto e os caracteres novos existem na fonte. Fontes compostas (Unicode/CID), muito comuns em PDFs gerados pelo Word, caem na sobreposição — que **não apaga** o texto antigo (ele continua selecionável por baixo). Não use para ocultar dados: a tarja definitiva (redação) não existe ainda. |
| Troca no próprio PDF | Se o texto novo for maior ou menor, o espaço até o trecho seguinte da linha muda; os ajustes de espaçamento (TJ) do trecho trocado são descartados. |
| Imagens que já existiam no PDF | Não podem ser movidas, substituídas ou excluídas; as ferramentas de imagem valem para as imagens inseridas no editor. |
| Anotações que já existiam no PDF | Aparecem na página, mas não podem ser editadas ou excluídas no editor. |
| Prévia na tela | Os itens são desenhados pelo WPF sobre a página; o resultado gravado é feito pelo PDFsharp e pode diferir em detalhes (quebra de linha, espaçamento). |
| Rotação de texto inserido | Não implementada (imagens e marca d'água giram). |
| Campos de data e botão "Imprimir" | Dependem do leitor de PDF executar JavaScript (Adobe Reader e similares). O PDF Master não executa JavaScript. |
| Formulários XFA | Ao preencher um formulário híbrido, a parte XFA é removida para que os valores apareçam em todos os leitores; formulários só XFA não são suportados. |
| Fontes de anotações e campos | Caixas de texto e campos usam Helvetica (fonte padrão do PDF): caracteres fora do Latin-1 aparecem como "?" na aparência dos campos. |
| PDFs protegidos por senha | Não abrem no editor: use "Remover proteção" (com a senha) e abra a cópia. |

### Extra — Compressão de vídeo (pedido do autor, fora das 8 fases)

| Funcionalidade | Estado | Testes |
|---|---|---|
| Aba "Comprimir vídeo" no menu lateral; pesquisa por "reduzir vídeo", "mp4", "360p" etc. | Concluído | Automático (catálogo e pesquisa) + captura de tela |
| Aceita qualquer formato que o FFmpeg abra | Concluído | Automático: AVI (MPEG-4 + AC-3), MPG (MPEG-2), WMV, MOV (MJPEG + PCM), FLV, MKV (FFV1 + FLAC), 3GP (H.263), MP4 |
| Saída MP4 com H.264 + AAC, lado menor com 360 pixels, proporção mantida, dimensões pares | Concluído | Automático (codec, tamanho e áudio conferidos no arquivo gerado) |
| Vídeo vertical, vídeo de celular com rotação, pixels não quadrados (DVD) | Concluído | Automático |
| Vídeos menores que 360p não são ampliados; no máximo 30 quadros por segundo | Concluído | Automático |
| Índice no início do arquivo (reprodução antes do download completo) | Concluído | Automático (ordem dos blocos do MP4 conferida) |
| Recusa clara de áudio puro, imagem e arquivo que não é vídeo, sem deixar arquivos | Concluído | Automático |
| Nomes com acentos, espaços e símbolos; nomes de saída nunca colidem | Concluído | Automático |
| Fila de vários vídeos com progresso, velocidade, tempo restante e falha isolada por vídeo | Concluído | Um vídeo: execução pela interface; vários: manual pendente |
| Cancelamento: o FFmpeg é encerrado e nada fica na pasta de destino | Concluído | Automático |
| FFmpeg LGPL embutido, extraído na primeira utilização e conferido por SHA-256 | Concluído | Automático + execução pela interface |
| "Localizar ffmpeg.exe…" (outro FFmpeg) e "Usar o FFmpeg incluído" | Concluído | Validação: automático; botões: manual pendente |
| Crédito do autor (rodapé, Configurações → Sobre, propriedades do .exe) | Concluído | Captura de tela |

### Limitações conhecidas da compressão de vídeo

| Item | Situação |
|---|---|
| Codificador H.264 | A variante LGPL não tem a libx264; usa o OpenH264 (ou o do Windows). Para o mesmo tamanho, a qualidade é um pouco inferior à da libx264. Quem tiver um FFmpeg com libx264 pode indicá-lo em "Localizar ffmpeg.exe…", e ele passa a ser usado. |
| Qualidade fixa | 360p, cerca de 700 kbit/s de vídeo e 96 kbit/s de áudio estéreo; não há opções de qualidade na tela (foi o pedido). |
| Vídeo que já é pequeno | Pode ficar maior que o original; a tela avisa ("ficou x% maior"), e o arquivo é gravado mesmo assim porque o objetivo também é padronizar em MP4 360p. |
| HDR (10 bits) | Convertido para 8 bits sem mapeamento de tons: as cores podem ficar mais apagadas. |
| Legendas, capítulos, faixas extras de áudio | Não são levadas: só a primeira faixa de vídeo e a primeira de áudio. |
| Desempenho | Um vídeo por vez, sem aceleração por placa de vídeo. O FFmpeg roda com prioridade baixa para não travar o computador. |
| Versão do FFmpeg | O CI baixa a compilação mais recente do ramo 9.0 de BtbN (na última execução: n9.0.2, com o codificador OpenH264); a versão exata e o SHA-256 ficam em `ORIGEM.txt`, ao lado do executável extraído. |

### Fase 4 — OCR e digitalização

| Funcionalidade | Estado | Testes |
|---|---|---|
| **OCR** de PDFs digitalizados e de imagens (JPG, PNG, BMP, TIFF, GIF) com Tesseract 5, tudo no computador | Concluído | Automático (texto reconhecido conferido) + execução pela interface |
| **PDF pesquisável**: camada de texto invisível sobre a imagem; aparência da página inalterada | Concluído | Automático (texto, posição da palavra e pixels conferidos) |
| Idiomas incluídos: português, inglês e espanhol; vários idiomas ao mesmo tempo | Concluído | Português: automático; inglês, espanhol e combinações: manual pendente |
| Instalar outros idiomas a partir de um arquivo `.traineddata` | Concluído | Automático (validação e instalação); botão na tela: manual pendente |
| Correção automática de orientação (páginas deitadas ou de cabeça para baixo) | Concluído | Automático (90°, 180° e 270°) |
| Correção de inclinação (até 7°), realce de contraste e redução de ruído | Concluído | Automático |
| Páginas que já têm texto são puladas (ou reconhecidas de novo, se pedido) | Concluído | Automático |
| **Extração de texto** do reconhecimento para TXT e Word (DOCX) | Concluído | Automático |
| Seleção de páginas e resolução de 100 a 600 DPI | Concluído | Validação: automático; seleção de páginas: manual pendente |
| **OCR em lote**: fila de documentos, falha isolada por arquivo, progresso por página, tempo decorrido e estimado | Concluído | Um documento: execução pela interface; vários documentos: manual pendente |
| Cancelamento sem deixar arquivo parcial; original nunca alterado | Concluído | Automático |
| **Digitalizar**: montar PDF a partir de páginas (scanner ou imagens), reordenar, excluir, OCR opcional | Concluído | Com imagens: execução pela interface |
| Digitalizar com scanner WIA: mesa, alimentador, frente e verso, cor, resolução | **Implementado, aguardando teste com scanner real** | Somente erros (sem scanner, dispositivo inexistente): automático. Nenhuma digitalização real foi feita: o CI não tem scanner. |

### Limitações conhecidas da Fase 4

| Item | Situação |
|---|---|
| Precisão do OCR | Depende da qualidade da digitalização. Não há garantia de acerto; a tela mostra a confiança média informada pelo Tesseract. Manuscritos não são reconhecidos de forma útil. |
| Modelos de idioma | São os modelos "fast" do Tesseract (menores e mais rápidos, um pouco menos precisos que os "best"). Os modelos "best" podem ser instalados pela tela. |
| Camada de texto com alfabetos não latinos | A camada invisível usa a codificação WinAnsi: grego, cirílico, árabe, chinês etc. são reconhecidos e saem corretos no TXT/DOCX, mas no PDF pesquisável aparecem como "?". |
| Orientação automática | Feita comparando o reconhecimento nas 4 posições (mais lento que a primeira passada). Páginas com pouquíssimo texto podem não ser corrigidas. |
| Scanner | Somente WIA. TWAIN não foi implementado. Corte automático de bordas e detecção do tamanho do papel não existem. O comportamento com alimentador e frente e verso varia conforme o driver e não foi testado. |
| Tabelas e colunas | O texto extraído segue a ordem de leitura do Tesseract; tabelas não são reconstruídas. |
| Padrões de OCR na tela de Configurações | Ainda não existem; a tela de OCR abre com valores fixos (Fase 7). |
| Desempenho | Uma página por vez (cerca de 1 a 4 s por página a 300 DPI, conforme o computador); sem paralelismo ainda. |

### Fase 3 — Conversão e compressão

| Funcionalidade | Estado | Testes |
|---|---|---|
| **PDF para imagem**: PNG, JPG, BMP, TIFF; DPI, qualidade, escala de cinza, seleção de páginas | Concluído | Automático (pixels, tamanho e DPI conferidos) + execução pela interface |
| PDF para TIFF de várias páginas em um único arquivo | Concluído | Automático |
| **Imagem para PDF**: JPG, PNG, BMP, TIFF de várias páginas, GIF | Concluído | Automático + execução pela interface (GIF: manual pendente) |
| Imagem para PDF: tamanho da página (imagem, A4, Carta), orientação, margem | Concluído | Automático |
| Imagem para PDF: JPG incorporado sem recompressão; orientação EXIF respeitada | Concluído | Automático |
| **Texto para PDF** (interno): quebra de linhas, várias páginas, UTF-8/UTF-16/Latin-1 | Concluído | Automático + execução pela interface |
| **Word e Excel para PDF** via LibreOffice | Concluído | Automático (DOCX e XLSX reais, texto conferido no PDF) + execução pela interface (DOCX) |
| **PowerPoint, ODF e RTF para PDF** via LibreOffice | Concluído | Manual pendente (mesmo caminho do Word/Excel, sem arquivo de teste próprio) |
| **HTML para PDF** via LibreOffice | Concluído | Automático |
| **PDF para Word** — texto corrido (interno) | Concluído | Automático + execução pela interface |
| PDF para Word — manter a aparência (LibreOffice) | Concluído | Automático |
| **PDF para TXT e HTML** | Concluído | Automático + execução pela interface (TXT) |
| Aviso quando o PDF não tem texto (digitalizado) em vez de gerar arquivo vazio | Concluído | Automático |
| Aviso de dependência ausente quando o LibreOffice não está instalado | Concluído | Manual pendente (o CI tem o LibreOffice) |
| **Comprimir PDF**: perfis leve, equilibrada, forte, máxima | Concluído | Automático (redução, legibilidade e original intacto conferidos) + execução pela interface |
| Comprimir: perfil sem perda (não toca nas imagens) e personalizado (qualidade, DPI, cinza) | Concluído | Automático |
| Comprimir: remoção de metadados | Concluído | Automático |
| Comprimir: tamanhos, economia, percentual e comparação visual antes/depois | Concluído | Captura de tela |
| Comprimir: nada é gravado quando não há redução; original nunca substituído | Concluído | Automático |
| Conversão em lote com falha isolada por arquivo | Concluído | Manual pendente (lógica simples na tela; os conversores são testados um a um) |
| Cancelamento sem deixar arquivo parcial | Concluído | Automático |
| Renderização com PDFium atual, lendo o arquivo sob demanda | Concluído | Automático |

### Limitações conhecidas da Fase 3

| Item | Situação |
|---|---|
| PDF para Word | Não existe conversão fiel. O modo texto corrido leva só o texto (sem imagens, tabelas, colunas ou fontes); o modo com aparência usa caixas de texto posicionadas. A tela diz isso antes de converter. |
| PDF para Excel e PowerPoint | Não implementado. Replanejado para a Fase 6, com a extração de tabelas. |
| PDF para SVG e PDF/A | Não implementados (PDF/A está na Fase 6). |
| Office para PDF | Depende do LibreOffice instalado; a fidelidade é a dele. Fontes ausentes e recursos exclusivos do Microsoft Office podem mudar a formatação. |
| HTML para PDF | Feito pelo LibreOffice, com suporte limitado a CSS moderno. Páginas que referenciam conteúdo da internet podem acessá-la durante a conversão. |
| Texto para PDF | Página A4 e duas fontes (largura fixa ou Segoe UI); tamanho de página, margens e fonte ainda não são configuráveis na tela. |
| Compressão | Só regrava imagens RGB e em tons de cinza de 8 bits. CMYK, indexadas, 1 bit, JBIG2, JPEG 2000 e imagens com máscara por cor ficam como estão. Sem Ghostscript, os perfis fortes podem render menos que os do PDF24 em alguns documentos. |
| Compressão e resolução | A resolução de cada imagem é estimada pelo tamanho da página, não pelo tamanho em que a imagem é desenhada; por isso a redução nunca passa do pedido, mas imagens pequenas na página podem ser reduzidas menos do que poderiam. |
| Compressão de PDFs assinados | A cópia comprimida não mantém a validade da assinatura digital; o programa avisa no resultado. |
| Otimização de fontes | Não implementada (subconjuntos de fontes não são recalculados). |
| Configurações padrão de conversão e compressão na tela de Configurações | Ainda não existem; cada tela abre com valores padrão fixos. |
| Pré-visualização antes de comprimir | A comparação aparece depois da compressão, não antes. |

### Fase 2 — Visualizador e organização

| Funcionalidade | Estado | Testes |
|---|---|---|
| Motor de PDF: ler páginas, tamanhos, rotação e marcadores | Concluído | Automático |
| Motor de PDF: montar novo documento a partir de páginas de vários arquivos | Concluído | Automático |
| Renderização de páginas (PDFium), em escala ou ajustada a uma caixa | Concluído | Automático (pixels conferidos) |
| Cache de miniaturas em memória, carregadas em segundo plano | Concluído | Captura de tela |
| **Visualizador**: página central, miniaturas, zoom, ajustar à largura, navegação | Concluído | Captura de tela (abertura e renderização); botões e atalhos: manual pendente |
| **Mesclar PDF**: vários arquivos, ordem, páginas por arquivo, nome e pasta de saída | Concluído | Automático + execução pela interface |
| Mesclar: preserva tamanho e orientação de cada página | Concluído | Automático |
| Mesclar: um marcador por arquivo de origem | Concluído | Automático |
| **Dividir PDF**: uma página por arquivo, a cada N páginas, por intervalos, em pontos de corte, por marcadores | Concluído | Automático + execução pela interface (uma página por arquivo) |
| Dividir por tamanho aproximado (medido de verdade, não estimado) | Concluído | Automático |
| Dividir: padrão de nomes (`{nome}_{n:000}` etc.) | Concluído | Automático |
| **Extrair páginas**: seleção visual ou por intervalo ("1-5", "1,3,5,7", "2-8,12,15-20") | Concluído | Analisador e gravação: automático; seleção na tela: manual pendente |
| **Organizar páginas**: mover, girar, duplicar, excluir, página em branco, inserir de outro PDF | Concluído | Gravação do arranjo: automático + execução pela interface; cada botão: manual pendente |
| Organizar: arrastar e soltar miniaturas, seleção múltipla, desfazer/refazer | Concluído | Manual pendente |
| **Intercalar documentos**: 1 a 1, em blocos, ordem inversa, sobras no final ou descartadas | Concluído | Automático + execução pela interface |
| Originais nunca alterados; saída nunca coincide com um arquivo de origem | Concluído | Automático |
| Cancelamento sem deixar arquivo parcial | Concluído | Automático |
| Erros claros para arquivo que não é PDF, inexistente ou com intervalo inválido | Concluído | Automático |
| Histórico e notificação com "Abrir arquivo" / "Abrir pasta" para cada operação | Concluído | Execução pela interface (registros conferidos na captura do Histórico) |
| Ferramentas mais utilizadas / recomendadas na página inicial | Concluído | Captura de tela |
| Linha de comando: `--tool`, `--output`, `--run` | Concluído | Execução pela interface |

### Limitações conhecidas da Fase 2

| Item | Situação |
|---|---|
| Marcadores do documento original ao mesclar | Não são copiados. O programa cria um marcador por arquivo. Copiar a árvore original fica para a Fase 6 (ferramenta de marcadores). |
| Marcadores com destino nomeado | Não são reconhecidos na divisão por marcadores (só destinos diretos para a página). |
| Links, formulários e anexos | As páginas são copiadas com seu conteúdo e anotações; estruturas do documento (campos de formulário, anexos) não são garantidas no resultado. |
| PDFs protegidos por senha | Não abrem ainda; a mensagem informa que senha e reparo chegam na Fase 6. |
| Documentos com milhares de páginas no editor de páginas | Todas as miniaturas ficam na tela (sem virtualização em grade). Funciona, mas consome memória; o visualizador já virtualiza a lista. |
| Arrastar várias páginas de uma vez | O arrasto move a seleção atual, mas clicar para arrastar reduz a seleção a uma página; para mover várias, use os botões "Mover". |
| "Substituir páginas" | Feita em dois passos (excluir + inserir); não há um comando único. |

### Fase 1 — Fundação

| Funcionalidade | Estado | Testes |
|---|---|---|
| Solução `.sln` com 15 projetos, compila sem erros nem avisos | Concluído | Automático |
| Janela principal: barra superior, menu lateral com 11 seções, área principal | Concluído | Captura de tela |
| Tema claro e escuro, salvo entre sessões | Concluído | Captura de tela (os dois temas); botão de alternância: manual pendente |
| Cor de destaque personalizável | Concluído | Persistência e validação: automático; efeito visual: manual pendente |
| Catálogo de 44 ferramentas com a fase prevista de cada uma | Concluído | Automático |
| Pesquisa por nome, descrição e sinônimos, sem diferenciar acentos | Concluído | Automático |
| Banco SQLite: configurações, histórico, arquivos recentes | Concluído | Automático |
| Abrir arquivos, importar pasta, arrastar e soltar, arquivos pela linha de comando | Concluído | Linha de comando: captura de tela; diálogo e arrastar: manual pendente |
| Validação de arquivos e detecção de duplicados | Concluído | Automático + captura de tela |
| Copiar arquivos para pasta com renomeação automática, pular ou substituir | Concluído | Serviço: automático; fluxo pela interface: manual pendente |
| Proteção contra sobrescrita acidental e caminhos maliciosos | Concluído | Automático |
| Histórico: pesquisar, filtrar, abrir, excluir, limpar, desativar | Concluído | Repositório: automático; ações na tela: manual pendente |
| Notificações internas com ações | Concluído | Captura de tela |
| Configurações (aparência, arquivos, privacidade) | Concluído | Captura de tela |
| Log local e tratamento central de exceções | Concluído | Execução no CI |
| Modo portátil (`portable.flag`) | Concluído | Manual pendente |

### Pendentes das fases concluídas (adiados de propósito)

| Item | Motivo |
|---|---|
| Padrões de conversão (formato de imagem, DPI etc.) nas Configurações | Compressão, OCR e desempenho entraram na Fase 7; os de conversão continuam fixos em cada tela. |
| Idioma da interface | Somente português por enquanto. |
| Ícone próprio do aplicativo e logotipo definitivo | O ícone do `.exe` ainda é o padrão. |
| Cache de miniaturas em disco | O cache atual é em memória; em disco será avaliado na Fase 8 (otimização). |

### Fase 8

Quatro ferramentas do catálogo estão **pendentes** e aparecem na interface como "Prevista para a
Fase 8", desabilitadas. A lista completa está em `PDFMaster.Core/Tools/ToolCatalog.cs`.

## Testes realizados

| Módulo | Testes | Resultado |
|---|---|---|
| Core — pesquisa e catálogo | 23 | Aprovado |
| Core — nomes de saída e segurança de caminhos | 20 | Aprovado |
| Core — configurações, formatação, tipos de arquivo | 20 | Aprovado |
| Core — intervalos de páginas, divisão, intercalação, nomes | 34 | Aprovado |
| Storage — SQLite (inclusive atualização do esquema 1 → 2) | 7 | Aprovado |
| Infrastructure — inspeção, varredura e cópia de arquivos | 17 | Aprovado |
| PdfEngine — motor (leitura, montagem, rotação, marcadores, erros, cancelamento) | 11 | Aprovado |
| PdfEngine — organizador (mesclar, intercalar, dividir, salvar arranjo) | 10 | Aprovado |
| Rendering — renderização de páginas (PDFium atual) | 6 | Aprovado |
| Conversion — PDF para imagem | 6 | Aprovado |
| Conversion — imagem para PDF | 6 | Aprovado |
| Conversion — texto para PDF e PDF para TXT/HTML/DOCX | 7 | Aprovado |
| Conversion — LibreOffice (Word, Excel, HTML → PDF; PDF → DOCX com layout) | 2 | Aprovado |
| Compression — compressão de PDF | 6 | Aprovado |
| OCR — imagem, geometria e camada de texto | 9 | Aprovado |
| OCR — reconhecimento real (PDF pesquisável, orientação, inclinação, TXT/DOCX, idiomas, erros) | 9 | Aprovado |
| Scanner — erros sem aparelho (nenhuma digitalização real) | 2 | Aprovado |
| Vídeo — leitura do relatório do FFmpeg, tamanho de saída, argumentos | 19 | Aprovado |
| Vídeo — compressão real (formatos, rotação, proporção, quadros, erros, cancelamento, FFmpeg escolhido) | 13 | Aprovado |
| Editor — geometria de página e formatação do PDF | 6 | Aprovado |
| Editor — texto, imagens, anotações, texto existente, marca d'água, cabeçalho/rodapé, formulários (pixels e conteúdo conferidos, 4 rotações) | 18 | Aprovado |
| Editor — bibliotecas de carimbos e assinaturas, itens | 3 | Aprovado |
| Security — proteção por senha, permissões e remoção de proteção | 4 | Aprovado |
| Security — metadados (editar e remover) | 2 | Aprovado |
| Security — verificação criptográfica de assinaturas | 6 | Aprovado |
| Security — diagnóstico e reparo | 5 | Aprovado |
| Security — PDF/A (conversão nos 3 níveis validada pelo veraPDF, recusas, validação) | 11 | Aprovado |
| Comparison — comparação de PDFs e relatório | 6 | Aprovado |
| Amostras para o teste de inicialização (Fase 6) | 1 | Aprovado |
| Batch — fluxos, ferramentas de página, fila, pastas monitoradas, menu de contexto, linha de comando | 17 | Aprovado |
| Amostras para o teste de inicialização (Fase 7) | 1 | Aprovado |
| App — execução das ferramentas pela interface (`--run`) | 26 | Aprovado |
| App — linha de comando sem janela (códigos de saída e arquivos gerados), `--watch`, menu de contexto | 17 | Aprovado |
| App — abertura das telas nos dois temas | 28 | Aprovado |

Total de testes automatizados: 313 (mais 54 execuções da interface do programa publicado no CI, 15 comandos da linha de comando, uma execução de `--watch`, a pasta monitorada com o programa aberto e o menu de contexto com dois processos).

Os números da última execução ficam no ramo `ci-results` (`test.log`, `smoke.log` e capturas de tela).

## Bugs conhecidos

- Nenhum confirmado. A interface ainda não foi operada por uma pessoa; ver "Manual pendente".
- O executável não é assinado digitalmente; o Windows SmartScreen pode avisar na primeira execução.

## Dependências utilizadas

| Pacote | Versão | Licença |
|---|---|---|
| PDFsharp | 6.2.4 | MIT |
| bblanchon.PDFium.Win32 (binário do PDFium) | 152.0.7961 | BSD-3-Clause / Apache-2.0 |
| PdfPig | 0.1.14 | Apache-2.0 |
| Tesseract (wrapper .NET de charlesw, com Tesseract 5.2 e Leptonica nativos) | 5.2.0 | Apache-2.0 |
| Modelos tessdata_fast: por, eng, spa (baixados na compilação, incorporados ao programa) | — | Apache-2.0 |
| FFmpeg, compilação estática LGPL de BtbN/FFmpeg-Builds, ramo 9.0 (baixado na compilação, incorporado; executado como processo separado) | 9.0.x | LGPL-2.1 |
| WIA (componente do Windows, usado por COM; nada é distribuído) | — | — |
| Perfil de cor sRGB do Windows (usado no PDF/A; lido do sistema, não distribuído) | — | — |
| veraPDF (opcional, instalado pelo usuário; o CI o instala só para validar os testes; não distribuído) | 1.30.4 no CI | GPLv3+ / MPLv2+ |
| LibreOffice (opcional, instalado pelo usuário; não distribuído) | — | MPL-2.0 |
| CommunityToolkit.Mvvm | 8.4.0 | MIT |
| Microsoft.Data.Sqlite | 10.0.0 | MIT |
| SQLitePCLRaw.lib.e_sqlite3 (SQLite nativo) | 3.50.3 | Apache-2.0 / domínio público |
| System.Security.Cryptography.Pkcs (também usado na verificação de assinaturas) | 10.0.13 | MIT |
| Microsoft.Extensions.DependencyInjection, .Logging | 10.0.0 | MIT |
| Serilog, Serilog.Sinks.File, Serilog.Extensions.Logging | 4.2.0 / 6.0.0 / 9.0.0 | Apache-2.0 |
| xunit, xunit.runner.visualstudio, Microsoft.NET.Test.Sdk (somente testes) | 2.9.2 / 2.8.2 / 17.12.0 | Apache-2.0 / MIT |

## Problemas de licença

Nenhum nas dependências em uso. Decisões sobre iText, Ghostscript, QuestPDF e veraPDF estão em
`docs/ARQUITETURA.md`, seção 3.

## Avisos de segurança

- `SQLitePCLRaw.lib.e_sqlite3` 2.1.11 (GHSA-2m69-gcr7-jv3q): corrigido fixando a versão 3.50.3.
- `System.Security.Cryptography.Pkcs` 8.0.1, trazido pelo PDFsharp (GHSA-846f-f764-76mx e
  GHSA-2fmv-rv2c-59x7): corrigido fixando a versão 10.0.13. O build está sem avisos.

## Última versão compilada

0.7.0 — .NET 10, `win-x64`, executável único autossuficiente, gerado pelo workflow `build`.
