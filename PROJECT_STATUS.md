# PDF Master — estado do projeto

Atualizado em 10/11/2026 · Versão 0.2.0

## Fases

| Fase | Conteúdo | Estado |
|---|---|---|
| 1 — Fundação | Solução, arquitetura, interface, temas, menu, pesquisa, SQLite, gerenciamento de arquivos | **Concluída** |
| 2 — Visualizador e organização | Visualizador, miniaturas, mesclar, dividir, extrair, girar, reordenar, excluir, intercalar | **Concluída** |
| 3 — Conversão e compressão | PDF ↔ imagens, Office → PDF, PDF → Word, compressão | Próxima |
| 4 — OCR e digitalização | | Pendente |
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
| Configurações de conversão, OCR, compressão e desempenho | Entram com as respectivas ferramentas. |
| Idioma da interface | Somente português por enquanto. |
| Ícone próprio do aplicativo e logotipo definitivo | O ícone do `.exe` ainda é o padrão. |
| Cache de miniaturas em disco | O cache atual é em memória; em disco será avaliado na Fase 8 (otimização). |

### Fases 3 a 8

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
| Rendering — renderização de páginas | 6 | Aprovado |
| App — execução das ferramentas pela interface (`--run`) | 4 | Aprovado |
| App — abertura das telas nos dois temas | 11 | Aprovado |

Os números da última execução ficam no ramo `ci-results` (`test.log`, `smoke.log` e capturas de tela).

## Bugs conhecidos

- Nenhum confirmado. A interface ainda não foi operada por uma pessoa; ver "Manual pendente".
- O executável não é assinado digitalmente; o Windows SmartScreen pode avisar na primeira execução.

## Dependências utilizadas

| Pacote | Versão | Licença |
|---|---|---|
| PDFsharp | 6.2.0 | MIT |
| Docnet.Core (inclui o binário do PDFium) | 2.6.0 | MIT (PDFium: BSD-3-Clause / Apache-2.0) |
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

0.2.0 — .NET 10, `win-x64`, executável único autossuficiente, gerado pelo workflow `build`.
