# Validação local da 1.0.2 — 2026-10-05

AppImage e tar.gz passaram nas 12 etapas em quatro bases novas. As oito
capturas foram inspecionadas: ambos os formatos renderizam a janela do Orion.
Não houve avisos/erros Fontconfig nos logs de Wine e das duas GUIs.

## Artefatos

Código do PR #5 até `45ef167bfa6e53d9eef5a644d0547282951126d7`, com
`RELEASE_VERSION=1.0.2`, notas finais EN/PT e teste do catálogo de changelogs
ajustado para a nova versão. Os commits seguintes registram esses mesmos ajustes
e este relatório; não houve mudança funcional depois da geração dos pacotes.
Publish: `artifacts/publish-1.0.2-final`. Pacotes: `artifacts/release-1.0.2-final`.
Empacotamento em Ubuntu 24.04, com runtimes fixados em `stack.lock.json`.

| Arquivo | SHA-256 |
| --- | --- |
| AppImage | `e2d0a35bb8c46d26039676830d009bf47959a56fc5d1b0b12dc5cbf605aa8796` |
| tar.gz | `7c50d097d5e5e88883853421cf2d50922ad96705a5d5b7b495688da078370795` |
| build-manifest.json | `ea6ae9aa7fa75f16a7b11781a7865a4dcef4c5171c8c14370143b27b6e768e82` |

Build local sem credencial CurseForge. O Actions recompila com a credencial
protegida e produzirá outros hashes; estes resultados não certificam esses
futuros arquivos.

## Bases e resultados

Todas reconstruídas com `--pull --no-cache`, sem `--reuse-images`.

| Distro | Versão | Etapas | Tempo total |
| --- | --- | --- | --- |
| Arch | rolling `20260927.0.600689` | 12/12 | 183,95 s |
| Debian | 13 trixie (`base-files=13.8+deb13u7`) | 12/12 | 197,27 s |
| Fedora | 44 | 12/12 | 236,49 s |
| Ubuntu | 24.04.5 LTS | 12/12 | 241,22 s |

IDs das imagens de teste:

- Arch: `sha256:65336b9e2cc06b906b801059c9c36c9fdb753171a178a2480bea8060db1d0f78`.
- Debian: `sha256:9dc9db214941745a642d56325c3a4abc0aae569f3c040117ead2d58fcd4f1ea5`.
- Fedora: `sha256:2d8eb64488bd53c9f15a1f3ec28934da312fe0c4e967c2b85055b1383feb4e0f`.
- Ubuntu: `sha256:a2fd7e79d6975a218a047b1d5bd1bfa36467f13177b54413f04b361ae47da9cb`.

Digests das bases oficiais:

- Arch: `sha256:b21322c663be387c0ed9cbc7bbbfe18e41633ad4e7b7c77cfad45f128be20040`.
- Debian: `sha256:a99cfc517144bc59b1978475ec53b46ecabec7e43635402ee5b77cc54cd1b20a`.
- Fedora: `sha256:7011f51bd8089d345be42d41f0aa3190d258823528852a5e7ec976fe2fd20f53`.
- Ubuntu: `sha256:534baea6a22c03a63003dbc8dbe78fe34bc0d7e595d9a9dc9834884ff530eb55`.

## Reprodução e evidências

```bash
python3 packaging/linux/compat/matrix.py artifacts/release-1.0.2-final \
  --distros arch debian fedora ubuntu \
  --output artifacts/compatibility-1.0.2-final
```

O diretório de saída guarda `summary.json`, hashes, `stages.json`, inventários,
metadados, logs e capturas XWD. Pacotes e evidências brutas são locais e ignorados
pelo Git. O teste cobre checksums, extração, EGL por software, dependências
nativas, CLI Orion/Xodus, prefixo Wine, WebKit offline e CLI/GUI dos pacotes.

Também passaram 408 testes .NET Release, 18 testes Python de empacotamento,
16 testes de automações GitHub, actionlint, validação da versão/notas e
`git diff --check`. A [revisão anterior](RESULTS-REVIEW-2026-10-04.md) documenta
os benchmarks sintéticos e o smoke test padrão daquela rodada.

## Limites

Execução offline, sem privilégios, GPU, contas, instâncias ou desktop reais,
usando dados descartáveis. Sem instalação de .NET, Wine, GTK ou WebKit nas bases
de execução; sandbox WebKit ativo. As distros compartilham o kernel do host.
Não certifica login Microsoft, keyring, áudio físico, Minecraft/RTX/DLSS,
FUSE, Wayland nativo, desempenho ou políticas SELinux/AppArmor em desktops reais.
