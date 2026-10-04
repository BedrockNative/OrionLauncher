# Matriz local de compatibilidade

Verifica os **pacotes de release**, não um `dotnet run` no ambiente de desenvolvimento.
Não participa do Actions. Use antes de preparar um commit/publicação de release
que afete empacotamento ou runtimes; não é necessário em cada commit comum.

```bash
python3 packaging/linux/compat/matrix.py artifacts/release
# Uma distro ou também a base mínima oficialmente suportada:
python3 packaging/linux/compat/matrix.py artifacts/release --distros debian
python3 packaging/linux/compat/matrix.py artifacts/release --distros arch debian fedora ubuntu
```

Requisitos do **host de desenvolvimento**: Linux x86_64, Python 3, Docker Engine
acessível, rede para preparar imagens, aproximadamente 6 GiB de RAM disponíveis
e 25–35 GiB livres para imagens/pacotes/extrações. O padrão é sequencial, com
limites de 4 CPUs, 6 GiB de RAM e 512 processos por teste. Ajuste com `--cpus`,
`--memory` e `--timeout` (900 segundos por distro; preparação tem outro limite).

## O que é instalado no contêiner

- Bases oficiais: `archlinux:base`, `debian:13-slim`,
  `registry.fedoraproject.org/fedora:44`; opcionalmente `ubuntu:24.04`.
- Apenas ferramentas de teste (Python, ELF, Xvfb, captura X11), D-Bus e a base
  gráfica de software Mesa/Vulkan/OpenGL/EGL/OpenGL ES.
- **Sem instalar .NET, Wine, GTK ou WebKit**. O inventário é verificado e salvo;
  bibliotecas comuns podem ser dependências transitivas do próprio desktop.

Não existe desktop Linux literalmente sem dependências: kernel, glibc, drivers
e serviços da sessão pertencem ao sistema. O pacote atual exige glibc 2.39+;
Debian 12/Ubuntu 22.04 não fazem parte dessa compatibilidade prometida.

## Isolamento

Depois de preparar a imagem, o teste roda como usuário sem privilégios, com rede
desligada, capabilities removidas, `no-new-privileges`, sem `/dev/dri`, sem FUSE,
sem socket do Docker e sem mounts da home/desktop/repositório. Só a pasta dos
artefatos é montada, **somente leitura**. HOME, XDG, D-Bus, Xvfb e prefixo Wine
são privados e descartáveis. Não use imagens/DLLs de procedência desconhecida:
um contêiner reduz exposição, mas não substitui uma VM como limite de segurança.

## Verificações e evidências

- SHA-256 dos pacotes; extração em caminho com espaços.
- Dependências de todos os ELF do pacote, incluindo auxiliares e módulos Wine.
- EGL de software com bibliotecas do sistema e com as bibliotecas privadas do
  pacote, para detectar conflitos que `ldd` sozinho não encontra.
- CLI do launcher e Xodus.
- Criação de prefixo Wine sem diálogo de download de Mono/Gecko; presença do
  registro e D3D12.
- HTML local renderizado pelo WebKit com seus subprocessos.
- Janela real do launcher em Xvfb, via tar.gz e AppImage no modo sem FUSE.
- Logs, duração por etapa, inventário de pacotes, imagem/digest e capturas XWD.

Relatórios ficam em `artifacts/compatibility-<UTC>/`; `--output` permite escolher
uma **nova** pasta. Uma falha dá exit code diferente de zero. Não ignore falhas
de etapas mesmo se outra janela abrir. Falhas de preparação não são prova de
incompatibilidade do launcher; consulte `build.log`.

Os contêineres criados são removidos ao terminar, inclusive após falha. Imagens
de teste e relatórios permanecem para diagnóstico; o comando nunca faz `prune`
nem interfere nos outros contêineres. Para investigar mais rápido, há
`--reuse-images`; ele rejeita um harness diferente do código atual. Para a
validação final, não use essa opção: bases e pacotes de desktop são atualizados.

Para ver capturas, use um visualizador compatível com XWD ou converta **no host**,
sem instalar ImageMagick no contêiner e mascarar dependências ausentes:

```bash
magick artifacts/compatibility-<UTC>/arch/tar-gui.xwd /tmp/orion-arch.png
```

## Limites

Esta é uma verificação funcional de portabilidade, não um benchmark nem uma
garantia para toda versão de uma família de distros. Contêineres compartilham o
kernel do host e não simulam SELinux/AppArmor/serviços de um desktop completo.
Se o host bloquear namespaces exigidos pelo WebKit, registre a falha e valide
numa VM adequada; não desative o sandbox para fabricar um resultado positivo.
Login real, keyring, áudio físico, GPU NVIDIA/AMD/Intel, RTX/DLSS, desempenho,
Wayland/compositor e AppImage montado via FUSE requerem testes complementares.

## Execução registrada

Consulte [o resultado de 2026-10-04](RESULTS-2026-10-04.md): o Debian passou,
mas Arch e Fedora revelaram um conflito entre o Mesa do sistema e o Wayland
incluído no pacote. Esses artefatos **não estão validados nas três distros**.
O relatório se refere aos hashes registrados, não automaticamente a builds futuros.
