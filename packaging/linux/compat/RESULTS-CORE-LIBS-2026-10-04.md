# Validação do empacotamento sem bibliotecas centrais — 2026-10-04

Os dois formatos finais passaram nas 12 etapas em Arch rolling, Debian 13.7,
Fedora 44 e Ubuntu 24.04.5 LTS. As oito capturas foram inspecionadas e mostram
a janela do launcher renderizada. Dez inicializações adicionais em Arch
(cinco por formato) também passaram, sem novos dumps de crash.

## Código e artefatos

Mudanças locais na `development`, sobre `822358b`, para o desenvolvimento da
1.0.2. `RELEASE_VERSION` permanece em **1.0.1**, por isso os arquivos de teste
ainda têm esse número. Este teste não publica nem substitui a release 1.0.1.
Build sem credencial CurseForge, com dados descartáveis e SDK no contêiner.

O snapshot de build foi comparado com todos os 313 arquivos de código/documentação
existentes antes deste relatório; os hashes estão em
`artifacts/build-source-sha256.json`. Pacotes finais:
`artifacts/build-output/release-core-libs-final`.

| Arquivo | SHA-256 |
| --- | --- |
| AppImage | `cd59cc5882c1bac310d1717060cb29f5c823438368571d4997342ac1d7b2a5c6` |
| tar.gz | `0f1e674f2d8ec88eb6930198f7c10a43477743a066e8379a5214227a490cf2a5` |
| build-manifest.json | `857dced51cd1dbff727c8412f2aaeaa2c7452cfddfcb0ad0de45134650b37d01` |

Base do build: `mcr.microsoft.com/dotnet/sdk:10.0-noble`, digest
`sha256:e70cdb7f80b0348f5cb85f19a8f670fca061f033d57eed12fa003d58b0e06317`.
WineGDK, Xodus e demais fontes continuam fixados em `stack.lock.json`.

A auditoria dos 458 ELF do tar não encontrou arquivos com nome/SONAME coberto
pela política de exclusão. O manifesto registra 50 nomes fornecidos pelo host,
dos quais 34 não eram excluídos pela política anterior. A auditoria também exige
a presença das bibliotecas privadas de fontes e bzip2 descritas abaixo.
Resultado completo: `artifacts/payload-audit-final.json`.

## Matriz final

Bases reconstruídas com `--pull --no-cache`, sem `--reuse-images`:

```bash
python3 packaging/linux/compat/matrix.py \
  artifacts/build-output/release-core-libs-final \
  --distros arch debian fedora ubuntu \
  --output artifacts/compatibility-core-libs-final
```

| Distro | Versão | Etapas | Tempo total |
| --- | --- | --- | --- |
| Arch | rolling `20260927.0.600689` | 12/12 | 54,96 s |
| Debian | 13.7 (trixie) | 12/12 | 65,96 s |
| Fedora | 44 | 12/12 | 60,26 s |
| Ubuntu | 24.04.5 LTS | 12/12 | 72,47 s |

Digests das bases, registrados nos logs do BuildKit:

- Arch: `sha256:b21322c663be387c0ed9cbc7bbbfe18e41633ad4e7b7c77cfad45f128be20040`.
- Debian: `sha256:a99cfc517144bc59b1978475ec53b46ecabec7e43635402ee5b77cc54cd1b20a`.
- Fedora: `sha256:7011f51bd8089d345be42d41f0aa3190d258823528852a5e7ec976fe2fd20f53`.
- Ubuntu: `sha256:534baea6a22c03a63003dbc8dbe78fe34bc0d7e595d9a9dc9834884ff530eb55`.

IDs das imagens de teste:

- Arch: `sha256:77186cb29220aaea397e07ac92332fb789083deb90ab8bac604d3915050a2a7a`.
- Debian: `sha256:4b73605c4bea8a64a12751171933f44be0840c6d8acef56a86a6ed67f9185d24`.
- Fedora: `sha256:a7eae70f9c0b6992628387ae143a314015390093bbd4c954c3203d55e3291dd9`.
- Ubuntu: `sha256:37c4baa2a7c7d48ee2f997d6681f8dfe4ab7943b6632073da9e0ae0167ef6225`.

`artifacts/compatibility-core-libs-final` preserva `summary.json`, `stages.json`,
logs, inventários, imagens/digests e capturas XWD. As oito capturas convertidas
e sua visão conjunta ficam em `artifacts/core-libs-captures`.
`artifacts/compatibility-core-libs-diagnostic-final` registra as 22 etapas do
diagnóstico Arch: a bateria normal e dez inicializações extras, todas aprovadas.

## Falhas encontradas e corrigidas

A primeira matriz, em `artifacts/compatibility-core-libs`, não foi aceita:

- **Arch:** o primeiro GUI do tar abria, mas inicializações seguintes abortavam
  com `free(): invalid pointer`. O diagnóstico reproduziu a falha e capturou
  um stack nativo passando pelo HarfBuzz do host e por `hb_font_create` de
  `libHarfBuzzSharp.so`. Fontconfig/FreeType/HarfBuzz/FriBidi voltaram a formar
  uma stack privada coordenada. Os pacotes reconstruídos passaram tanto na
  matriz nova quanto nas dez inicializações extras. Dumps/logs iniciais ficam
  em `artifacts/compatibility-core-libs-diagnostic` e
  `artifacts/arch-native-backtrace.log`.
- **Fedora:** `libbz2.so.1.0`, usada pelos binários Ubuntu, não era encontrada,
  apesar do pacote bzip2 do host usar outro SONAME. A biblioteca Ubuntu continua
  privada; não foi criado um alias entre ABIs.
- **Ubuntu:** a base já contém UID 1000, impedindo a criação do usuário de teste.
  O preparo agora reaproveita esse usuário no contêiner descartável, com nome
  e home privados, preservando a execução sem privilégios.

Essas exceções não reintroduzem os runtimes GCC, X11/XCB, bibliotecas centrais
de compressão ou interfaces de áudio/dispositivos/sessão excluídas.

Validação adicional: 385 testes .NET Release, 16 testes Python de empacotamento,
16 testes da automação GitHub, `actionlint`, sintaxe dos scripts e links locais
passaram. A primeira execução .NET teve uma falha de teardown em
`HeadlessUnitTestSession.Dispose`; o teste isolado e duas execuções completas
subsequentes passaram. Não houve alteração no código desse teste.

## Limites

Os testes usam containers offline, usuário sem privilégios, dados descartáveis,
Xvfb e renderização por software, sem GPU/home/desktop reais e sem desativar o
sandbox WebKit. A base instala os requisitos comuns de desktop documentados;
não instala .NET, Wine, GTK ou WebKit. Containers compartilham o kernel do host.

Esta validação não certifica login Microsoft, keyring real, áudio físico,
Minecraft/RTX/DLSS, FUSE, Wayland nativo, desempenho ou políticas SELinux/AppArmor
de desktops reais. Os hashes acima identificam apenas estes builds locais;
uma futura release do Actions deverá gerar seus próprios artefatos e evidências.
