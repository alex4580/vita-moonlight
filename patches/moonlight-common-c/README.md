# moonlight-common-c backport

The Vita build pins `moonlight-common-c` at
`07c32c80f98bb0d7214c577bd080eea3ce64a856`. The VPK also carries the exact
single-file diff from upstream commit
`7b026e77be62175104640e7e722b758df6d3d0d7`, which rejects malformed RTSP
session headers and caps RTSP responses at 1 MiB.

The submodule is never patched in place. At build time,
`tools/prepare-moonlight-common-rtsp.py` verifies the submodule source and patch
hashes, applies the diff in memory, verifies the resulting hash, and writes an
LF-normalized copy under the CMake build directory. CMake compiles that copy
instead of the submodule file and repeats input verification on every build.
Both exact LF and Windows CRLF checkouts of the pinned source are recognized;
no other source bytes are accepted.

Run the release/CI assertion locally with:

```sh
python tools/check-moonlight-common-backport.py
```

When updating the submodule or retiring the backport, update the checker,
hashes, CMake wiring, and this note together. Do not regenerate the patch from a
moving upstream branch.
