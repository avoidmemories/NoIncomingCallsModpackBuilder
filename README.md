# No Incoming Calls Modpack Builder

Windows tool that creates a version-aware GTA V `.oiv` or `.zip` package to suppress incoming NPC phone calls

The builder does not ship old game scripts. It reads the original scripts from the selected GTA V Legacy or Enhanced installation at build time, validates their layout, modifies only the selected script categories in memory, and writes those copies into a new package. It never changes the game installation itself.

## Use

1. Run **No Incoming Calls Modpack Builder**.
2. Select the GTA V Legacy or Enhanced installation folder.
3. Select `PRIMARY`
4. Select **Build Modpack**.

The first build asks where packages should be saved. That export folder is remembered; use `>` → **Set export folder** to change it later.

`PRIMARY` contains `dialogue_handler.ysc`. `OPTIONAL` contains eight dedicated ambient-call scripts. The builder creates a package only when at least one category is selected.

## Requirements

- Windows 10 or 11
- GTA V Legacy or Enhanced installed locally
- .NET 10 Desktop Runtime for the prebuilt application, or .NET 10 SDK to build from source

## Build from source

CodeWalkerProjects is an external Git submodule. Clone recursively:

```powershell
git clone --recurse-submodules https://github.com/avoidmemories/NoIncomingCallsModpackBuilder.git
cd NoIncomingCallsModpackBuilder
dotnet build ".\No Incoming Calls Modpack Builder.csproj" -c Release
```

If the repository was cloned without its submodule:

```powershell
git submodule update --init --recursive
```

The executable is produced in `bin\Release\net10.0-windows\`.

## Dependency and attribution

The project uses [`CodeWalker.Core`](https://github.com/crxhvrd/CodeWalkerProjects) only to read GTA V RPF archives and encryption keys. The complete upstream project remains an external submodule under `external/CodeWalkerProjects`; it is not part of this application's source tree. See [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md) for its attribution and separate terms.

## License

The source code written for this application is released under the [MIT License](LICENSE). CodeWalkerProjects is a separate dependency with its own terms; it is not relicensed by this repository.
