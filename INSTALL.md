# Installing Kickl Browser

Kickl Browser is a C# desktop application (.NET 10, Avalonia). You can run a prebuilt Windows executable or build
from source. The registry server, the CA and the BTC shop are **optional**: the browser works on its own.

| You want to… | Go to |
|---|---|
| Just run the browser | [1](#1-quick-start-windows) or [2](#2-build-from-source) |
| Try it with sample sites | [3](#3-first-run) |
| Run a registry server (the shared phonebook) | [6](#6-optional-the-registry-server) |
| Run the CA and let people claim names | [7](#7-optional-claims-and-the-certificate-authority) |
| Write a game | [8](#8-games) |

---

## 1. Quick start (Windows)

1. Download `KicklBrowser-windows-x64.exe` from the project's download page (`KICKLESpace/download/`).
   The page lists its SHA-256; compare it before you run the file:
   ```powershell
   Get-FileHash .\KicklBrowser-windows-x64.exe -Algorithm SHA256
   ```
2. Double-click it. No installer, no admin rights. Your data lives in `%LOCALAPPDATA%\KicklBrowser`.

> The prebuilt executable is a snapshot. For the newest features (site claims, the CA, WebAssembly games, sealed
> page content) build from source below.

## 2. Build from source

**Requirements**

- The [.NET 10 SDK](https://dotnet.microsoft.com/download) (`dotnet --version` should print `10.0.x`).
- Git.
- Windows 10/11 for the full desktop app. Linux and macOS: the libraries, tools and tests build and pass on Ubuntu in
  CI, but the browser window itself has **only been run on Windows**. Treat it as untested elsewhere.

```bash
git clone https://github.com/JJustis/Kickl---Encryption-Standardized-Website-Browser.git
cd Kickl---Encryption-Standardized-Website-Browser

dotnet restore Kickl.slnx --locked-mode      # exact package versions, as CI does
dotnet build   Kickl.slnx -c Release
```

Run it:

```bash
# Windows: builds and starts the browser with a data folder inside the repo
run.bat

# any platform
dotnet run --project src/Kickl.Browser -c Release
```

The first build downloads packages, including the Wasmtime runtime (about 21 MB of native library) that sandboxes
games. Warnings are treated as errors, so a clean build prints none.

To make your own single-file Windows executable:

```bash
dotnet publish src/Kickl.Browser -c Release -r win-x64 --self-contained -p:PublishSingleFile=true
```

(A publish with a runtime identifier regenerates `packages.lock.json`; do not commit that change.)

## 3. First run

**Your own profile.** Start the browser, create a local profile and choose a **user key** (a PIN or passphrase).

- It unlocks everything: your master key is wrapped by an Argon2id key derived from it.
- **There is no reset.** If you lose it, the profile cannot be recovered. Use 8 or more characters.
- Each local profile has its own key and its own folder. Profile names are visible in folder names.

**Sample sites.** To explore without setting anything up, start the demo profile:

```bash
dotnet run --project src/Kickl.Browser -c Release -- --demo --data .kickl-demo
```

Sign in with the fixed key **`1234`**. Use the demo for experiments only, never for personal data. Then try:

| Address | What it shows |
|---|---|
| `jttp://pinwheelexplained.com` | a sealed page; unlock a blob with your key |
| `jttp://svg-lab.custom` | inline SVG |
| `jttp://sealed-lab.custom` | click-to-unlock sealed images |
| `jttp://game-lab.custom` | a playable WebAssembly game |
| `jttp://caverns.custom` | Crystal Caverns, a first-person 3D game (the showpiece of what a sandboxed game can do) |

**Games.** The arcade on the home page lists every game in the `games/` folder (Crystal Caverns, Coin Dash, Plasma, Hello). To add one, put a folder with a
`game.wasm` in `games/`, or in a `games` folder inside your data folder. `kickl games` lists what is found. See `games/README.md`.
The arcade also lists games other people have shared, as dimmed DOWNLOAD cards; clicking one asks before it downloads the game from the publisher or another computer.
**SHARING: ON** (the default) means this computer hands the games it has to others, over an encrypted connection, on one port; the switch is on the arcade.
| `jttp://btcshop.com` | an example Bitcoin shop (testnet); the demo access key for its sealed file is `demo-paid` |

**Where your data goes.** `--data DIR` picks the folder; the default is `%LOCALAPPDATA%\KicklBrowser`
(`~/.local/share/KicklBrowser` on Linux). `run.bat` uses `.kickl-mail-ui` in the repo, which Git ignores. To remove
everything, delete that folder. That destroys your keys and your data with them.

## 4. The command-line tool

```bash
dotnet run --project src/Kickl.Cli -- init --data mydata        # create a profile
dotnet run --project src/Kickl.Cli -- publish --site sites/pinwheelexplained.com --pin 1234 --data mydata
dotnet run --project src/Kickl.Cli -- open jttp://pinwheelexplained.com/ --pin 1234 --data mydata
dotnet run --project src/Kickl.Cli -- help
```

## 5. Run the tests

```bash
dotnet test tests/Kickl.Core.Tests -c Release
```

About 390 tests; allow a few minutes (the key-derivation tests are deliberately slow).

---

## 6. Optional: the registry server

The registry is the shared phonebook: signed snapshots that browsers verify before trusting. Without it, the browser
uses a local registry and everything still works. It needs PHP 8.2+ (with `pdo_mysql`, `json`, `openssl`) and
MySQL 8 or MariaDB 10.4+. [XAMPP](https://www.apachefriends.org/) provides both on Windows.

```bash
# 1. build the offline signer and create the registry key
dotnet build tools/Kickl.RegistrySigner -c Release
dotnet tools/Kickl.RegistrySigner/bin/Release/net10.0/Kickl.RegistrySigner.dll keygen --dir server/keys

# 2. configure (server/.env is ignored by Git)
cp server/.env.example server/.env        # then set KICKL_ADMIN_TOKEN to a long random value

# 3. create the database (start MySQL first) by applying the migrations in order
mysql -u root -e "CREATE DATABASE IF NOT EXISTS kickl_registry"
mysql -u root kickl_registry < server/migrations/001_init.sql
mysql -u root kickl_registry < server/migrations/002_mail_identity.sql

# 4. serve it
php -S 127.0.0.1:8099 -t server/public server/public/index.php
```

Point a browser at it:

```bash
dotnet src/Kickl.Cli/bin/Release/net10.0/Kickl.Cli.dll registry sync \
  --url http://127.0.0.1:8099 --key server/keys/registry-key.json --data mydata
```

The first sync pins the registry key's fingerprint. Details, Apache deployment and the endpoint list are in
[`server/README.md`](server/README.md). **Registration is operator-only in this slice. Do not expose it to the
internet as it is.**

## 7. Optional: claims and the certificate authority

Claiming a name costs Bitcoin at a shop, and a CA countersigns the receipt so visitors see **VERIFIED OWNER**. The
**BTC shop itself is not included in this repository**: it is your own site. The CA, its website and the browser
side are. Full design and operations: [`docs/23-certificate-authority.md`](docs/23-certificate-authority.md).

**What you need:** the registry server's PHP environment above, a web server (Apache) and `dotnet` on the PATH of the
user that runs it.

```bash
# build the CA tool
dotnet build tools/Kickl.Ca -c Release
CA="dotnet tools/Kickl.Ca/bin/Release/net10.0/Kickl.Ca.dll"

# 1. OFFLINE machine: create the root. Keep these private files off the server.
$CA root-keygen --dir /safe/offline/ca-root

# 2. online machine: create the issuing key; copy issuer-public.json to the offline machine
$CA issuer-keygen --dir server/keys/ca/issuer

# 3. OFFLINE machine: certify the issuing key for 90 days; copy the result back
$CA issue-issuer --root-dir /safe/offline/ca-root \
   --issuer-public server/keys/ca/issuer/issuer-public.json \
   --out server/keys/ca/issuer-cert.json --days 90

# 4. trust anchors
cp /safe/offline/ca-root/root-public.json server/keys/ca/root-public.json
cp /safe/offline/ca-root/root-public.json src/Kickl.Core/Trust/ca-root.json   # built into the browser: rebuild after this
echo "<your shop's public key, base64>" > server/keys/ca/shop-keys.txt         # the shop keys the CA will seal receipts from
```

Then publish the CA website: copy `server/secca/` to your web root (for example `htdocs/secca`) and set
`KICKL_APP_ROOT` to the repository's `server/` folder (`SetEnv` in `.htaccess` or the host panel). Open it in a
browser; it should show the root fingerprint and an empty log.

**Renew the issuing certificate before it expires** (90 days). Never put the root's private files on the server.

Point the browser at your shop and CA with environment variables:

| Variable | Meaning | Default |
|---|---|---|
| `KICKL_SHOP_URL` | where CLAIM SITE sends people | `https://secupgrade.com/btcshop/` |

The CA is not configurable: **`https://secupgrade.com/secca/` is the only CA**, fixed in the build and reached only over https.

> The development CA root committed in `src/Kickl.Core/Trust/ca-root.json` was generated while building this
> project. **Replace it with your own before you rely on any seal**, then rebuild.

## 8. Games

Nothing to install: the runtime ships with the browser. To write one, compile a WebAssembly module that follows
[`docs/22-wasm-games.md`](docs/22-wasm-games.md), put it in a site's `blobs/` folder and add a `<game>` element.
`sites/game-lab.custom/src/catcher.wat` is a complete example; build it with WABT:

```bash
wat2wasm sites/game-lab.custom/src/catcher.wat -o sites/game-lab.custom/blobs/catcher.wasm
```

---

## Settings reference

| Variable / option | Purpose |
|---|---|
| `--data DIR` | data folder for the browser and CLI |
| `--demo` | demo profile with the sample sites (key `1234`) |
| `--open URL` | open an address at start |
| `KICKL_SHOP_URL` | the BTC shop address (the CA is fixed to secupgrade.com) |
| `KICKL_APP_ROOT` | the `server/` folder, when `server/public` or `server/secca` is deployed on its own |
| `KICKL_CA_DIR` | a separate CA data folder (used for test CAs; leave unset in production) |

## Troubleshooting

- **`file is locked by Kickl.Browser` during a build.** The browser is still running. Close it and build again.
- **`NU1004` / "lock file is inconsistent".** Restore with `dotnet restore Kickl.slnx --locked-mode` and do not
  pass a runtime identifier. If you changed packages, run `dotnet restore Kickl.slnx --force-evaluate` and commit the
  updated `packages.lock.json` files.
- **A game says "the game asks the host for …".** Your toolchain emitted an import. Games may import nothing.
- **`git push` fails with an SSL certificate error on Windows.** Use the system store for that command:
  `git -c http.sslBackend=schannel push`. Do not turn certificate checking off.
- **CAs and shops.** The browser trusts only the CA root built into it. A seal from any other root shows no badge.

## Security notes

- Your user key unlocks the master key; there is no recovery.
- A `.kcert` ownership certificate bought **without** an owner passphrase is a bearer credential: whoever holds it can edit that name. Type an owner passphrase on the shop page when you order and the file alone is no longer enough. Copies the browser saves are passphrase-locked; keep the passphrase safe, there is no recovery.
- Direct mail listens on a TCP port on your machine (TLS 1.3, pinned to your own key). Allow it through your firewall only if you want mail from other computers; it is never open to plain http.
- FreeChat shows each member's key fingerprint and warns when a known member's key changes; compare fingerprints with the person for anything sensitive.
- The registry server's operator registration and the demo key `1234` are for development only.
- See [`docs/20-encryption-map.md`](docs/20-encryption-map.md) for exactly what is and is not encrypted.
