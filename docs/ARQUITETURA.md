# PDF Master — análise técnica e plano

Este documento responde à "Primeira tarefa do agente" da especificação: análise, arquitetura,
bibliotecas e licenças, estrutura da solução, plano, dificuldades técnicas e estratégia de testes.

## 1. Decisões principais

| Tema | Decisão | Motivo |
|---|---|---|
| Plataforma | .NET 10 (LTS), C#, WPF, Windows 10/11 x64 | A especificação pede ".NET 8 ou superior, LTS". O suporte do .NET 8 termina em novembro de 2026; o .NET 10 é a LTS atual. |
| Interface | WPF + MVVM (CommunityToolkit.Mvvm) com o tema Fluent nativo do WPF | Visual moderno sem biblioteca de terceiros; claro/escuro em tempo de execução. |
| Injeção de dependência | Microsoft.Extensions.DependencyInjection | Padrão do ecossistema. |
| Persistência | SQLite via Microsoft.Data.Sqlite | Configurações, histórico e recentes. Nunca conteúdo de documentos. |
| Logs | Serilog em arquivo local, rotação diária | Log estruturado, 100% local. |
| Distribuição | Executável único autossuficiente (não exige .NET instalado) | Simples de instalar e de levar em pendrive. |
| Compilação | GitHub Actions (Windows) a cada envio | Compila, roda os testes, publica o `.exe` e faz teste de inicialização. |

Para abrir a solução no Visual Studio é preciso o SDK do .NET 10 (Visual Studio 2022 17.14+ ou 2026).
Se isso for um problema, voltar para `net8.0` é trocar uma linha em cada `.csproj`, mas o .NET 8
deixa de receber correções de segurança.

## 2. Arquitetura

```
PDFMaster.App            Interface WPF (views, view models, temas, serviços de UI)
PDFMaster.Core           Regras sem E/S: catálogo, pesquisa, nomes de saída, configurações, contratos
PDFMaster.Domain         Tipos de dados (sem dependências)
PDFMaster.Infrastructure Arquivos, pastas, shell do Windows, logs
PDFMaster.Storage        SQLite: configurações, histórico, recentes
PDFMaster.PdfEngine      Abstração dos mecanismos de PDF            (Fase 2)
PDFMaster.Rendering      Renderização e miniaturas                  (Fase 2)
PDFMaster.Conversion     Conversões                                 (Fase 3)
PDFMaster.Compression    Compressão e otimização                    (Fase 3)
PDFMaster.OCR            OCR                                        (Fase 4)
PDFMaster.Scanner        WIA (TWAIN não implementado)               (Fase 4)
PDFMaster.Video          Compressão de vídeo com FFmpeg             (extra)
PDFMaster.Video.Ffmpeg   Só recursos: FFmpeg LGPL comprimido        (extra)
PDFMaster.Editor         Editor visual                              (Fase 5)
PDFMaster.Security       Senhas, permissões, assinaturas            (Fase 6)
PDFMaster.Batch          Fila de tarefas e fluxos                   (Fase 7)
PDFMaster.Tests          Testes unitários e de integração
PDFMaster.Installer      Instalador                                 (Fase 8)
```

Regras de dependência: `Domain` não depende de nada; `Core` depende só de `Domain`; os módulos
dependem de `Core`/`Domain`; apenas `App` conhece todos. A interface nunca executa trabalho pesado
na thread principal: view models chamam serviços assíncronos com `CancellationToken` e `IProgress`.

**Camada de abstração de PDF.** A interface e os demais módulos só conhecem dois contratos:
`IPdfEngine` (ler informações e montar documentos; hoje implementado por `PdfSharpEngine`) e
`IPdfRenderer` (transformar páginas em imagem; hoje `PdfiumRenderer`). Trocar de biblioteca é
escrever outra implementação desses contratos, sem tocar na interface.

Todas as ferramentas de organização são expressas como uma lista de `PageInstruction` — "copie a
página N do arquivo X, com tal rotação" ou "página em branco" — entregue a `IPdfEngine.ComposeAsync`.
Mesclar, dividir, extrair, girar, reordenar, excluir, intercalar e inserir são apenas formas
diferentes de montar essa lista (`PdfOrganizer` e os planejadores em `PDFMaster.Core/Pdf`), o que
mantém uma única rotina de gravação, bem testada. A gravação é sempre em arquivo temporário seguido
de renomeação, e o motor se recusa a gravar por cima de um arquivo de origem.

**Catálogo de ferramentas.** Todas as ferramentas da especificação estão em `ToolCatalog` com a fase
prevista. `IsAvailable` só vira `true` quando a ferramenta passa nos critérios da seção 21.4. Enquanto
isso ela aparece como "Prevista para a Fase N" e não é clicável — não há botões simulados.

**Segurança.** Nomes de saída são saneados (`PathSafety`) e conferidos contra a pasta de destino;
arquivos recebidos só são abertos se o tipo estiver na lista de formatos aceitos e nunca são
executados; PDFs têm o cabeçalho validado antes de qualquer uso; consultas SQL são parametrizadas.
Nenhum código do programa usa a rede.

