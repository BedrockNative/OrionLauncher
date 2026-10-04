<p align="center">
  <img src="src/Orion.Desktop/Assets/orion.svg" width="88" alt="Orion logo">
</p>

<h1 align="center">Orion Launcher</h1>
<p align="center"><strong>Minecraft Bedrock, at home on Linux.</strong></p>
<p align="center">
  <a href="https://github.com/BedrockNative/OrionLauncher/releases"><img src="https://img.shields.io/github/v/release/BedrockNative/OrionLauncher?color=91cbbb" alt="Latest release"></a>
  <img src="https://img.shields.io/badge/platform-Linux_x86__64-91cbbb" alt="Linux x86_64">
  <a href="LICENSE"><img src="https://img.shields.io/badge/license-MIT-91cbbb" alt="MIT license"></a>
</p>

Orion is a **Minecraft Bedrock launcher for Linux**, bringing the game and its
online services into a native-like desktop experience through our own **WineGDK**
fork. Manage instances, Microsoft accounts, addons, textures and worlds in one place.
Available in English and Brazilian Portuguese.

> Practically native in daily use — not an official Linux port. Minecraft still
> runs through WineGDK. A Minecraft for Windows license and compatible graphics
> drivers are required; support varies with the game version, hardware and service.

## 🚀 Get started

Download an **AppImage** or **tar.gz** from [Releases](https://github.com/BedrockNative/OrionLauncher/releases),
sign in on **Account**, and create your first instance. Portable releases include
the launcher runtime stack; no .NET SDK is needed to play.

[Setup & usage](docs/en_US/getting-started.md) · [Linux requirements](RELEASING.md#stack-incluída-e-limites-do-sistema) ·
[Changelog](CHANGELOG.md)

## 🧭 Roadmap

Checked items are implemented, not a guarantee for every game version or device.

- [x] **Instance manager** — independent instances and Microsoft accounts.
- [x] **Our own WineGDK fork** — deep Linux desktop integration for a native-like game experience.
- [x] **Native Xbox account sign-in** — Microsoft login directly in the game.
- [x] **Online features** — friends' worlds, servers, parties, Realms, Marketplace purchases/downloads, friend requests and achievements.
- [x] **Resource manager** — manage addons, textures and maps without navigating complicated folders. *Experimental.*
- [x] **RTX integration** — Vanilla RTX, BetterRTX and NVIDIA DLSS. *Experimental; compatible hardware and drivers required.*
- [x] **Native file picker** — import custom skins, worlds and textures, plus import/export of `.mcstructure` files.
- [ ] **Native mod integration** — under review; a redesigned system is planned. Bedrock addons are already supported.

## 📚 Explore & contribute

[Content & CurseForge](docs/en_US/content.md) · [RTX Studio](docs/en_US/rtx.md) ·
[Appearance](docs/en_US/appearance-options.md) · [Architecture](docs/en_US/architecture.md)

Contributions go to **`development`**. **`main`** is the publication branch and
accepts release/configuration PRs only from this repository's `development`.
See [contributing](docs/en_US/contributing.md), [development setup](docs/en_US/development.md)
and [release workflow](RELEASING.md).

## 💬 Questions, bugs & suggestions

[Open an issue](https://github.com/BedrockNative/OrionLauncher/issues/new/choose)
and choose **Bug**, **Question** or **Suggestion**. English and Portuguese are welcome.
Reporting instructions: [English](docs/en_US/issues.md) · [Português](docs/pt_BR/issues.md).

## Credits

Maintained by [yPerfectBR](https://github.com/yPerfectBR) and
[raonygamer](https://github.com/raonygamer).

- [BedrockNative/Xodus](https://github.com/BedrockNative/xodus) and
  [BedrockNative/WineGDK](https://github.com/BedrockNative/WineGDK): runtime forks.
- [JaymeFernandes](https://github.com/JaymeFernandes): interface translation system.
- [BetterRTX](https://bedrock.graphics) and
  [Cubeir](https://github.com/Cubeir/Vanilla-RTX): RTX catalog, formats and texture packs.

[MIT license](LICENSE).

<sub>Orion Launcher is not affiliated with or associated with Mojang or Microsoft.</sub>
