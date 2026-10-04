# Publicação do Orion Launcher

## Fluxo normal

1. Trabalhe e faça commits na `development`. Pushes e pull requests dessa branch
   **não disparam workflows de build** (a classificação de issues é independente).
   Não envie commits diretamente à `main`; se estiver nela, troque para
   `development` antes de editar. Execute os testes localmente antes de enviar o PR.
   Ao iniciar a próxima versão, crie seu novo changelog (por exemplo,
   `docs/en_US/changelog/release/v1.0.1.md`) e atualize-o ao longo do desenvolvimento,
   junto do equivalente em português. Não acrescente novidades às notas já publicadas.
2. Quando estiver pronto para publicar, altere `RELEASE_VERSION`, na raiz, para a
   versão desejada, por exemplo `1.0.0` (sem `v`). Essa alteração é a flag de release.
3. Escreva `docs/en_US/changelog/release/v1.0.0.md`. O conteúdo desse arquivo será
   usado integralmente como descrição da release. Atualize também o changelog em
   português e `CHANGELOG.md` para a documentação do launcher.
4. Revise e faça merge de `development` na `main`, preservando os commits com
   merge commit. A flag é o arquivo `RELEASE_VERSION`, não uma opção do Git ou
   mensagem especial de merge. O workflow **Release Linux** só é disparado
   automaticamente se `RELEASE_VERSION` mudou. Um merge comum, sem mudar esse
   arquivo, roda apenas **Build and test** e não publica nada.
5. Aprove o ambiente protegido `curseforge-release` no GitHub. Revise o commit
   exato antes de aprovar o acesso ao secret.
6. Após testes, empacotamento e smoke tests, o workflow cria a tag `v1.0.0` no
   **commit que foi testado**, e a release **Orion Launcher 1.0.0**.

Também é possível executar **Actions → Release Linux → Run workflow**, selecionar
`main` e informar `1.0.0`. O valor precisa coincidir com `RELEASE_VERSION` nesse
commit. Isso permite repetir um build que falhou antes da publicação. Tags/releases
existentes não são sobrescritas. Não crie a tag manualmente antes do workflow.

Pré-releases como `1.1.0-rc.1` são aceitas e marcadas como pré-release no GitHub;
exigem o arquivo `v1.1.0-rc.1.md`. Não use zeros iniciais, `v`, espaços ou `+metadata`.
A versão dos assemblies .NET também vem de `RELEASE_VERSION`.

## Arquivos publicados

- `OrionLauncher-1.0.0-linux-x64.AppImage`
- `OrionLauncher-1.0.0-linux-x64.tar.gz`
- `SHA256SUMS` e `build-manifest.json` (commit, runtimes e bibliotecas do build).

Os dois formatos usam o mesmo conteúdo. Para o tar, extraia a pasta inteira e
execute `./OrionLauncher` ou `./AppRun`; não mova apenas o executável interno.
Para AppImage, dê permissão com `chmod +x` e execute. Sem FUSE, use
`APPIMAGE_EXTRACT_AND_RUN=1 ./OrionLauncher-1.0.0-linux-x64.AppImage`.
Mantenha o arquivo/pasta em um local estável para os atalhos das instâncias.

## Stack incluída e limites do sistema

Build oficial: **Linux x86_64, Ubuntu 24.04, glibc 2.39 ou superior**. Não é um
pacote Alpine/musl, ARM ou Windows. Não precisa instalar .NET, SDK, CMake ou MinGW
no computador que executará o launcher.

O pacote inclui .NET self-contained, Avalonia/Skia/HarfBuzz/Inter,
`Orion.Native.dll`, Xodus, WineGDK (com os componentes distribuídos pelo fork),
Wine Mono e Gecko nas versões esperadas pelo Wine (sem prompts de download no
primeiro prefixo), GTK/WebKit e seus processos auxiliares de login, GStreamer, bibliotecas de áudio,
TLS/ICU/fontes e dependências ELF transitivas. Inclui também licenças/atribuições.
Somente o provider opcional de profiling LTTng 2.12 é excluído; ele não é usado
pelo launcher. EventPipe, dumps de crash e logs do launcher/jogo são preservados.
O FFmpeg exigido pelo WineGDK é compilado a partir da versão e checksum fixados,
contra a mesma base Ubuntu; não são feitos aliases entre ABIs de FFmpeg diferentes.
O código-fonte correspondente e a receita ficam em `usr/share/licenses/ffmpeg`.
A camada Linux Reflex do DXVK-NVAPI também é recompilada, sem alterar suas DLLs
Windows, a partir da mesma versão incluída no WineGDK. Isso evita exigir símbolos
GLIBCXX de um compilador mais novo que a base suportada. Fontes, headers e receita
são fixados por checksum e incluídos em `usr/share/licenses/dxvk-nvapi-portable`;
o manifesto registra a origem e a recompilação.
Os runtimes incluídos são usados sem redownload inicial; **Atualizar runtimes**
instala atualizações no diretório de dados do usuário, sem modificar o AppImage.
Atualizações futuras podem exigir uma stack nativa mais nova e uma nova release.

