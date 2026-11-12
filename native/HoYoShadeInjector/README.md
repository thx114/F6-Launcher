# F6ShadeInjector

Launcher-owned HoYoShade injector, BSD-3-Clause. Derived from the local
`_research/HoYoShade-upstream/inject_mod.cpp` (checkout HEAD
`17af03335ea3c86c03960cf5ccc401e1ef11294c`, source SHA256
`DC6028FC5B70BFE66EADEFA2C52105C59AF1893331723DE5C74C9B21781FD66E`).
That working source already contains the managed per-game AddonPath fix.
Original copyright notices and both licenses are retained.

The launcher builds and ships this executable in its versioned application
directory. `game.exe --shade-root <framework>` selects the framework without
replacing its injector, ReShade DLL, or user configuration. All UI/CLI/pack
launch paths use it through `InjectorHelper`.

Changes: remove the pre-readiness terminate/re-enumerate loop; ignore zero-thread
exit records and other sessions when finding an injection target; retain game
files and addons after loading. No game is terminated by this executable.

`--self-test` tests target selection with synthetic records. With a game name
and root, `--check-ready` validates framework files and emits the ready marker,
then exits without INIBuild, game process enumeration or injection.
