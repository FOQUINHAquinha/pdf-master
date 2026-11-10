# PDF Master — estado do projeto

Atualizado em 10/11/2026 · Versão 0.3.0

## Fases

| Fase | Conteúdo | Estado |
|---|---|---|
| 1 — Fundação | Solução, arquitetura, interface, temas, menu, pesquisa, SQLite, gerenciamento de arquivos | **Concluída** |
| 2 — Visualizador e organização | Visualizador, miniaturas, mesclar, dividir, extrair, girar, reordenar, excluir, intercalar | **Concluída** |
| 3 — Conversão e compressão | PDF ↔ imagens, Office → PDF, PDF → Word, compressão | **Concluída** |
| 4 — OCR e digitalização | OCR, PDF pesquisável, digitalização, OCR em lote | Próxima |
| 5 — Editor | | Pendente |
| 6 — Segurança e ferramentas avançadas | | Pendente |
| 7 — Automação | | Pendente |
| 8 — Finalização | | Pendente |

## Como cada item foi verificado

- **Automático**: testes do `PDFMaster.Tests` aprovados no CI, com PDFs reais gerados e relidos.
- **Execução pela interface**: o executável publicado é aberto no CI com `--tool … --run`; a tela
  carrega os arquivos, executa a ação principal e o CI confere os arquivos gerados.
- **Captura de tela**: a tela foi conferida visualmente nas capturas do CI.
- **Manual pendente**: depende de clique, arrasto ou diálogo do Windows e ainda precisa ser
  conferido por uma pessoa.

## Funcionalidades

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
| "Repetir operação" no histórico | Depende do sistema de tarefas (Fase 7). |
| Padrões de conversão e compressão na tela de Configurações | Adiados para a Fase 7, junto com as configurações de desempenho; hoje cada tela usa padrões fixos. |
| Configurações de OCR e desempenho | Entram com as respectivas ferramentas. |
| Idioma da interface | Somente português por enquanto. |
| Ícone próprio do aplicativo e logotipo definitivo | O ícone do `.exe` ainda é o padrão. |
| Cache de miniaturas em disco | O cache atual é em memória; em disco será avaliado na Fase 8 (otimização). |

### Fases 4 a 8

As demais ferramentas estão **pendentes** e aparecem na interface como "Prevista para a Fase N",
desabilitadas. A lista completa está em `PDFMaster.Core/Tools/ToolCatalog.cs`.

## Testes realizados

| Módulo | Testes | Resultado |
|---|---|---|
| Core — pesquisa e catálogo | 23 | Aprovado |
| Core — nomes de saída e segurança de caminhos | 20 | Aprovado |
| Core — configurações, formatação, tipos de arquivo | 20 | Aprovado |
| Core — intervalos de páginas, divisão, intercalação, nomes | 34 | Aprovado |
| Storage — SQLite | 6 | Aprovado |
| Infrastructure — inspeção, varredura e cópia de arquivos | 17 | Aprovado |
| PdfEngine — motor (leitura, montagem, rotação, marcadores, erros, cancelamento) | 11 | Aprovado |
| PdfEngine — organizador (mesclar, intercalar, dividir, salvar arranjo) | 10 | Aprovado |
| Rendering — renderização de páginas (PDFium atual) | 6 | Aprovado |
| Conversion — PDF para imagem | 6 | Aprovado |
| Conversion — imagem para PDF | 6 | Aprovado |
| Conversion — texto para PDF e PDF para TXT/HTML/DOCX | 7 | Aprovado |
| Conversion — LibreOffice (Word, Excel, HTML → PDF; PDF → DOCX com layout) | 2 | Aprovado |
| Compression — compressão de PDF | 6 | Aprovado |
| App — execução das ferramentas pela interface (`--run`) | 11 | Aprovado |
| App — abertura das telas nos dois temas | 11 | Aprovado |

Total de testes automatizados: 175.

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
| LibreOffice (opcional, instalado pelo usuário; não distribuído) | — | MPL-2.0 |
| CommunityToolkit.Mvvm | 8.4.0 | MIT |
| Microsoft.Data.Sqlite | 10.0.0 | MIT |
| SQLitePCLRaw.lib.e_sqlite3 (SQLite nativo) | 3.50.3 | Apache-2.0 / domínio público |
| System.Security.Cryptography.Pkcs | 10.0.13 | MIT |
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

0.3.0 — .NET 10, `win-x64`, executável único autossuficiente, gerado pelo workflow `build`.
