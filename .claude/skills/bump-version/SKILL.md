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
   - It is `<VersionPrefix>X.Y.Z</VersionPrefix>`, with no `VersionSuffix`: since 1.0.0 Morgana ships stable versions only
   - `AssemblyVersion` and `FileVersion` stay `$(VersionPrefix)` and are never touched

2. **Increment the version** following Semantic Versioning
   - **Default behavior (new development cycle)**: increment the minor number and reset the patch number
     - X.Y.Z → X.(Y+1).0
     - Examples: 1.0.0 → 1.1.0, 1.1.2 → 1.2.0
   - **Hotfix behavior**: increment the patch number
     - X.Y.Z → X.Y.(Z+1)
     - Example: 1.1.0 → 1.1.1
   - A major increment (X+1.0.0) is the user's decision, never this skill's: it happens only when the user names the major version
   - Never add a `VersionSuffix`, unless the user explicitly asks for a prerelease

3. **Update all version files**: every `.props` and `.csproj` file under `<repo root>` (excluding `bin/` and `obj/`) that declares `<VersionPrefix>`
   - Find them by searching, never from a remembered list: a project added since the last bump must not be missed
   - At the time of writing they are:
     - `<repo root>/Morgana/Directory.Build.props`
     - `<repo root>/Channels/Cauldron/Directory.Build.props`
     - `<repo root>/Channels/Rune/Directory.Build.props`
     - `<repo root>/Channels/Grimoire/Directory.Build.props`
     - `<repo root>/Examples/Examples.csproj`
     - `<repo root>/Examples/GardeningMCP/GardeningMCP.csproj`
     - `<repo root>/PromptHarness/PromptHarness.csproj`
     - `<repo root>/PromptHarness/Plugins/FederationPlugin/FederationPlugin.csproj`
     - `<repo root>/PromptHarness/Plugins/PoisonedPlugin/PoisonedPlugin.csproj`
     - `<repo root>/PromptHarness/Plugins/UnlabelledIntentPlugin/UnlabelledIntentPlugin.csproj`
     - `<repo root>/Alembic/Directory.Build.props`
   - Every file gets the same version: a file found on a different version is reported to the user and aligned

4. **Update the Docker image versions** in `<repo root>/production.env.template`
   - Every `*_VERSION=` line in its `DOCKER VERSIONS` block gets the new version, never `latest`
   - The file is what a production installation copies at a release tag, so it names the images published from that same tag

5. **Add new section in CHANGELOG.md**
   - Read `<repo root>/CHANGELOG.md`
   - Insert a new section right after the header and preamble
   - **For new development cycles** (minor increment):
   ```
   ## [X.Y.Z] - UNDER DEVELOPMENT
   ### ✨ Added

   ### 🔄 Changed

   ### 🐛 Fixed

   ### 📦 Dependencies

   ### 🚀 Future Enablement

   ```
   - **For hotfixes** (patch increment):
   ```
   ## [X.Y.Z] - UNDER DEVELOPMENT
   ### 🐛 Fixed

   ```
   - Replace `X.Y.Z` with the newly calculated version (e.g. `1.1.0`)

6. **Communicate the result** to the user with details of the updated version

## Notes

- The skill operates idempotently: running it twice does not create duplicates
- Morgana convention: the minor number counts development cycles, the patch number counts hotfixes
- All solution projects are updated atomically
- The empty CHANGELOG section is ready to be filled with change details
- Regular development cycles always reset the patch number (e.g., 1.0.3 → 1.1.0)