# Sail Customization

**Paint emblems and patterns on your sails.** A [BepInEx](https://github.com/BepInEx/BepInEx) mod for **Sailwind**.

Bring your own colours to sea: a red lion on the mainsail, a seigaiha wave pattern on a lateen, a saltire on the topsail. Paint at any shipyard, on new sails and on the ones you already have. The paint soaks into the canvas, stays upright on every kind of sail, and stays with the boat through furling, storms, shipyard visits and saves.

<img width="1001" height="842" alt="image" src="https://github.com/user-attachments/assets/134ae748-a6e1-4981-bde0-3949ca0ce65b" />

---

## Contents

- [Features at a glance](#features-at-a-glance)
- [How to paint](#how-to-paint)
- [Emblems and patterns](#emblems-and-patterns)
- [Your own pictures](#your-own-pictures)
- [Prices](#prices)
- [Installation](#installation)
- [Configuration](#configuration)
- [Compatibility and saves](#compatibility-and-saves)

---

## Features at a glance

- **48 emblems and 11 whole-sail patterns**, 13 period paints (madder red, ochre, navy, lamp black, bone white...).
- **New and installed sails** can be painted at the shipyard; the order pays for it, Cancel takes it back.
- **Size and place:** emblems can be scaled and moved up, down and sideways; patterns can be scaled up and across the sail and shifted both ways.
- **Laid out on the real cloth:** an emblem stands upright and keeps its proportions on every sail, even where the game's texture runs sideways or is warped; it shrinks to stay on narrow triangles.
- **Patterns drawn from the sail's own shape:** stripes and crosses keep their width on a wider sail, splits and crosses meet at the middle of a triangular sail, the border follows the cloth's edge. On gaffs and lugs a pattern runs along the boom.
- **Kept everywhere:** furling and setting, recolouring, leaving the shipyard, saving and loading.
- **Your own PNGs** show up in the paint window next to the built-in ones.

## How to paint

1. Bring your boat into a shipyard and select a sail (a new one or one you already have).
2. Press **Paint sail** (it stands with the sail's move and width buttons).
3. Pick an emblem or a pattern, a paint colour, and adjust:
   - **Emblems:** Size, Up, Side, Mirror.
   - **Patterns:** Height and Width scale, Up, Side, Mirror, **Along the foot** (lay it along the sail's lower edge instead of level; on by default for gaffs and lugs).
   - **Reset** puts the placement back, **none** takes the paint off.
4. Every click shows on the boat at once. Confirm the order to keep it, or Cancel to go back.

Square sails read correctly from the deck looking forward; fore-and-aft sails read correctly from starboard. **Mirror** swaps left and right.

## Emblems and patterns

<img width="1100" height="2460" alt="image" src="https://github.com/user-attachments/assets/cb9862f8-5100-48b3-b4ad-c2a10641325b" />

<img width="1200" height="480" alt="image" src="https://github.com/user-attachments/assets/2f23efba-965e-4094-9d0c-668d956db4e5" />


Sources and licences of the pictures are drawn for the mod or converted from public-domain (CC0) artwork.

## Your own pictures

Put PNG files into `BepInEx/config/SailEmblems/emblems` (an emblem, placed and scaled on the sail) or `BepInEx/config/SailEmblems/patterns` (stretched over the whole sail). They appear in the window marked with `*` the next time the game starts.

See **[EMBLEMS.md](EMBLEMS.md)** for what a picture needs and the quickest ways to make one.

## Prices

- A new painted sail costs **10% more** than the plain one.
- Repainting a sail you already have adds a **"painting"** line to the order: 10% of the sail's price.
- Taking the paint off is free.

## Installation

1. Install [BepInEx 5](https://github.com/BepInEx/BepInEx/releases) for Sailwind.
2. Unpack the release zip into the game folder, so that you get `BepInEx/plugins/SailEmblems/SailEmblems.dll`.
3. Start the game.

## Configuration

`BepInEx/config/fluffyjesus.sailwind.sailemblems.cfg` (or in-game with the Configuration Manager):

| Setting | Default | |
|---|---|---|
| General / Enabled | true | Offer painting at the shipyard |
| General / PriceShare | 0.1 | Painting price as a share of the sail price |
| Look / MaxResolution | 2048 | Largest side of a painted texture. A sail gets about 100 px per metre up to this; lower it to save video memory on big fleets |
| Shipyard / ButtonColumn, ButtonRow | 4, 0 | Where the Paint button stands, in the grid of the Width buttons |

## Compatibility and saves

- **Requires** only BepInEx 5. Every other mod named here is optional.
- **Saves:** the paint is stored in the save's own mod data, matched to each sail by its place on the mast. Saves without the mod load fine (plain sails); removing the mod leaves plain sails.
- **Sail Damage** (1.0.2 or newer): a painted sail tears with its emblem, patches are plain sailcloth, and a torn sail can be repainted. With older versions a torn sail keeps the emblem it had when it first tore.
- **Studding Sails:** studding sails keep plain canvas.
- **Shipyard Expansion:** its sail texture choice is painted over.
- **Not paintable:** the junk's square sail (its cloth has no texture layout, the game shows it in one flat colour) and the fin sail (its texture layout is too warped for a clean picture). The button says "Can't paint this sail".

<img width="1406" height="1102" alt="image" src="https://github.com/user-attachments/assets/5537a68e-e2ec-4826-a729-9bc212d4872b" />

<p align="center"><i>Fly your colours.</i></p>
