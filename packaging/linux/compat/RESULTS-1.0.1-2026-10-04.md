# Validação local da 1.0.1 — 2026-10-04

AppImage e tar.gz passaram nas 12 etapas em Arch rolling, Debian 13.7 e
Fedora 44. As seis capturas foram inspecionadas: ambos os formatos renderizam
a janela do launcher. Não é uma validação de desempenho do Minecraft ou RTX.

## Artefatos

Código do launcher até `5d80bf5`, com `RELEASE_VERSION=1.0.1` e as notas finais
da 1.0.1. A revisão do harness está em `9560bdf`; os commits finais registram
os testes, a documentação e a flag já usados para gerar esses binários.
Publish: `artifacts/publish-1.0.1-final`. Pacotes: `artifacts/release-1.0.1-final`.
Base de empacotamento Ubuntu 24.04; stack fixada em `stack.lock.json`, incluindo
WineGDK `11.18-8-winrt` e Xodus `0.7.3`.

| Arquivo | SHA-256 |
| --- | --- |
| AppImage | `0e786a85337204a0704fbeea8843a50047f7cc0e5b171de77f28a53908185abd` |
| tar.gz | `3a6d78245574424e90e8defd548d5a1046877cb2dd25f55dc670c332d4ef57b2` |
| build-manifest.json | `6deabb04ce29cbb17ec7f7a79ed2edf849b3f56e97074f78f3c1453e8df0335c` |

Build local sem chave CurseForge. O Actions recompila com a credencial protegida
e produzirá outros hashes; esta evidência não é uma medição desses futuros arquivos.

## Bases e resultados

Todas reconstruídas com `--pull --no-cache`, sem `--reuse-images`.

| Distro | Versão | Etapas | Tempo total |
| --- | --- | --- | --- |
| Arch | rolling `20260927.0.600689` | 12/12 | 177,83 s |
| Debian | 13.7 (trixie) | 12/12 | 179,36 s |
| Fedora | 44 | 12/12 | 196,03 s |

IDs das imagens de teste:

- Arch: `sha256:ae6b8eed8b9a47a98a779a1d0fc964b4dfa73b4b7424499bc4f837bd59904197`.
- Debian: `sha256:9307696c49fb8dfa4f4dda415a5695e1b4111d140811f9dd93ce0b130cdcf96a`.
- Fedora: `sha256:c07bbc080111f066d130d1c4356d7e518080016d264d18e5f88b3087d2ebd23d`.

Digests das bases oficiais:

- Arch: `sha256:b21322c663be387c0ed9cbc7bbbfe18e41633ad4e7b7c77cfad45f128be20040`.
- Debian: `sha256:a99cfc517144bc59b1978475ec53b46ecabec7e43635402ee5b77cc54cd1b20a`.
- Fedora: `sha256:7011f51bd8089d345be42d41f0aa3190d258823528852a5e7ec976fe2fd20f53`.

## Correção da validação e evidências

A primeira rodada (`artifacts/compatibility-1.0.1-final`) marcou Arch como
aprovado, mas a inspeção da captura do AppImage encontrou uma tela preta.
Essa rodada não foi aceita como validação final; o Debian foi interrompido.
O harness agora exige `Map State: IsViewable`, captura a própria janela e
rejeita pixels uniformes, aguardando a renderização dentro do prazo. Há teste
de regressão para capturas vazias. Os pacotes não foram alterados na repetição.

```bash
python3 packaging/linux/compat/matrix.py artifacts/release-1.0.1-final \
  --output artifacts/compatibility-1.0.1-rendered
```

Esse diretório preserva `summary.json`, `stages.json`, inventários, metadados,
logs e capturas XWD. A rodada anterior permanece como diagnóstico. Evidências
brutas e pacotes são locais e ignorados pelo Git.

Validação adicional: 385 testes .NET Release, 13 testes Python de empacotamento,
16 testes das automações GitHub, `actionlint` e `git diff --check` passaram.

## Limites

Contêineres offline, sem privilégios, GPU, contas, instâncias ou desktop reais.
Não foram instalados .NET, Wine, GTK ou WebKit nas bases de execução. O sandbox
WebKit permaneceu ativo. Não certifica login real, keyring, áudio físico,
Minecraft/RTX/DLSS, FUSE, Wayland nativo, desempenho ou SELinux/AppArmor em
desktops reais. As distros compartilham o kernel do host.
