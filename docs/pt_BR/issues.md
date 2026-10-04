# Perguntas, bugs e sugestões

[Abra uma issue](https://github.com/BedrockNative/OrionLauncher/issues/new/choose)
e escolha **Bug**, **Question / Pergunta** ou **Suggestion / Sugestão**.
Português e inglês são aceitos. Procure primeiro por relatos existentes.

## Relatando um bug

Informe versão do Orion, sistema/distribuição e versão, kernel e versão,
desktop/window manager, sessão X11/Wayland, passos para reproduzir, resultado
esperado e resultado observado. Em problemas gráficos, informe GPU e driver.
Anexe foto/vídeo e logs relevantes da instância/launcher quando possível.
Remova tokens, credenciais e dados pessoais antes de enviar.

RTX e gerenciamento de conteúdo são experimentais. Informe provider, preset/pacote,
versão do Minecraft e se o problema acontece sem esse conteúdo. Faça backup dos
mundos importantes antes de experimentar; não apague seus dados para relatar um bug.

## Categorias e automação

Os formulários aplicam `bug`, `question` ou `enhancement`. Issues em branco ficam
desativadas no seletor web. A API ainda permite criar issues sem categoria:
**Issue classification** aplica `needs-classification` e um lembrete bilíngue.
Se não puder aplicar labels, responda com a categoria para um mantenedor ajustar.
Após classificar, a marcação pendente é removida. Outros labels/comentários são
preservados e issues nunca são fechadas automaticamente. É uma ajuda de triagem,
não uma garantia de que todos os relatos estejam completos.

Mantenedores: preservem os três labels. Os formulários ficam em
`.github/ISSUE_TEMPLATE/` na branch padrão. A automação não compila o launcher
nem acessa credenciais de release. Teste com:

```sh
python3 -m unittest discover -s .github/tests -v
```

Para contribuir com código, consulte [o fluxo de contribuição](../en_US/contributing.md).
Categorias de issues e direcionamento de PRs são políticas separadas.