## 3. Bibliotecas e licenças

Licenças conferidas pelo que os projetos publicam; cada uma será verificada de novo, na versão
exata, no momento em que a dependência for adicionada (regra 12).

### Em uso na Fase 1

| Biblioteca | Uso | Licença |
|---|---|---|
| CommunityToolkit.Mvvm | MVVM | MIT |
| Microsoft.Extensions.DependencyInjection / Logging | DI e logging | MIT |
| Microsoft.Data.Sqlite (+ SQLitePCLRaw, SQLite) | Banco local | MIT / Apache-2.0 / domínio público |
| Serilog, Serilog.Sinks.File, Serilog.Extensions.Logging | Log em arquivo | Apache-2.0 |
| xUnit, Microsoft.NET.Test.Sdk | Testes (não distribuídos) | Apache-2.0 / MIT |

### Em uso desde a Fase 2

| Biblioteca | Uso | Licença |
|---|---|---|
| PDFsharp 6.2 | Ler e montar documentos (mesclar, dividir, girar, marcadores) | MIT |
| PDFium (pacote bblanchon.PDFium.Win32, binário nativo) | Motor de renderização, chamado diretamente por P/Invoke. Substituiu o Docnet.Core na Fase 3 para usar uma compilação atual do PDFium. | BSD-3-Clause / Apache-2.0 |
| System.Security.Cryptography.Pkcs | Dependência do PDFsharp, fixada em versão corrigida | MIT |

### Em uso desde a Fase 3

| Biblioteca | Uso | Licença |
|---|---|---|
| PdfPig 0.1.14 | Extração de texto (PDF → TXT, HTML, DOCX) | Apache-2.0 |
| Codecs de imagem do Windows (WIC, via WPF) | Ler e gravar JPG, PNG, BMP, TIFF e GIF; recompressão de imagens na compressão | Parte do Windows |
| LibreOffice (instalado pelo usuário, não distribuído) | Office/ODF/RTF/HTML → PDF e PDF → DOCX com layout | MPL-2.0 |

Decisões da Fase 3: as imagens usam os codecs do próprio Windows, então **não foi preciso**
Magick.NET nem SkiaSharp; o DOCX de texto corrido é gravado pelo próprio programa (um ZIP com
XML), então **não foi preciso** o Open XML SDK; a compressão é feita sobre o PDFsharp, sem
Ghostscript nem qpdf.

### Compressão de vídeo (extra, fora das 8 fases)

| Finalidade | Escolha | Licença | Observação |
|---|---|---|---|
| Ler qualquer formato de vídeo e gravar MP4 (H.264 + AAC) | FFmpeg, compilação estática **LGPL** de BtbN/FFmpeg-Builds (ramo 9.0), baixada na compilação por `tools/obter-ffmpeg.ps1` | LGPL 2.1 | Executado como processo separado (sem ligação com o código do programa). Embutido gzip num assembly só de recursos, extraído na primeira utilização para `video-runtime` e conferido por SHA-256; a licença vai junto. A variante GPL (libx264) **não** é usada; o codificador H.264 é o OpenH264 (BSD) ou o do Windows (Media Foundation), escolhido conforme o que o executável tiver. |

Avaliado e descartado: a variante GPL do FFmpeg (melhor codificador, libx264, mas obrigações da GPL
sobre o binário distribuído) e o Media Foundation direto (sem FFmpeg), que não abre vários formatos
(FLV, MKV com VP9/AV1 sem extensões da Store, AVI com Xvid etc.). Segurança: só o protocolo `file`
é permitido na leitura, então um vídeo com lista de reprodução embutida não acessa a rede. H.264 e
AAC são formatos com patentes em alguns países; o programa não cobra nem distribui codificadores
comerciais.

### Planejadas

| Finalidade | Escolha prevista | Licença | Observação |
|---|---|---|---|
| Senhas, permissões e metadados | PDFsharp 6 (já em uso) | MIT | Fase 6. |
| Comparação de documentos | PdfPig (já em uso) | Apache-2.0 | Fase 6. |
| PDF → XLSX/PPTX | Open XML SDK | MIT | Fase 6, junto com a extração de tabelas. |
| OCR | Tesseract 5.2.0 (binding .NET de charlesw) + modelos `tessdata_fast` | Apache-2.0 | por/eng/spa incorporados na compilação (`tools/obter-modelos-ocr.ps1`) e extraídos para a pasta de dados; outros idiomas instalados pelo usuário. |
| Otimização estrutural, reparo | qpdf | Apache-2.0 | Alternativa permissiva ao Ghostscript. |
| Assinaturas digitais | BouncyCastle | MIT | Validação criptográfica real (CMS/PAdES). |
| Scanner | WIA (COM do Windows) e NTwain | — / MIT | |
| Instalador | Inno Setup | Licença própria, gratuita inclusive para uso comercial | |

### Com restrição de licença — decisão

