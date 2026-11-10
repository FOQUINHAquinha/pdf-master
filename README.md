# PDF Master

Suíte de ferramentas PDF para Windows 10 e 11, gratuita, sem conta, sem anúncios e com
processamento 100% local. Em desenvolvimento por fases — veja [PROJECT_STATUS.md](PROJECT_STATUS.md).

**Estado atual: Fases 1 e 2 concluídas.** Já funcionam o visualizador e as ferramentas de
organização: mesclar, dividir, extrair, reorganizar, girar, excluir, inserir e intercalar páginas.
Conversão, compressão, OCR, editor e segurança aparecem no programa como "Prevista" e chegam nas
próximas fases.

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
| `--page <seção>` | Abre direto numa seção: `home`, `organize`, `edit`, `convert`, `compress`, `security`, `ocr`, `compare`, `batch`, `history`, `settings`. |
| `--tool <ferramenta>` | Abre uma ferramenta com os arquivos informados: `view`, `merge`, `interleave`, `split`, `reorder`, `rotate`, `delete-pages`, `insert-pages`, `extract-pages`. |
| `--output <pasta>` | Define a pasta de destino da ferramenta aberta com `--tool`. |
| `--run` | Executa a ação principal da ferramenta com as opções padrão (a janela continua aberta para mostrar o resultado). |
| arquivos e pastas | Sem `--tool`, vão para a lista de arquivos abertos (pastas são lidas com as subpastas). |

Exemplos:

```
PDFMaster.exe --tool merge --output C:\Saida --run a.pdf b.pdf
PDFMaster.exe --tool split documento.pdf
PDFMaster.exe --tool view documento.pdf
```

A execução sem janela (`--merge`, `--compress`, `--ocr`, `--split` em modo terminal) entra na Fase 7.

## Onde ficam os dados

- Padrão: `%LOCALAPPDATA%\PDFMaster` (banco `pdfmaster.db` e pasta `Logs`).
- Modo portátil: pasta `Data` ao lado do executável.
- Variável de ambiente `PDFMASTER_DATA_DIR`: usa a pasta indicada.

O banco guarda configurações, histórico e arquivos recentes — nunca o conteúdo dos documentos.
O programa não usa a rede.

## Documentação

- [docs/ARQUITETURA.md](docs/ARQUITETURA.md) — arquitetura, bibliotecas e licenças, plano, riscos, testes.
- [PROJECT_STATUS.md](PROJECT_STATUS.md) — o que está pronto, o que falta e como foi testado.
- [CHANGELOG.md](CHANGELOG.md) — histórico de alterações.
