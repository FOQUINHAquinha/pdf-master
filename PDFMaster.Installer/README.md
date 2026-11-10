# PDFMaster.Installer

O instalador é entregue na Fase 8. Plano: script Inno Setup gerado no GitHub Actions a partir do
executável publicado, com escolha de diretório, atalhos, componentes opcionais (menu de contexto,
associação de arquivos, modelos de OCR) e desinstalação completa que não toca nos documentos do usuário.

Até lá, a distribuição é o executável único e a versão portátil publicados pelo workflow `build`.
