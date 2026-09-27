Place the Windows x64 libmpv runtime here for local development/package testing.

Expected main library: libmpv-2.dll
Runtime location:       AppContext.BaseDirectory/native/mpv/libmpv-2.dll
Exact fallback name:    mpv-2.dll

For the official 1.0 packaging flow, run:

  scripts\Get-LibMpvRuntime.ps1

The pinned source URL and SHA-256 are stored in build/libmpv-runtime.json.
Release packaging uses the x86_64 LGPL-labelled community build documented in THIRD_PARTY_NOTICES.md.

No native libmpv binary is stored in this Git repository.
