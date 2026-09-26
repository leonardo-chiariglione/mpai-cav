# Installing MPAI-MAS on a Linux server

MPAI as a Service - MAS-App and the four Apps it offers (MAD, AMQ, MAT, MPD), with
their AIMs and models - runs on Linux. Installed as described here, the server holds
all of it: the **MAS Service** (the Apps' Modules, their AIMs, the models, Ollama)
and the **browser client's host** (the page people open). A person needs only a
browser: `https://<server>/`.

Tested on 2026/09/26 by following these steps in WSL (Ubuntu 26.04, .NET 10.0.12,
x86-64) into a folder of its own: the four Apps answered as on Windows - MAD "The
capital of France is Paris.", AMQ "red", MAT "Buongiorno, come stai tu?", MPD
"That's wonderful to hear!" - and the browser client, served by the installed host,
offered the four Apps. What was not tested is listed at the end.

## What the server holds

Everything under one folder, `/opt/mpai` here (any other works: give it to
`package.sh`):

| Folder | What | Size |
|---|---|---|
| `service/` | the MAS Service, published for linux-x64 | 29 MB |
| `client/` | the browser client's host, with the client (WebAssembly) | 64 MB |
| `Apps/`, `AMDs/`, `schemas/` | the four Apps, the L3s of the AIMs, the schemas | 2 MB |
| `UserAgent/` | the avatar and the client's workflow | 6 MB |
| `Models/` | the model files the Apps use - only those | 6.8 GB |
| `bin/` | Piper 1.2.0 (TTS), `whisper-cli` of whisper.cpp 1.9.3 (ASR) | 55 MB |
| `ollama/`, `ollama-models/` | Ollama 0.34.4, and llama3.2:3b (EDP, in MAD and MPD) | 4 GB |
| `tls/` | the certificate of the client's host | - |
| `mas-server.json`, `aim-settings.json` | the Service's configuration and the AIMs' settings | - |
| `systemd/` | the units that run it | - |

About 11 GB of disk. Memory: 16 GB is comfortable. A GPU is optional: Ollama uses an
NVIDIA one if its driver is installed (a GTX 1050 Ti was used through Ollama's CUDA 12
libraries); everything else runs on the CPU.

The Service listens on the server's loopback only (`127.0.0.1:5005`): the network
reaches the client's host, over HTTPS, and the host passes the client's requests on.
No bearer token is then needed.

## 1. On the build machine: the package

With the repository, its `Models` folder and the .NET 10 SDK (Git Bash on Windows, or
Linux), from the repository root:

```
MAS/Service/linux/package.sh ~/mpai-package /opt/mpai
```

It publishes the Service and the client's host for linux-x64, and copies the Apps,
the L3s, the schemas, the avatar and - from `Models` - the files `aim-settings.json`
names, into `~/mpai-package`, laid out for `/opt/mpai`.

## 2. On the server: the system, and the files

Ubuntu 26.04 (as tested; on another release or distribution, install the ASP.NET Core 10 runtime as Microsoft documents it), as root:

```
apt-get install -y aspnetcore-runtime-10.0 espeak-ng zstd g++ cmake curl openssl
useradd --system --create-home --home-dir /opt/mpai mpai
```

`g++` and `cmake` build whisper.cpp; `espeak-ng` gives GFD the phonemes of the face;
`zstd` unpacks Ollama. Then copy the package (from the build machine):

```
rsync -a ~/mpai-package/ <server>:/opt/mpai/
```

and, on the server, make it the service user's: `chown -R mpai:mpai /opt/mpai`.

## 3. On the server: the programs

As the service user, in `/opt/mpai`:

```
sudo -u mpai /opt/mpai/setup.sh
```

It downloads Piper (26 MB), the whisper.cpp source (9 MB) - built here into one
file, `bin/whisper-cli` - and Ollama (1.4 GB), pulls llama3.2:3b (2 GB), writes the
SHA-256 of `whisper-cli` into `aim-settings.json`, and makes a self-signed certificate
in `tls/`. Anything already in place is left as it is; run it again after a failure.

## 4. The certificate

Browsers warn about the self-signed certificate. For people to use the server, put
in its place one issued for the server's name - `tls/cert.pem` and `tls/key.pem`, PEM,
the key unencrypted and readable by `mpai` only.

## 5. The services

As root:

```
cp /opt/mpai/systemd/*.service /etc/systemd/system/
systemctl daemon-reload
systemctl enable --now mpai-ollama mpai-service mpai-client
```

- `mpai-ollama`: Ollama, on `127.0.0.1:11434`;
- `mpai-service`: the MAS Service, on `127.0.0.1:5005` - it loads the models first,
  up to a minute;
- `mpai-client`: the client's host, on `https://0.0.0.0:443` (the unit gives it the
  right to bind 443 without root).

Open port 443 in the server's firewall. Logs: `journalctl -u mpai-service` (and the
others).

## 6. Checking

On the server:

```
curl -s http://127.0.0.1:5005/MPAI/AIFU/Apps        # the Service: the four Apps
curl -sk https://localhost/MPAI/AIFU/Apps           # the same, through the client's host
```

Then from a browser: `https://<server>/`, Start, choose an App. The browser asks for
the microphone; AMQ also asks for a picture or the camera.

## The two files

`mas-server.json` - the Service:

| Setting | Here |
|---|---|
| `ListenUrl` | `http://127.0.0.1:5005/` - loopback. A Service reachable beyond loopback does not start without a `BearerToken`. |
| `AppDirectory`, `Apps` | the four Apps |
| `AmdDirectory` | the L3s |
| `SettingsPath` | `aim-settings.json` |
| `SchemaDirectory` | the schemas: without it, a Service installed outside the repository validates nothing |

`aim-settings.json` - the AIMs: every path absolute, with `/`; `OllamaUrl` of
`1MMC-EDP-V2.5-I01` where Ollama is (`http://127.0.0.1:11434`); `SHA256:ExecutablePath`
of `1MMC-ASR-V2.5-I01` written by `setup.sh`.

## Updating

Run `package.sh` again on the build machine, copy `service/` and `client/` (and
whatever else changed) to the server, and `systemctl restart mpai-service mpai-client`.

## Other ways

- **The Service here, the client elsewhere** - a desktop client, or a client's host on
  another machine: the Service must listen beyond loopback, so it needs a
  `BearerToken`, and the client must send it. The desktop client's API
  (`RemoteControllerApi`) can; the browser client's host today passes on only what the
  browser sends, and the browser sends none - it cannot yet be used this way.
- **Ollama elsewhere**: set `OllamaUrl`, and leave out `mpai-ollama`.
- **In WSL** (as tested): the same, in a folder of the WSL user; WSL forwards the ports
  its programs listen on to Windows' `localhost`. A Windows Ollama is not reachable
  from WSL's default networking (and mirrored networking, which would share it, needs
  Windows 11): Ollama runs in WSL, on another port than a Windows one (e.g. 11435, in
  `OllamaUrl`).

## Not tested

- The systemd units running as services: they were checked with `systemd-analyze
  verify`, and the three programs run with exactly their command lines - but not
  under systemd, nor on port 443 (5443 was used), nor as a `mpai` user.
- A server of its own, a certificate browsers trust, and a firewall.
- Other distributions than Ubuntu, and ARM.
