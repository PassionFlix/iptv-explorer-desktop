Place the Windows x64 libmpv runtime here for local packaging.

Expected main library: libmpv-2.dll
Runtime location:       AppContext.BaseDirectory/native/mpv/libmpv-2.dll
Exact fallback name:    mpv-2.dll

If the selected libmpv distribution includes dependent DLLs, keep those DLLs
beside libmpv-2.dll in this same directory. No native binary is stored in this
repository.
