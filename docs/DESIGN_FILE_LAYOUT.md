# Where designs live, and what they are called

Two folders, and one rule that keeps them apart.

## The authored folder is read-only

```
Assets/RecastCustomizer/GloveAsset/
```

The mesh, the maps and the twelve materials the artist made. Three designs, numbered
`01`, `02`, `03`, for four parts.

**Nothing writes here.** Not the design importer, not the thumbnail baker, not the
tool at runtime. This folder is the default the whole project is measured against,
and a generated design must never be able to reach it. If a file appears here that
nobody authored by hand, something is wrong.

## Generated designs get a folder each

```
Assets/RecastCustomizer/NewDesigns/
  design_04/
    falcon_glove_top_04_mat.mat
    falcon_glove_bottom_04_diff.png
    falcon_glove_bottom_04_mat.mat
    falcon_glove_04_p.prefab
  design_05/
    ...
```

One folder per design, holding everything that design owns: its materials, the maps
those materials need, and its prefab. The prefab sits with the materials it uses
rather than in a separate prefabs folder, because they are one deliverable.

A design can be reviewed, zipped, handed on or deleted as a single unit, and deleting
it cannot take anything else with it.

## Names carry the design number

The design number replaces the variant number. Nothing is appended.

| Authored | Design 04 | Design 05 |
|---|---|---|
| `falcon_glove_bottom_02_diff.png` | `falcon_glove_bottom_04_diff.png` | `falcon_glove_bottom_05_diff.png` |
| `falcon_glove_bottom_02_mat.mat` | `falcon_glove_bottom_04_mat.mat` | `falcon_glove_bottom_05_mat.mat` |
| `falcon_glove_02_p.prefab` | `falcon_glove_04_p.prefab` | `falcon_glove_05_p.prefab` |

So every generated name is the same shape and the same length as the ones the artist
wrote, and the number tells you which design it belongs to at a glance.

**Why not a suffix.** The first version of the importer appended instead, producing
`falcon_glove_bottom_02_diff_adjusted` and `falcon_glove_bottom_02_mat_design6`. That
does not survive a second design. The suffixes stack, the `02` stops meaning anything
because it now refers to the design's ancestor rather than the design, and two designs
built on the same part sort next to each other and read as versions of one file.

## Where the number comes from

Read from the design's own name, so a design called "DESIGN 6" becomes `06` and its
files match what the person who made it called it. A design with a real name like "Sun
Bleached" takes the next free number instead. Either way the number is checked against
the variant set first, so two designs can never claim the same one.

## Only what is genuinely new gets written

A part the designer merely swapped already has a finished material in `GloveAsset`, so
the design points straight at it and no asset is created. Only a part that was actually
tuned costs anything.

One design can therefore produce a single material rather than four. This matters: a
full material set per design multiplies assets fast, and on a live-service project that
is a real cost rather than an argument about tidiness.

Of the five look controls, two are free and three are not:

| Control | Becomes | Cost |
|---|---|---|
| Smoothness | a material value | none |
| Normal strength | a material value | none |
| Hue, saturation, brightness | a baked texture | one map per part |

The tool shows this while a design is being made, as **new maps if saved** in the asset
panel.

## Removing a design

**Recast Customizer ▸ Remove Imported Design.** It deletes the design's folder, removes
its variant entry from every part, and destroys any instance left in the open scene.
Materials the design only referenced are left alone, because the design never owned
them.

Designs imported before this layout existed are also handled. Those scattered their
assets through `GloveAsset` with suffixed names, and are found and removed by name.
