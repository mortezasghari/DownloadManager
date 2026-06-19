# ADR-0017: IDM-style extension routing — resolved at download start, container-vs-content placement

- Status: Accepted
- Date: 2026-06-19

## Context

Completed files should land in per-category folders (video → Videos, audio → Music, …) like IDM, instead
of all piling into one Downloads directory. The routing config lives in the same `settings.json` and
shares the per-platform path resolution introduced in ADR-0016, which is why the two are one phase.

## Decision

### Detection by file extension, resolved at download **start**

The destination is chosen from the file **extension** (from the URL/filename) **before any bytes are
requested**. This is forced by the architecture, not preference: preallocation and positioned segment
writes (ADR-0005/0006) need the final path up front. Therefore:

- **No content sniffing** — it is fundamentally incompatible with preallocate-at-start; you cannot read
  response bytes to pick a folder you already had to open and preallocate.
- **`Content-Type` is at most a fallback tiebreaker**, never the primary signal, for the same reason.
- Routing is a **pure synchronous** `ResolveDestination(fileName, explicitPathOrNull)` — it only selects
  a directory/path and ensures it exists. The engine's write/preallocation/durability path is unchanged;
  it still just receives a final path.

### Container-vs-content: the key placement rule

- **Terminal content types** — video, audio, documents, pictures — *are* their content, so they route to
  the matching **semantic user folder** (macOS video → **Movies**, not Videos).
- **Containers** — archives (zip/rar/7z/tar/…) and executables (exe/msi/dmg/deb/appimage/…) — their
  extension does **not** tell you the contained content, so routing them into a guessed semantic folder
  would be wrong. They go to **neutral dedicated subfolders inside Downloads**: `Downloads/Archives` and
  `Downloads/Programs`.
- **Unknown / extensionless** → the **Downloads root** catch-all.

### Per-platform folders, kept deliberately simple

Each category folder defaults to `UserProfile/<FolderName>`; relative folders in config resolve against
the user profile, absolute folders are used verbatim. We do **not** parse XDG user-dirs or chase Windows
known-folder GUIDs for v1 — plain `UserProfile/<Folder>` defaults, fully overridable in `settings.json`.
Every category's extension list is user-extensible. Target folders (including `Downloads/Archives` and
`Downloads/Programs`, which won't pre-exist) are **created on demand**.

### Explicit path and collisions

1. A caller-supplied explicit per-download destination **wins** — routing never overrides it.
2. Otherwise extension → category folder; unknown/extensionless → Downloads root.
3. The folder is created if absent.
4. A filename collision **auto-renames** `name (1).ext`, `name (2).ext`, … — never an overwrite.

## Consequences

- Files self-organise IDM-style, configurable per category and extension, with zero engine changes.
- The placement is defensible: content types get semantic folders; containers stay in neutral Downloads
  subfolders rather than a guessed location.
- Because routing only picks a path and is resolved at start, it composes cleanly with preallocation and
  the unchanged durability path; no content sniffing is ever introduced.