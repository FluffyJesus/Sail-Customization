# Making your own emblems

Sail Emblems paints any PNG you give it. This page says what a picture needs and how to make one in a few minutes.

## Where the files go

| Folder | What it is |
|---|---|
| `BepInEx/config/SailEmblems/emblems` | **Emblems**: one picture placed on the sail. Size, Up, Side and Mirror work on it. |
| `BepInEx/config/SailEmblems/patterns` | **Patterns**: one picture stretched over the whole sail. |

The folders are made the first time the game starts with the mod. Pictures are read when the game starts, so restart it after adding one. They appear at the end of the list, marked with `*`.

## What a picture needs

**One shape, one colour.** The mod paints with the colour you pick in the window; colours in the file are ignored. What counts is where paint goes:

- **With transparency** (recommended): transparent = canvas, opaque = paint, half-transparent = thin paint.
- **Without transparency:** black = paint, white = canvas, greys in between.

**Size and shape**

- **512 x 512 px** is right for almost everything. 256 is fine for simple shapes; above 1024 only costs memory.
- The picture does not have to be square; the emblem keeps the picture's proportions on the sail.
- Leave a **small margin** (about 30 px on a 512 picture). The edge of the picture is the edge of the emblem: Size sets the picture's height on the sail.
- Centre the shape in the picture; the picture's middle goes to the point you place.

**Lines and edges**

- Lines at least **8 px wide** on a 512 picture. A sail is seen from far away, and hairlines disappear.
- Smooth (anti-aliased) edges of 1 to 2 px look best. No blur, glow, shadows or gradients: they turn into a faint haze of paint.
- Holes inside the shape (the eye of a fish, the gaps in a wheel) are fine; make them a bit bigger than you think.

**Patterns**

- The picture is stretched over the sail's box in the sail's own axes (with Height and Width scale on top). The left edge of the picture is the left of the sail as you read it, the top is the head.
- A triangular sail cuts off corners of the picture, so keep what matters away from them.
- Design it square; the built-in patterns are drawn by code and keep their stripe widths on any sail, picture patterns stretch.

**File name**

- The file name is the name in the window (`my_flag.png` shows as `* my flag`) and is what the save remembers.
- Don't use `;`, `=` or `|` in it.
- Renaming or deleting a file a sail is painted with leaves that sail plain (the log says so); put the file back and the paint returns.
- To share a design, give your friends the file: the same name in the same folder.

## Quick ways to make one

**1. Draw it (any image editor).** Paint.NET, GIMP, Krita or the free web editor Photopea:
1. New image 512 x 512, transparent background.
2. Draw or fill your shape in black (or white, it does not matter).
3. Export as PNG into the `emblems` folder.

**2. From a picture you have** (a logo, a silhouette, clip art):
1. Open it in GIMP or Photopea and select the shape (Magic Wand or Select by Colour).
2. New 512 x 512 transparent image, paste, scale to fit with a margin.
3. Fill the selection black, export as PNG.

Public-domain clip art (for example openclipart.org, all CC0) is a good source; many of the built-in emblems come from there.

**3. From an SVG** (Inkscape): File, Export, PNG, 512 px wide, background transparent.

**4. From a symbol in a font:** type a large symbol (heraldic, nautical, Noto Symbols, emoji in black-and-white fonts) in any editor, centre it, export as PNG.

**5. The mod's own scripts** (for people who like Python): `art/make.py` draws the built-in geometric emblems as SVG, `art/trace.py` turns a picture into a clean mask, `art/patterns.py` draws the pattern pictures. They need Python 3 with Pillow, NumPy and SciPy.

## Checking it

Start the game and look in `BepInEx/LogOutput.log`:

- `Sail Emblems 1.0.0 loaded: N emblems, M patterns` counts yours too.
- `[Emblems] cannot read picture <name>` means the file is not a valid PNG.
- `[Emblems] picture name '<name>' has ; = or | in it` means rename it.

## Adding a built-in emblem to the mod

Put the PNG into `Assets/emblems` (or `Assets/patterns`), add a line to `art/SOURCES.md` with where it comes from and its licence, and build. Only your own drawings or public-domain / CC0 pictures, please.
