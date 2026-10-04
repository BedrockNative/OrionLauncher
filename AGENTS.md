# Orion — instruções para agentes

## Preservação e escopo

- Preserve mudanças existentes. Não publique commits, tags ou releases sem pedido.
- Nunca exponha nem versione credenciais do CurseForge, contas ou dados de jogo.
- Testes de runtime devem usar dados descartáveis, nunca instâncias reais do usuário.

## Branches, changelogs e releases

- A branch de desenvolvimento é **`development`**. Faça alterações, commits
  Conventional Commits e pushes nela; **não faça push direto na `main`**.
  Se estiver na `main`, mude para `development` antes de começar. Preserve
  alterações locais ao trocar de branch; não use reset para forçar a troca.
- A `main` é a linha de publicação: recebe o trabalho revisado por merge de
  `development`, somente quando o usuário autorizar uma release ou um merge de
  configuração/documentação. Nesse segundo caso, não altere `RELEASE_VERSION`.
  Preserve os commits separados usando merge commit, não squash.
- Contribuições de outras branches/forks devem abrir PR para `development`.
  A proteção de `main` exige PR e o check **Main source policy**, sem bypass:
  somente `development` do próprio repositório é aceita. PRs de outra origem são
  fechados com orientação; o GitHub não impede sua abertura. Não desative regras
  para contornar uma falha. Consulte `docs/en_US/contributing.md` e o modelo em
  `.github/rulesets/main.json`; editar o JSON não atualiza a regra remota sozinho.
- Durante todo o desenvolvimento da próxima versão, acumule suas mudanças no
  **novo** arquivo `docs/en_US/changelog/release/v<VERSÃO>.md`, ainda na
  `development`. Por exemplo, depois da 1.0.0, use `v1.0.1.md` se essa for a
  próxima versão acordada. Mantenha o equivalente em `docs/pt_BR/changelog/release/`
  e `CHANGELOG.md` coerentes. Não reescreva notas de versões já publicadas para
  incluir novidades futuras e não afirme que uma versão foi publicada sem conferir.
- Criar/editar esse Markdown **não dispara uma release**. Somente quando a nova
  versão estiver pronta e autorizada, atualize **`RELEASE_VERSION`**, na raiz,
  para a versão sem `v` (ex.: `1.0.1`) e inclua essa alteração no merge para `main`.
  Essa é a **flag de build/release** implementada; não há uma flag especial do Git
  nem um marcador de mensagem de merge. Confira a versão e o Markdown antes do merge.
- Pushes/PRs de `development` não executam os workflows de build. Na `main`,
  um push que muda `RELEASE_VERSION` dispara **Release Linux**; sem essa mudança,
  roda apenas **Build and test**, sem publicação. A automação de classificação
  de issues é independente desse fluxo de branches. PRs destinados à `main`
  executam também a checagem leve de origem, usando somente código da base confiável.
- O Actions usa integralmente `docs/en_US/changelog/release/v<VERSÃO>.md` como
  descrição, gera AppImage e tar.gz e cria a tag `v<VERSÃO>` no commit testado.
  **Não crie tags antecipadamente nem sobrescreva releases existentes.**
- Siga a validação local abaixo antes do merge. Mantenha a aprovação humana do
  ambiente protegido `curseforge-release`; não a contorne. Verifique o resultado
  do Actions e informe se está aguardando aprovação, falhou ou foi publicado.
  Para repetir um build que falhou, siga o `workflow_dispatch` documentado em
  `RELEASING.md`, na `main` e com a mesma versão de `RELEASE_VERSION`.

## Antes de preparar um commit de release

Aplica-se à preparação/publicação de mudanças em `RELEASE_VERSION`, notas finais de release, empacotamento,
dependências/runtimes ou inicialização de pacotes portáteis:

1. Leia `RELEASING.md` e `packaging/linux/compat/README.md`.
2. Execute os testes .NET e Python de empacotamento e valide os workflows.
3. Gere **os dois artefatos finais do mesmo código que será entregue**.
4. Execute localmente:

   ```bash
   python3 packaging/linux/compat/matrix.py artifacts/release
   ```

   A matriz padrão é Arch rolling, Debian 13 e Fedora 44. Use bases novas
   (não `--reuse-images`) na validação final. Ubuntu 24.04 pode ser acrescentado
   com `--distros arch debian fedora ubuntu`.
5. Confira `summary.json`, `stages.json`, logs e capturas de **ambos** os formatos.
   Não considere apenas um processo vivo como sucesso: a janela deve aparecer.
6. Informe distros, versões/digests, hashes dos pacotes e limitações no resultado
   da revisão. Se algum teste falhar, não declare a release validada; diagnostique
   e informe a falha. Se não puder executar a matriz, declare a validação pendente.

Esses testes pesados são **exclusivamente locais e opt-in**. Não os adicione ao
GitHub Actions nem os execute a cada commit comum de desenvolvimento. Não altere
pacotes globais do host para fazer o teste passar, não use dados/desktop reais,
não desative o sandbox WebKit para esconder uma falha e não use contêineres
privilegiados. Peça autorização quando o ambiente exigir acesso ao Docker/rede.

Atualizar documentação, regras de contribuição ou notas incrementais de uma versão
ainda em desenvolvimento, sem preparar uma release nem alterar runtime/pacotes,
não exige repetir a matriz pesada. Valide links, testes da automação e workflows.

Contêineres compartilham o kernel do host. Passar na matriz não certifica GPU
física, desempenho de Minecraft/RTX/DLSS, login Microsoft, Wayland nativo, FUSE
ou políticas SELinux/AppArmor de cada desktop. Essas verificações precisam de
VMs/instalações reais e, quando pertinente, hardware real.
