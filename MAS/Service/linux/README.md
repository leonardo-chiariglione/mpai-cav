# The MAS Service on Linux

The MAS Service - the server half of MPAI-MAS, which holds the models and runs the
Modules of the Apps - is plain `net10.0` and runs on Linux unchanged. This was
tested on 2026/09/26 in WSL (Ubuntu 26.04, .NET 10): the same requests to AMQ and
MAT, on the Windows Service and on this one, gave the same answers; HTTPS with a PEM
certificate served; the Service refused to listen beyond loopback without a bearer
token. MAD and MPD were not tried: they need Ollama (see below).

## What it needs

| For | What | Version |
|---|---|---|
| the Service | .NET 10 runtime (ASP.NET Core included) - or publish self-contained | 10 |
| ASR | `whisper-cli` of whisper.cpp, built from source | 1.9.3 |
| TTS | Piper, `piper_linux_x86_64` | 1.2.0 (2023.11.14-2) |
| GFD | `espeak-ng` (apt) | 1.52 |
| ONNX models | ONNX Runtime - its Linux library comes with the Service | 1.27 |
| MAD, MPD (EDP) | Ollama with `llama3.2:3b` | - |
| the models | the `Models` folder, as on Windows (about 15 GB) | - |

`setup.sh` fetches Piper, builds whisper.cpp and makes a PEM certificate.

## Publishing, and running

On Windows (or Linux), from the repository:

```
dotnet publish MAS/Service/src/MasService.csproj -c Release -r linux-x64 --self-contained false -o <folder>
```

Copy `<folder>` to `~/mpai-linux/service`, and this folder's `mas-server.json` and
`aim-settings.json` to `~/mpai-linux`, then:

```
~/mpai-linux/service/MasService ~/mpai-linux/mas-server.json
```

## The two files

`mas-server.json` and `aim-settings.json` here are the ones tested, with the
repository at `/mnt/d/DI` (the Windows checkout, seen from WSL) and the programs
under `/home/mpai/mpai-linux`. On another machine, change:

- every `/mnt/d/DI` to where the repository (for `Apps`, `AIMs/AMDs`, `schemas`) and
  the `Models` folder are;
- `/home/mpai` to the home of the user that runs the Service;
- `SHA256:ExecutablePath` of `1MMC-ASR-V2.5-I01` to the SHA-256 of the `whisper-cli`
  built there (`setup.sh` prints it) - the one here is of the build tested;
- `OllamaUrl` of `1MMC-EDP-V2.5-I01` to where Ollama is, if not on this machine;
- `ListenUrl`: a Service reachable beyond loopback does not start without a
  `BearerToken`.

A path in the settings is used as written: on Linux, with `/`. Relative paths are
resolved from the working directory, so absolute ones are used here.

`SchemaDirectory` names the schemas the port data is validated against. Without it
the Service looks for `schemas` above its own folder - which finds it in the
repository, not in an installation elsewhere, and then validates nothing.

## Ollama

EDP (in MAD and MPD) asks Ollama at `OllamaUrl`, `http://127.0.0.1:11434` if not
set. In WSL with its default (NAT) networking, Windows' `127.0.0.1` is not WSL's:
run Ollama in Linux, or make the Windows one listen beyond loopback and name its
address in `OllamaUrl`.
