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

1. **Extract current version** from `/home/marco/RiderProjects/Morgana/Morgana/Directory.Build.props`
   - Extract the value of `<Version>X.Y.Z</Version>`

2. **Increment the version** using Semantic Versioning
   - **Default behavior (new development cycle)**: Increment the minor version (Y)
     - X.Y.Z → X.(Y+1).0
     - Example: 0.25.0 → 0.26.0
   - **Hotfix behavior**: Increment the patch version (Z)
     - X.Y.Z → X.Y.(Z+1)
     - Example: 0.25.0 → 0.25.1

3. **Update all version files** in the following paths:
   - `/home/marco/RiderProjects/Morgana/Morgana/Directory.Build.props`
   - `/home/marco/RiderProjects/Morgana/Channels/Cauldron/Directory.Build.props`
   - `/home/marco/RiderProjects/Morgana/Channels/Rune/Directory.Build.props`
   - `/home/marco/RiderProjects/Morgana/Channels/Grimoire/Directory.Build.props`
   - `/home/marco/RiderProjects/Morgana/Morgana.Examples/Morgana.Examples.csproj`

4. **Add new section in CHANGELOG.md**
   - Read `/home/marco/RiderProjects/Morgana/CHANGELOG.md`
   - Insert a new section right after the header and preamble
   - **For normal version bumps** (Y increment):
   ```
   ## [X.Y.Z] - UNDER DEVELOPMENT
   ### ✨ Added

   ### 🔄 Changed

   ### 🐛 Fixed

   ### 🚀 Future Enablement
   ```
   - **For hotfixes** (Z increment):
   ```
   ## [X.Y.Z] - UNDER DEVELOPMENT
   ### 🐛 Fixed
   ```
   - Replace `X.Y.Z` with the newly calculated version

5. **Communicate the result** to the user with details of the updated version

## Notes

- The skill operates idempotently: running it twice does not create duplicates
- Morgana convention: increment **minor (Y)** for new development cycles, **patch (Z)** for hotfixes only
- All solution projects are updated atomically
- The empty CHANGELOG section is ready to be filled with change details
- Regular development cycles always reset patch to 0 (e.g., 0.25.1 → 0.26.0, or 0.25.0 → 0.26.0)
