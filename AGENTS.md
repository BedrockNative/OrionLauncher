# Orion — instruções para agentes

## Preservação e escopo

- Preserve mudanças existentes. Não publique commits, tags ou releases sem pedido.
- Nunca exponha nem versione credenciais do CurseForge, contas ou dados de jogo.
- Testes de runtime devem usar dados descartáveis, nunca instâncias reais do usuário.

## Antes de preparar um commit de release

Aplica-se a mudanças em `RELEASE_VERSION`, notas de release, empacotamento,
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

Contêineres compartilham o kernel do host. Passar na matriz não certifica GPU
física, desempenho de Minecraft/RTX/DLSS, login Microsoft, Wayland nativo, FUSE
ou políticas SELinux/AppArmor de cada desktop. Essas verificações precisam de
VMs/instalações reais e, quando pertinente, hardware real.
