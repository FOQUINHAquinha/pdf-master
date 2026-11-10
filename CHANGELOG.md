# Histórico de alterações

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
