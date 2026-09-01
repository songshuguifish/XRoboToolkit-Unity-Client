# PICO SDK vendoring

The Unity repository is the release source of truth for the PICO package used
by this application. `Packages/manifest.json` resolves `com.unity.xr.picoxr`
from the checked-in `PICO Unity Integration SDK` directory, so a normal clone
contains the exact C# sources, native plugins, Enterprise AARs, and Unity
importer metadata required to build the APK.

`PICO_SDK_VENDOR_LOCK.json` records the upstream SDK commit, package version,
application-owned customization, and SHA-256 hashes for every vendored AAR/JAR
and its `.meta` file. Verify a clone before building with:

```bash
python3 tools/pico_vendor_lock.py
```

The SDK directory does not need its own `.git` directory. A nested Git checkout
may be useful while comparing with PICO upstream, but it is local development
metadata and is neither required nor included by the Unity repository.

## Updating the SDK

1. Prepare the new PICO SDK in a temporary checkout and record its upstream
   commit.
2. Replace the vendored package contents without copying the temporary `.git`.
3. Reapply and review the XRoboToolkit compatibility changes, especially the
   deployed `tobservicelib-release.aar` policy.
4. Update `PICO_SDK_VENDOR_LOCK.json`, then run the verifier and a clean Unity
   Android build.
5. Commit the SDK directory, lock, compatibility code, and required AAR `.meta`
   files together in the Unity repository.

The current artifacts are comfortably below GitHub's per-file size limit, so
plain Git keeps cloning and Unity setup simple. Introduce Git LFS only if future
SDK upgrades make binary history materially expensive; doing so would require
all build machines to install and pull LFS objects.
