# Installing Orion on Arch Linux

Once published, run `yay -S orionlauncher-bin` for the official release with its bundled runtimes and CurseForge integration. Choose `yay -S orionlauncher` to compile the launcher with the .NET SDK. The source build downloads NuGet dependencies and requires separate game runtimes. Install one variant at a time.

To install directly from this repository, run as your regular user:

```bash
cd packaging/aur/orionlauncher-bin
makepkg -si
```

## First submission

Create an account at https://aur.archlinux.org and add your public SSH key to your AUR account. Each package name has a separate Git repository. From the root of this project, test each recipe in a clean Arch build environment before submitting. With Arch devtools installed, run `pkgctl build` inside each recipe directory and check startup using a disposable profile.

After testing, run the following for each package, changing the name on the first line for the second submission.

```bash
name=orionlauncher-bin
work=$(mktemp -d)
git clone "ssh://aur@aur.archlinux.org/${name}.git" "$work/$name"
cp "packaging/aur/$name/"{PKGBUILD,.SRCINFO} "$work/$name/"
git -C "$work/$name" add PKGBUILD .SRCINFO
git -C "$work/$name" commit -m "Initial package"
git -C "$work/$name" push origin HEAD:master
```

For updates, change the version, verify the upstream release checksum or source and submodule commits, and regenerate metadata with `makepkg --printsrcinfo > .SRCINFO` inside each recipe directory. Increment pkgrel for packaging-only changes and reset it to 1 for a new upstream version.
