# PDF Master — estado do projeto

Atualizado em 10/11/2026 · Versão 0.1.0

## Fases

| Fase | Conteúdo | Estado |
|---|---|---|
| 1 — Fundação | Solução, arquitetura, interface, temas, menu, pesquisa, SQLite, gerenciamento de arquivos | **Concluída** |
| 2 — Visualizador e organização | Visualizador, miniaturas, mesclar, dividir, extrair, girar, reordenar, excluir, intercalar | Próxima |
| 3 — Conversão e compressão | | Pendente |
| 4 — OCR e digitalização | | Pendente |
| 5 — Editor | | Pendente |
| 6 — Segurança e ferramentas avançadas | | Pendente |
| 7 — Automação | | Pendente |
| 8 — Finalização | | Pendente |

## Funcionalidades

"Testes" indica como a funcionalidade foi verificada: **Automático** = testes do `PDFMaster.Tests`
aprovados no CI; **Inicialização** = conferido nas capturas de tela do executável publicado no CI;
**Manual pendente** = depende de clique/arrasto e ainda precisa ser conferido por uma pessoa.

### Fase 1

| Funcionalidade | Estado | Testes |
|---|---|---|
| Solução `.sln` com 15 projetos, compila sem erros | Concluído | Automático |
| Janela principal: barra superior, menu lateral com 11 seções, área principal | Concluído | Inicialização |
| Tema claro e escuro, alternância em tempo de execução, salvo entre sessões | Concluído | Inicialização (os dois temas); alternância pelo botão: manual pendente |
| Cor de destaque personalizável (8 predefinidas + código #RRGGBB) | Concluído | Persistência e validação: automático; efeito visual: manual pendente |
| Catálogo com as 43 ferramentas da especificação e fase prevista de cada uma | Concluído | Automático |
| Pesquisa de ferramentas por nome, descrição e sinônimos, sem diferenciar acentos | Concluído | Automático (ex.: "juntar documentos" → Mesclar PDF) |
| Banco SQLite: configurações, histórico, arquivos recentes | Concluído | Automático |
| Abrir arquivos, importar pasta (recursivo), arrastar e soltar, arquivos pela linha de comando | Concluído | Linha de comando: inicialização; diálogo e arrastar: manual pendente |
| Validação de arquivos: tipo aceito, cabeçalho de PDF, vazio, ilegível, inexistente | Concluído | Automático |
| Detecção de duplicados (conteúdo idêntico, SHA-256) e de nomes repetidos | Concluído | Automático (hash) + inicialização |
| Copiar arquivos para pasta com renomeação automática, pular ou substituir | Concluído | Serviço: automático; fluxo pela interface: manual pendente |
| Proteção contra sobrescrita acidental e contra caminhos maliciosos | Concluído | Automático |
| Organização dos resultados em subpastas (por data ou por operação) | Concluído | Automático |
| Pasta padrão e lembrar última pasta | Concluído | Persistência: automático; diálogos: manual pendente |
| Cancelamento de importação e de cópia, sem deixar arquivos parciais | Concluído | Automático |
| Histórico: pesquisar, filtrar por data e tipo, abrir arquivo/pasta, excluir, limpar, desativar | Concluído | Repositório: automático; tela vazia: inicialização; ações: manual pendente |
| Notificações internas com ações (ex.: "Abrir pasta") | Concluído | Inicialização (aviso de importação) |
| Configurações (aparência, arquivos, privacidade) | Concluído | Inicialização |
| Log estruturado local com rotação diária; tratamento central de exceções | Concluído | Inicialização (log gerado) |
| Modo portátil (`portable.flag` ao lado do executável) | Concluído | Manual pendente |
| Compilação, testes e publicação automáticos no GitHub Actions | Concluído | — |

### Pendentes da Fase 1 (adiados de propósito)

| Item | Motivo |
|---|---|
| "Ferramentas mais utilizadas" e "recomendadas" na página inicial | Só fazem sentido com ferramentas disponíveis; entram na Fase 2 para não exibir dados fictícios. |
| "Repetir operação" no histórico | Depende do sistema de tarefas (Fase 7). |
| Configurações de conversão, OCR, compressão e desempenho | Entram com as respectivas ferramentas, para não haver opções sem efeito. |
| Idioma da interface | Somente português por enquanto. |
| Ícone próprio do aplicativo e logotipo definitivo | Hoje há um logotipo simples desenhado na interface; o ícone do `.exe` é o padrão. |

### Fases 2 a 8

Todas as ferramentas de PDF estão **pendentes** e aparecem na interface como "Prevista para a Fase N",
sem ação ao clicar. A lista completa está em `PDFMaster.Core/Tools/ToolCatalog.cs`.

## Testes realizados

| Módulo | Testes | Resultado |
|---|---|---|
| Core — pesquisa e catálogo | 18 | Aprovado |
| Core — nomes de saída e segurança de caminhos | 20 | Aprovado |
| Core — configurações, formatação, tipos de arquivo | 20 | Aprovado |
| Storage — SQLite (configurações, histórico, recentes, injeção de SQL) | 6 | Aprovado |
| Infrastructure — inspeção de arquivos e varredura de pastas | 8 | Aprovado |
| Infrastructure — cópia de arquivos (políticas, falhas, cancelamento) | 9 | Aprovado |
| App — inicialização do executável publicado (6 execuções, 2 temas) | 6 | Aprovado |

Os números e resultados da última execução ficam no ramo `ci-results` (`test.log`, `smoke.log` e capturas de tela).

## Bugs conhecidos e limitações

- A interface não foi operada por uma pessoa ainda: cliques, diálogos de arquivo e arrastar e soltar
  não são cobertos pelo CI (ver "Manual pendente" acima).
- Não há automação de interface (cliques simulados) nos testes.
- O executável não é assinado digitalmente; o Windows SmartScreen pode avisar na primeira execução.

## Dependências utilizadas

| Pacote | Versão | Licença |
|---|---|---|
| CommunityToolkit.Mvvm | 8.4.0 | MIT |
| Microsoft.Data.Sqlite | 10.0.0 | MIT |
| SQLitePCLRaw.lib.e_sqlite3 (SQLite nativo) | 3.50.3 | Apache-2.0 / domínio público |
| Microsoft.Extensions.DependencyInjection, .Logging | 10.0.0 | MIT |
| Serilog, Serilog.Sinks.File, Serilog.Extensions.Logging | 4.2.0 / 6.0.0 / 9.0.0 | Apache-2.0 |
| xunit, xunit.runner.visualstudio, Microsoft.NET.Test.Sdk (somente testes) | 2.9.2 / 2.8.2 / 17.12.0 | Apache-2.0 / MIT |

## Problemas de licença

Nenhum nas dependências em uso. Decisões sobre iText, Ghostscript, QuestPDF e veraPDF estão em
`docs/ARQUITETURA.md`, seção 3.

## Avisos de segurança

- `SQLitePCLRaw.lib.e_sqlite3` 2.1.11 (trazido pelo Microsoft.Data.Sqlite) tem o alerta
  GHSA-2m69-gcr7-jv3q. Corrigido fixando a versão 3.50.3.

## Última versão compilada

0.1.0 — .NET 10, `win-x64`, executável único autossuficiente, gerado pelo workflow `build`.
