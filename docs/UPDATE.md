# Update design

NovaClip 1.0.0-beta.7 has two explicit update modes: installed setup coverage and portable replacement.

## Common verification

The release service accepts only safe setup/portable filenames and supported MIME types. The GitHub asset must have a positive declared size and a \`sha256:\` digest. NovaClip downloads \`novaclip-update-manifest.json\` and \`.sig\`, verifies the RSA-PKCS#1 SHA-256 signature with the embedded public key, requires key ID \`novaclip-beta7-2026\`, and matches version, channel, runtime, package type, name, size and SHA-256 before selecting the package.

Missing digest, missing signed manifest, mismatched key ID, signature, size, channel or asset hash rejects the update before updater handoff.

## Installed build

NovaClip downloads the matching \`*-setup.exe\` into a bounded temporary directory, verifies it, starts the independent updater and closes the app. The updater waits until NovaClip exits, launches Inno Setup silently against the same AppId/per-user install directory, then restarts NovaClip.

## Portable build

The portable ZIP contains \`portable.marker\`, \`NovaClip.Updater.exe\` and \`novaclip-package-manifest.json\`. The package extractor enforces entry/size/ratio/disk/reparse limits. The updater waits for the app, validates the package file set, journals old application-owned files, replaces them atomically and rolls back only paths recorded as backed up. User data remains outside the replacement set.

## Repository access

The official repository and Releases are public, so normal update checks are anonymous. A developer testing a private fork may expose a read-only token through \`NOVACLIP_GITHUB_TOKEN\`; NovaClip never embeds or persists a token.