Não se deve empacotar/substituir o kernel, glibc do host, drivers de GPU ou serviços
da sessão. São necessários: desktop X11/XWayland, D-Bus com `XDG_RUNTIME_DIR`,
Secret Service/keyring desbloqueado para contas, portais/gerenciador de arquivos,
e driver Vulkan/OpenGL/EGL/OpenGL ES compatível com a GPU (incluindo os loaders
do sistema, como `libGLESv2.so.2`, e suas bibliotecas Wayland client/server/cursor/EGL).
Essas bibliotecas seguem a versão dos drivers do host para não ocultar símbolos
exigidos pelo Mesa de distribuições mais recentes. FUSE é opcional pelo modo de extração.
Minecraft, mundos, contas, shaders e conteúdo de terceiros **não** são incluídos;
o download do jogo ainda requer internet, conta e licença válidas.

`packaging/linux/install-dependencies.sh` é para o runner/contêiner Ubuntu, **não
para executar com sudo na máquina do usuário**. O empacotador coleta bibliotecas
privadas e usa RUNPATH relativo; não altera o `LD_LIBRARY_PATH` de todo o desktop.
Smoke tests verificam ELF, inicialização CLI do launcher/Xodus/Wine, criação de um
prefixo Wine descartável, extração do
AppImage, renderização HTML offline pelos processos WebKit e inicialização gráfica
em Xvfb, inclusive em um Ubuntu sem .NET/GTK/WebKit
instalados. Isso não equivale a testar login real, drivers ou uma sessão de Minecraft
em todas as distribuições. Essas verificações precisam de teste manual de release.

O WebKit de produção contém caminhos absolutos para seus auxiliares. `AppRun`
gera uma cópia privada da biblioteca com esses três caminhos relocados em um
diretório temporário exclusivo (permissão 0700), removido ao encerrar o launcher.
Nenhuma biblioteca do sistema é alterada e o sandbox WebKit não é desativado.
Mudanças nesses caminhos upstream bloqueiam o build até revisão do empacotamento.
O sistema precisa permitir executar os auxiliares e os namespaces utilizados pelo
sandbox; políticas locais restritivas precisam de configuração pelo administrador.

## Validação local antes de uma release

Antes de preparar o commit/publicação, teste os **artefatos finais** em Arch,
Debian e Fedora seguindo [a matriz local](packaging/linux/compat/README.md):

```bash
python3 packaging/linux/compat/matrix.py artifacts/release
```

As instruções para agentes estão em [AGENTS.md](AGENTS.md). Esta bateria pesada
é local, opt-in e não roda no GitHub Actions. O workflow mantém os testes de
código e o smoke test do pacote no próprio runner, sem criar a matriz de distros.
Guarde o relatório com os hashes testados. Uma falha ou falta de acesso ao Docker
deve ser informada; não significa que a release foi validada.

## CurseForge e segurança

O workflow usa o secret existente **`CURSEFORGE_API_KEY`**, no ambiente
**`curseforge-release`**. Não copie a chave para YAML, documentação, argumentos,
commits, caches ou logs. A chave só entra no processo gerador de recurso ofuscado;
restore e testes não a recebem. O arquivo temporário em `obj/` é removido mesmo
se o publish falhar. Só os pacotes finais são enviados como artifacts.

O ambiente deve continuar restrito à `main` e sujeito a aprovação. A permissão
`contents: write` existe apenas no job final de publicação. Não há
`pull_request_target`, acesso a secrets por PR ou push de commits automático.
Ofuscação **não é sigilo**: uma chave embutida pode ser recuperada de um cliente
distribuído. Consulte [a política de credenciais](docs/en_US/curseforge-build.md).

## Manutenção e execução local

`packaging/linux/stack.lock.json` fixa URLs, versões e SHA-256 dos runtimes,
appimagetool e runtime AppImage. Atualize os hashes somente após verificar os
artefatos oficiais. Se um asset `continuous` for substituído, o build falha no
checksum; nunca aceite automaticamente o novo hash. Mantenha revisão obrigatória
nos workflows, scripts de empacotamento e nesse arquivo.

Em Ubuntu 24.04 preparado para build, sem chave para validação local:

```bash
python3 -m unittest discover -s packaging/tests -v
dotnet test OrionLauncher.slnx -c Release -m:1
dotnet publish src/Orion.Desktop -c Release -r linux-x64 --self-contained true \
  -p:DebugType=None -p:DebugSymbols=false -o artifacts/publish
python3 packaging/linux/package.py artifacts/publish artifacts/release
bash packaging/linux/smoke-test.sh artifacts/release
```

A pasta de saída deve estar vazia. O pacote local sem chave pode receber uma chave
própria pelo mecanismo documentado de variável/arquivo privado. Não é necessário
nem possível baixar o valor do GitHub Secret pela API.

Referências: [eventos do Actions](https://docs.github.com/en/actions/how-tos/write-workflows/choose-when-workflows-run/trigger-a-workflow),
[dependências Avalonia/Linux](https://docs.avaloniaui.net/docs/deployment/linux),
[boas práticas AppImage](https://docs.appimage.org/reference/best-practices.html),
[appimagetool](https://github.com/AppImage/appimagetool).