| Biblioteca | Problema | Decisão |
|---|---|---|
| **iText** | AGPL: distribuir o programa obrigaria a abrir todo o código sob AGPL, ou comprar licença comercial. | **Não usar.** |
| **Ghostscript** | AGPL (ou comercial). Mesmo chamado como processo externo, embuti-lo no instalador traz obrigações da AGPL. | **Não embutir.** Usar qpdf + recompressão própria de imagens. Se a qualidade de compressão não bastar, oferecer Ghostscript como componente opcional que o usuário instala por conta própria, com aviso claro. |
| **QuestPDF** | Licença "Community": gratuita só abaixo de um limite de faturamento anual. | **Evitar.** A geração de PDF necessária (texto/HTML → PDF, relatórios) é coberta por PDFsharp/MigraDoc (MIT). |
| **veraPDF** (validação PDF/A) | GPL/MPL, em Java. | Usar apenas como ferramenta externa opcional. Sem validador, o programa **não** declara conformidade PDF/A (regra 16). |

## 4. Plano de implementação

Segue as oito fases da especificação. Cada fase termina com build verde, testes aprovados,
`PROJECT_STATUS.md` e `CHANGELOG.md` atualizados e executável publicado.

| Fase | Entrega | Situação |
|---|---|---|
| 1 | Fundação: solução, arquitetura, interface, temas, menu, pesquisa, SQLite, gerenciamento de arquivos | Entregue |
| 2 | Visualizador, miniaturas, mesclar, dividir, extrair, girar, reordenar, excluir, intercalar | Entregue |
| 3 | Conversões e compressão | Entregue |
| 4 | OCR e digitalização (scanner WIA aguardando teste com aparelho real) | Entregue |
| extra | Compressão de vídeo para MP4 360p (FFmpeg) | Entregue (0.4.1) |
| 5 | Editor | Próxima |
| 6 | Segurança, PDF/A, comparação, reparo, assinaturas | |
| 7 | Lote, fluxos, pasta monitorada, menu de contexto, linha de comando | |
| 8 | Testes gerais, otimização, instalador, documentação | |

## 5. Dificuldades técnicas previstas

1. **Editar texto existente em PDF.** PDF não guarda parágrafos, só glifos posicionados. Edição real
   só é viável em casos simples (mesma fonte incorporada completa, linha única). O editor vai
   diferenciar "editar texto existente" de "cobrir e sobrepor", avisando quando o layout pode mudar.
2. **PDF → Word/Excel/PowerPoint.** Sempre uma reconstrução aproximada. Será entregue com aviso de
   limitações e nunca descrita como reprodução fiel. PDF → PPTX fiel não é realista; a alternativa é
   uma página por slide como imagem + texto extraído.
3. **Office → PDF.** Depende de LibreOffice (ou do Office instalado). Sem nenhum dos dois a
   ferramenta fica indisponível e a interface diz o porquê.
4. **Compressão sem Ghostscript.** Reamostrar e recomprimir imagens dentro do PDF exige código
   próprio sobre PDFsharp/PdfPig. Os perfis mais fortes podem render menos que os do PDF24.
5. **PDF/A.** Converter exige incorporar fontes, perfil de cor e metadados XMP; validar exige
   validador externo. Sem validação, o resultado será rotulado "convertido, não validado".
6. **Assinaturas digitais e ICP-Brasil.** Validação criptográfica e de cadeia é viável com
   BouncyCastle; conformidade ICP-Brasil só será alegada após testes específicos com certificados reais.
7. **TWAIN.** Drivers variam muito e muitos são 32 bits; WIA é mais estável e será o caminho padrão.
8. **Documentos grandes.** Renderização sob demanda, cache de miniaturas em disco e limite de
   concorrência configurável são requisitos desde a Fase 2.
9. **Reparo de PDF.** Reconstrução de xref é possível (qpdf); recuperação de objetos danificados é
   parcial por natureza e o relatório dirá o que foi perdido.

## 6. Estratégia de testes

- **Unitários** (`PDFMaster.Tests`): regras do `Core` sem E/S — pesquisa, nomes de saída,
  saneamento de caminhos, configurações, e depois intervalos de páginas, perfis etc.
- **Integração**: SQLite em arquivo temporário, sistema de arquivos em pasta temporária,
  cancelamento, recuperação de erros; a partir da Fase 2, cada operação de PDF contra arquivos reais.
- **Arquivos de teste**: gerados pelos próprios testes quando possível (PDFs mínimos, imagens) e um
  conjunto versionado em `PDFMaster.Tests/Samples` para casos que não dá para gerar: escaneados,
  protegidos, danificados, formulários, assinados, com muitas páginas.
- **Teste de inicialização no CI**: o executável publicado é aberto em tema claro e escuro, em
  várias seções; o build falha se o processo encerrar sozinho, e as capturas de tela ficam guardadas
  no ramo `ci-results`.
- **Critério de conclusão** (seção 21.4): recebe arquivos reais, executa, gera resultado, permite
  abrir o resultado, trata erros e passa nos testes. Só então `IsAvailable = true`.
- **Relatório por módulo**: seção "Testes realizados" do `PROJECT_STATUS.md`.
