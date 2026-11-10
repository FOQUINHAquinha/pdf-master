# Histórico de alterações

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
