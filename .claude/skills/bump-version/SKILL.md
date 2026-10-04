---
name: bump-version
description: Automates version increment for Morgana and all dependent projects when starting a new development cycle, or when preparing a production hotfix. Use when the user says things like "we just released", "bump the version", "start a new version", "prepare a hotfix".
---

# bump_version

Automates version increment for Morgana and all dependent projects when starting a new development cycle.

## Trigger

Activated when the user says things like:
- "we just released"
- "bump the version"
- "start a new version"
- "increment version"
- Any request indicating the start of a new development cycle

### Hotfix Trigger

For production hotfixes, activated when the user says:
- "prepare a hotfix"
- "we need to ship a production fix"
- "hotfix release"
- Any request indicating a patch fix needs to be distributed

## Procedure

1. **Extract current version** from `<repo root>/Morgana/Directory.Build.props`
   - It is split in `<VersionPrefix>X.Y.Z</VersionPrefix>` and `<VersionSuffix>preview.N</VersionSuffix>`
   - The full version is `X.Y.Z-preview.N`. `AssemblyVersion` and `FileVersion` stay `$(VersionPrefix)`, because an assembly version cannot carry a suffix

2. **Increment the version**
   - **Default behavior (new development cycle)**: increment the preview number (N), prefix untouched
     - 1.0.0-preview.N → 1.0.0-preview.(N+1)
     - Example: 1.0.0-preview.33 → 1.0.0-preview.34
   - **Hotfix behavior**: append a patch counter to the suffix
     - 1.0.0-preview.N → 1.0.0-preview.N.1
     - Example: 1.0.0-preview.33 → 1.0.0-preview.33.1
   - Leaving preview (1.0.0 final) is the user's decision, never this skill's: then `VersionSuffix` is removed

3. **Update all version files** in the following paths:
   - `<repo root>/Morgana/Directory.Build.props`
   - `<repo root>/Channels/Cauldron/Directory.Build.props`
   - `<repo root>/Channels/Rune/Directory.Build.props`
   - `<repo root>/Channels/Grimoire/Directory.Build.props`
   - `<repo root>/Examples/Examples.csproj`
   - `<repo root>/PromptHarness/PromptHarness.csproj`
   - `<repo root>/Alembic/Directory.Build.props`

4. **Add new section in CHANGELOG.md**
   - Read `<repo root>/CHANGELOG.md`
   - Insert a new section right after the header and preamble
   - **For normal version bumps** (preview number increment):
   ```
   ## [X.Y.Z] - UNDER DEVELOPMENT
   ### ✨ Added

   ### 🔄 Changed

   ### 🐛 Fixed

   ### 📦 Dependencies

   ### 🚀 Future Enablement

   ```
   - **For hotfixes** (patch counter on the suffix):
   ```
   ## [X.Y.Z] - UNDER DEVELOPMENT
   ### 🐛 Fixed

   ```
   - Replace `X.Y.Z` with the newly calculated full version (e.g. `1.0.0-preview.34`)

5. **Communicate the result** to the user with details of the updated version

## Notes

- The skill operates idempotently: running it twice does not create duplicates
- Morgana convention: the preview number counts development cycles, the patch counter on the suffix counts hotfixes
- All solution projects are updated atomically
- The empty CHANGELOG section is ready to be filled with change details
- Regular development cycles always drop the hotfix counter (e.g., 1.0.0-preview.33.1 → 1.0.0-preview.34)
