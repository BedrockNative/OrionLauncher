# Validação para revisão de startup e empacotamento — 2026-10-04

AppImage e tar.gz passaram nas 12 etapas em Arch, Debian, Fedora e Ubuntu.
As oito capturas foram inspecionadas: ambos os formatos do pacote exibem
uma janela renderizada em cada distro. Os logs finais de Wine e das duas GUIs
não contêm avisos nem erros Fontconfig.

## Código e artefatos

Código de runtime e empacotamento de `0c24f30`, na `development`, incluindo
`368338b` (inicialização/configurações) e `65a9132` (bibliotecas e Fontconfig).
O snapshot foi comparado com 278 arquivos de código, testes, empacotamento e
configuração desse commit. Alterações posteriores ficam no harness local de
validação e no relatório/documentação; não alteram o conteúdo executável testado.
Hashes do snapshot: `artifacts/review-build-source-sha256.json`.

`RELEASE_VERSION` continua em **1.0.1**, portanto esse número aparece nos nomes
locais. Estes são builds de desenvolvimento, sem chave CurseForge; não publicam
nem substituem a release 1.0.1. O PR permanece aberto para revisão, sem merge.

Pacotes: `artifacts/build-output/release-review`.

| Arquivo | SHA-256 |
| --- | --- |
| OrionLauncher-1.0.1-linux-x64.AppImage | `9afa6eddb80452933c959ae693939febbd6d2b16513bcc3ba34fd4498256fbd4` |
| OrionLauncher-1.0.1-linux-x64.tar.gz | `a330a746781019919f058ebb9605cc3965013a943149ba90d7d5dfc30e3160b6` |
| build-manifest.json | `857dced51cd1dbff727c8412f2aaeaa2c7452cfddfcb0ad0de45134650b37d01` |

Base de build: `mcr.microsoft.com/dotnet/sdk:10.0-noble`, digest
`sha256:e70cdb7f80b0348f5cb85f19a8f670fca061f033d57eed12fa003d58b0e06317`.
Xodus 0.7.3 e WineGDK 11.18-8-winrt, com fontes/checksums fixados em `stack.lock.json`.

## Matriz final

```bash
python3 packaging/linux/compat/matrix.py artifacts/build-output/release-review \
  --distros arch debian fedora ubuntu \
  --output artifacts/compatibility-review-final
```

Bases reconstruídas com `--pull --no-cache`, sem `--reuse-images`.

| Distro | Versão | Etapas | Tempo total |
| --- | --- | --- | --- |
| Arch | rolling 20260927.0.600689 | 12/12 | 60.19 s |
| Debian | 13.7 (trixie) | 12/12 | 67.75 s |
| Fedora | 44 | 12/12 | 61.75 s |
| Ubuntu | 24.04.5 LTS | 12/12 | 98.29 s |

Digests das bases registrados pelo BuildKit:

- Arch: `sha256:b21322c663be387c0ed9cbc7bbbfe18e41633ad4e7b7c77cfad45f128be20040`.
- Debian: `sha256:a99cfc517144bc59b1978475ec53b46ecabec7e43635402ee5b77cc54cd1b20a`.
- Fedora: `sha256:7011f51bd8089d345be42d41f0aa3190d258823528852a5e7ec976fe2fd20f53`.
- Ubuntu: `sha256:534baea6a22c03a63003dbc8dbe78fe34bc0d7e595d9a9dc9834884ff530eb55`.

IDs das imagens de teste:

- Arch: `sha256:e0ce568300260104472f6730c632341f89e2ba9a9f2a99d09b423cda2be11e3d`.
- Debian: `sha256:c9ec44504a7b4cc68de80dc158fc1ef73e075a6b41e8d192bc3f19565912747d`.
- Fedora: `sha256:be675c55fa258dcfa1b898aec97d9ac46e1fe1b7f9eba87d86f1bef9451eb5e6`.
- Ubuntu: `sha256:be8e2a61a4e61687adbb4641251fffcef1433e96f276ac72ab73877d9d2c8ba5`.

Evidências em `artifacts/compatibility-review-final`: `summary.json`,
`stages.json`, logs, inventários, metadados das imagens e capturas XWD.
As capturas convertidas ficam em `artifacts/review-final-captures`.
O smoke test padrão também passou: `artifacts/review-smoke.log`.

## Revisão e verificações adicionais

- 408 testes .NET Release, 18 testes Python de empacotamento e 16 testes da
  automação GitHub passaram. Workflows (`actionlint`), sintaxe shell, traduções e
  links locais também foram verificados.
- Corrigido um reset Xodus cancelado antes de adquirir o bloqueio que podia
  cancelar permanentemente a geração atual de serviços. Um teste garante que
  cancelar esse reset preserva o serviço para a partida seguinte.
- Fontconfig 2.15 consultava seu diretório absoluto de templates do host apesar
  de `FONTCONFIG_FILE`/`FONTCONFIG_PATH` privados. A relocação usa os templates
  incluídos no pacote e falha caso o caminho upstream mude. O teste com home/cache
  descartáveis manteve 2.416 fontes disponíveis e reduziu 45 avisos para zero;
  na base mínima do smoke test foram encontradas 16 fontes, sem avisos/erros.
- A primeira matriz (`artifacts/compatibility-review`) passou nas etapas então
  existentes, mas a inspeção dos logs encontrou `No writable cache directories`
  na criação do prefixo Wine em Arch/Fedora. O harness agora prepara o subdiretório
  privado `fontconfig`, como o smoke test já fazia, e reprova mensagens Fontconfig
  também nessa etapa. A matriz final acima repetiu as quatro bases novas e passou
  com os mesmos hashes de pacotes. Nenhum arquivo do host foi alterado.
- Benchmark RTX sintético com 2.000 arquivos descartáveis, um aquecimento e três
  medições: mediana de 184,235 ms para 18,329 ms (~90% menos). Evidência em
  `artifacts/rtx-scan-performance-results.json`; não mede uma partida real.

## Limites

Contêineres offline, sem privilégios, sem GPU/home/desktop reais, com Xvfb,
renderização por software e sandbox WebKit habilitado. Nenhuma instância real do
usuário foi usada. Contêineres compartilham o kernel do host.

Esta validação não certifica login Microsoft, keyring real, áudio físico,
Minecraft/RTX/DLSS, FUSE, Wayland nativo nem políticas SELinux/AppArmor reais.
Os hashes identificam somente estes builds locais; não certificam uma futura
release do Actions. As notas da 1.0.2 continuam em desenvolvimento.
