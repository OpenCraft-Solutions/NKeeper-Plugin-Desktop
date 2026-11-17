# NKeeper Desktop plugin

The official `desktop` plugin for [NKeeper](https://github.com/OpenCraft-Solutions/NKeeper) 2.0: remote desktops in the browser, over RDP, through [Apache Guacamole's](https://guacamole.apache.org/) `guacd`.

- **Service type** `rdp`, port 3389, checked with a TLS probe.
- **Session type** `rdp` ("Desktop"), a stream session on `rdp` services, through jump hosts too.
- **Host action** `rdp.desktop` ("Desktop"), which opens it with a saved or typed credential.
- **Frontend:** the `rdp` session view, drawing Guacamole's own JavaScript client (`guacamole-common-js`, Apache 2.0, in `frontend/src/vendor`) into the tab.

NKeeper's core makes the connection: it checks the server's certificate, signs in with the credential and talks to guacd, so this plugin never sees a secret, and the browser's instructions pass through core's allow-list. The plugin relays Guacamole instructions between the browser and core's desktop connection ([architecture.md §7.20–7.21](https://github.com/OpenCraft-Solutions/NKeeper/blob/main/docs/architecture.md)). The server needs guacd (`Guacd:Address`); without it the session ends with `gateway_unavailable`.

The plugin's ID is `desktop`, and it loads on NKeeper servers with SDK v1 (`>=1.0.0 <2.0.0`).

## Layout

| Path | What it is |
| --- | --- |
| `backend/` | The plugin: `DesktopPlugin.cs`, its manifest `nkeeper-plugin.json`, and the project that builds, packs and signs it |
| `frontend/` | The session view (Vite, React, TypeScript), built to `frontend/dist/index.js` |
| `tests/` | The backend tests (`DesktopTests.cs`) |
| `tests/e2e/` | `desktop.mjs`, the real-browser test, run against an installed NKeeper |

This repository references nothing of NKeeper's source. It builds against what NKeeper publishes to GitHub Packages (architecture.md §9): NuGet `NKeeper.Abstractions` (the SDK), `NKeeper.Plugin.Build` (build, pack and sign) and `NKeeper.Testing` (the test server and fixtures), and npm `@opencraft-solutions/nkeeper-sdk`, which the frontend imports as `@nkeeper/sdk`.

## Build

You need the .NET 10 SDK (see `global.json`) and Node.js 24 (see `.nvmrc`).

GitHub Packages needs a token to restore, even for public packages: a personal access token with `read:packages`. Set it in your environment, never in a file in the repository:

```bash
export NUGET_USERNAME=<your GitHub user name>
export NUGET_TOKEN=<token>
export NODE_AUTH_TOKEN=<token>
```

`nuget.config` and `frontend/.npmrc` read those variables.

```bash
(cd frontend && npm ci)                    # the frontend's packages
dotnet build NKeeper.Plugins.Desktop.slnx  # builds the frontend, then packs and signs the plugin
```

Building the plugin packs it into `artifacts/plugins/desktop-<version>.nkp`, signed with a development key that the build creates under `.dev/keys/` on first use (git ignores both). To try it on a local NKeeper server in `Development`, point `Plugins:Directory` at `artifacts/plugins` and `Plugins:DevelopmentTrustDirectory` at `.dev/keys`.

The frontend alone, from `frontend/`:

```bash
npm run lint
npm run typecheck
npm run build
```

## Test

Docker is needed: the tests start NKeeper's test server, PostgreSQL, xrdp and guacd in containers.

```bash
dotnet test NKeeper.Plugins.Desktop.slnx
```

The tests start the test server from `NKeeper.Testing` with this plugin's package, built above, loaded and signed with the development key, then open a desktop to a real xrdp server through a real guacd.

### The browser test

`tests/e2e/desktop.mjs` drives a real browser (Playwright, Chromium) on an **installed** NKeeper that has this plugin and guacd. It signs in, creates a host for an RDP server with a saved credential, opens its desktop, trusts the server's certificate in core's dialog, checks that the desktop draws, then closes it and deletes the host. CI runs it later against an installation; to run it yourself:

```bash
cd tests/e2e
npm install
npx playwright install chromium
BASE=https://localhost ADMIN_PASSWORD=… RDP_ADDRESS=127.0.0.1 RDP_USER=… RDP_PASSWORD=… node desktop.mjs
```

`ADMIN_USER` (default `admin`), `RDP_ADDRESS` (default `127.0.0.1`) and `SCREENSHOTS` (default: that folder) are optional. The script creates only its own host, `e2e-rdp-<random>`, and deletes it at the end.

## CI

`.github/workflows/ci.yml` runs on every push to `main` and every pull request:
- **Frontend:** `npm ci`, lint, typecheck and build.
- **Backend:** restore, format check, build, test, and pack the plugin, signed with a development key made fresh for the run. That package is only for checking: no server trusts that key outside development.

Both restore from GitHub Packages with the workflow's own token.

## Release

Releases are signed with NKeeper's official key, and only through `.github/workflows/release.yml`. To release a version:

1. Set `version` in `backend/nkeeper-plugin.json` (and `frontend/package.json`), and merge it to `main`.
2. Tag the commit `v<version>`, for example `v1.0.0`, and push the tag.
3. The workflow refuses a tag that doesn't match the manifest's version, or that isn't on `main`. Then it builds and tests the plugin.
4. The `release` job runs in the `release` environment, which an owner has to approve. It holds `NKEEPER_RELEASE_KEY`, the official key's private half, as a PEM. The job writes the key to a private temporary file for the packing step alone, never prints it, and deletes it at the end of the job.
5. It packs `desktop-<version>.nkp`, signs it, verifies it, and publishes a GitHub release with the package and its SHA-256 (also as `desktop-<version>.nkp.sha256`).

NKeeper's installer picks up a release when its `deploy/plugins.lock` is updated with that release's URL and SHA-256.

## License

MIT, as NKeeper: see [LICENSE](LICENSE).
