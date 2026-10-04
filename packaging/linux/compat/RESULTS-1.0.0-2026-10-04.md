# Validação local da 1.0.0 — 2026-10-04

Os mesmos AppImage e tar.gz passaram nas 12 etapas de portabilidade em Arch,
Debian 13 e Fedora 44. As seis capturas de interface foram inspecionadas.
Isso valida inicialização e dependências nos ambientes abaixo, **não** desempenho
do Minecraft, RTX, login real ou toda instalação dessas distribuições.

## Artefatos e código

- Código/pacote: `331ceff17da814ab339c6be69586d16ab7befcb8`.
- Ajuste posterior apenas da base Fedora do harness: `b37df65`.
- Publish: `artifacts/publish-1.0.0-final`; saída: `artifacts/release-1.0.0-final`.
- Base de empacotamento: Ubuntu 24.04, imagem local `8894339ca392`.
- WineGDK `11.18-8-winrt`, Xodus `0.7.3`; demais entradas em `stack.lock.json`.
- Build local **sem chave CurseForge**. O Actions recompila com o recurso de
  credencial protegido e terá hashes próprios. Estes hashes não são atribuídos
  automaticamente a uma publicação futura do GitHub.

| Arquivo | Bytes | SHA-256 |
| --- | ---: | --- |
| `OrionLauncher-1.0.0-linux-x64.AppImage` | 1039706616 | `32c999c33fe0a5a0d0e991b434a4f48245a1c7f95c7d39f64b2edf5af771183d` |
| `OrionLauncher-1.0.0-linux-x64.tar.gz` | 1136492831 | `fbc81f3e98f05c75a352db587a8016aa436a3589f866250f9fb5d35be0a21ed4` |
| `build-manifest.json` | — | `9e9e256ae6a5df3dfd29d21e985ed64665ed6a8e6dc44901c69dab8641eff47f` |

## Ambientes e resultados

Todas as bases foram reconstruídas com `--pull --no-cache`, sem `--reuse-images`.

| Ambiente | glibc / Mesa / Wayland | Resultado |
| --- | --- | --- |
| Arch rolling, base `20260927.0.600689` | 2.44+r50 / 26.2.4 / 1.26.0 | 12/12 etapas; tar e AppImage abrem a janela |
| Debian 13 (trixie) | 2.41 / 25.0.7 / 1.23.1 | 12/12 etapas; tar e AppImage abrem a janela |
| Fedora 44 | 2.43 / 26.2.3 / 1.26.0 | 12/12 etapas após completar as interfaces Wayland do host |

Imagens preparadas (IDs Docker):

- Arch: `sha256:d6cd9c6beef8576e398b51b9bdffa843dce5968f19a28ac1e296868ec9bccd7c`.
- Debian: `sha256:eb8de5b549ff3d2e6c1009f6160507c5cf101b07b78812174e3e5e3af6274ec2`.
- Fedora: `sha256:5b44036d7d3077cef56fdac755dcf4f2f07c83be7dc5de6b4406a1682a24fdbc`.

Digests das bases oficiais:

- `archlinux:base`: `sha256:b21322c663be387c0ed9cbc7bbbfe18e41633ad4e7b7c77cfad45f128be20040`.
- `debian:13-slim`: `sha256:a99cfc517144bc59b1978475ec53b46ecabec7e43635402ee5b77cc54cd1b20a`.
- `registry.fedoraproject.org/fedora:44`: `sha256:7011f51bd8089d345be42d41f0aa3190d258823528852a5e7ec976fe2fd20f53`.

## Problemas encontrados e corrigidos

1. O conflito Mesa/Wayland do [relatório anterior](RESULTS-2026-10-04.md)
   foi resolvido mantendo as interfaces Wayland junto dos drivers do **host**.
   EGL passou tanto com bibliotecas do sistema quanto com a stack privada.
2. O ELF Reflex distribuído no WineGDK exigia `GLIBCXX_3.4.35`, indisponível na
   base Ubuntu. O pacote recompila somente essa camada Linux do DXVK-NVAPI 0.9.2
   usando fontes/headers fixados e GCC 13.3. As DLLs Windows permanecem intactas;
   receita, fontes e proveniência da recompilação acompanham o pacote.
3. A primeira rodada destes hashes passou no Arch e Debian, mas falhou no Fedora:
   a base mínima tinha apenas `libwayland-client`, sem server/cursor/EGL. A receita
   Fedora passou a instalar explicitamente essas interfaces gráficas já exigidas
   do host, assim como a receita Debian. **Nenhum binário foi alterado** para a
   repetição Fedora, que passou. A rodada com falha não foi apagada nem reclassificada.

## Evidências e comandos

```bash
python3 packaging/linux/compat/matrix.py artifacts/release-1.0.0-final \
  --output artifacts/compatibility-1.0.0-final
# Após corrigir somente a preparação da base Fedora:
python3 packaging/linux/compat/matrix.py artifacts/release-1.0.0-final \
  --distros fedora --output artifacts/compatibility-1.0.0-fedora-host-interfaces
```

Resultados Arch/Debian e a primeira falha Fedora estão no primeiro diretório;
o resultado final Fedora está no segundo. Ambos preservam `summary.json`, hashes,
`stages.json`, inventários, metadados de imagem, logs e capturas XWD. Artefatos e
evidências brutas permanecem locais e ignorados pelo Git.

Foram verificados checksums, extração em caminho com espaços, EGL, 529 ELF,
CLI do launcher/Xodus, prefixo Wine offline com Mono/Gecko, WebKit renderizando
HTML local e janelas reais dos dois formatos. O smoke test padrão do Actions
também passou no contêiner de empacotamento.

Validação adicional: 377 testes .NET Release, 12 testes Python de empacotamento,
8 testes da classificação de issues e `actionlint` passaram.

## Limites e segurança

Testes offline como usuário sem privilégios, sem GPU física, home, contas,
instâncias reais ou desktop do host. Não foram instalados .NET, Wine, GTK ou
WebKit nas bases de execução; as interfaces gráficas do sistema são pré-requisitos,
não componentes que o launcher substitui. O sandbox WebKit não foi desativado.

Não foram validados aqui login Microsoft/CurseForge online, áudio físico,
Minecraft/RTX/DLSS, FUSE, Wayland nativo, desempenho ou políticas SELinux/AppArmor
de desktops reais. Contêineres compartilham o kernel do host. Builds diferentes,
incluindo os futuros artefatos do Actions, não herdam estes checksums nem uma
garantia irrestrita de compatibilidade.
