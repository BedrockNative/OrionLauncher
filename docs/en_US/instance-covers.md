# Optional instance cover images

Images are fetched on selection, not included in Orion's binaries or downloaded
for the default color-and-initial cards. The fixed catalog is in
`src/Orion.Desktop/Content/InstanceCovers.cs`.

The following Wikimedia Commons file pages attribute the images to **Xbox México**
and identify their license as [CC BY 3.0](https://creativecommons.org/licenses/by/3.0/):

- [Minecraft — 1.18 mountains](https://commons.wikimedia.org/wiki/File:Minecraft_-_1.18_mountains.jpg)
- [Minecraft — Jungle](https://commons.wikimedia.org/wiki/File:Minecraft_-_Jungle.jpg)
- [Minecraft — Lush caves](https://commons.wikimedia.org/wiki/File:Minecraft_-_Lush_caves.jpg)

These are screenshots extracted from Xbox México's *Xbox Masterclass: TIPS y consejos
para ser un experto en Minecraft*. Orion resizes them to 320 pixels wide and crops
their display to the card's aspect ratio. Image rights are separate from Orion's
MIT license. Source and license links, attribution, and the modification notice
are displayed in the appearance editor; cards also expose attribution in a tooltip.
No endorsement or affiliation with Mojang or Microsoft is implied.

Downloads accept only the catalog's HTTPS URLs, PNG/JPEG responses up to 2 MiB,
and landscape thumbnails. Writes are atomic. A corrupt thumbnail is discarded
and fetched again. Disk cache lives in `$XDG_CACHE_HOME/orion-launcher/covers`;
reference-counted decoded images are released when no viewer uses them.
