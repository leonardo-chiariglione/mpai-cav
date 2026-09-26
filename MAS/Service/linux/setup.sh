#!/bin/bash
# SET UP MPAI-MAS ON A LINUX SERVER, in the folder package.sh made, once it is in
# its install root. Run as the user that will run the Services (not root):
#
#   ./setup.sh
#
# It checks the system's packages, then puts in <root>/bin the programs the AIMs call
# - Piper 1.2.0, whisper.cpp 1.9.3 built here - installs Ollama with llama3.2:3b in
# <root>/ollama, writes the SHA-256 of whisper-cli into aim-settings.json, and makes a
# self-signed certificate in <root>/tls if none is there. What is already in place is
# left as it is.
set -e
root="$(cd "$(dirname "$0")" && pwd)"
cd "$root"

echo "== the system's packages"
missing=""
for c in espeak-ng zstd g++ cmake curl openssl; do command -v "$c" > /dev/null || missing="$missing $c"; done
dotnet --list-runtimes 2> /dev/null | grep -q "Microsoft.AspNetCore.App 10\." || missing="$missing aspnetcore-runtime-10.0"
if [ -n "$missing" ]; then
    echo "Missing:$missing. As root (Ubuntu):"
    echo "  apt-get install -y espeak-ng zstd g++ cmake curl openssl aspnetcore-runtime-10.0"
    exit 1
fi

mkdir -p bin src
echo "== Piper 1.2.0 (TTS)"
if [ ! -x bin/piper/piper ]; then
    [ -f src/piper_linux_x86_64.tar.gz ] || curl -sSL -o src/piper_linux_x86_64.tar.gz https://github.com/rhasspy/piper/releases/download/2023.11.14-2/piper_linux_x86_64.tar.gz
    tar xzf src/piper_linux_x86_64.tar.gz -C bin
fi
bin/piper/piper --version

echo "== whisper.cpp 1.9.3 (ASR), built here, one file"
if [ ! -x bin/whisper-cli ]; then
    [ -f src/whisper.cpp-v1.9.3.tar.gz ] || curl -sSL -o src/whisper.cpp-v1.9.3.tar.gz https://codeload.github.com/ggml-org/whisper.cpp/tar.gz/refs/tags/v1.9.3
    tar xzf src/whisper.cpp-v1.9.3.tar.gz -C src
    (cd src/whisper.cpp-1.9.3 && cmake -B build -DCMAKE_BUILD_TYPE=Release -DBUILD_SHARED_LIBS=OFF -DWHISPER_BUILD_TESTS=OFF > /dev/null \
        && cmake --build build -j"$(nproc)" --target whisper-cli > /dev/null)
    cp src/whisper.cpp-1.9.3/build/bin/whisper-cli bin/whisper-cli
fi
hash=$(sha256sum bin/whisper-cli | cut -d' ' -f1 | tr a-f A-F)
sed -i "s#\"SHA256:ExecutablePath\": \"[^\"]*\"#\"SHA256:ExecutablePath\": \"$hash\"#" aim-settings.json
echo "whisper-cli $hash, written into aim-settings.json"

echo "== Ollama 0.34.4 and llama3.2:3b (EDP, in MAD and MPD)"
if [ ! -x ollama/bin/ollama ]; then
    [ -f src/ollama-linux-amd64.tar.zst ] || curl -sSL -o src/ollama-linux-amd64.tar.zst https://github.com/ollama/ollama/releases/download/v0.34.4/ollama-linux-amd64.tar.zst
    mkdir -p ollama && tar --zstd -xf src/ollama-linux-amd64.tar.zst -C ollama
fi
url=$(grep -o '"OllamaUrl": "[^"]*"' aim-settings.json | cut -d'"' -f4)
export OLLAMA_HOST="${url#http://}" OLLAMA_MODELS="$root/ollama-models"
started=""
if ! curl -s --max-time 2 "$url/api/tags" > /dev/null; then
    ollama/bin/ollama serve > /dev/null 2>&1 & started=$!
    for i in $(seq 1 30); do curl -s --max-time 2 "$url/api/tags" > /dev/null && break; sleep 1; done
fi
ollama/bin/ollama list | grep -q "llama3.2:3b" || ollama/bin/ollama pull llama3.2:3b
ollama/bin/ollama list | grep "llama3.2:3b"
[ -n "$started" ] && kill "$started"

echo "== the certificate of the browser client's host"
if [ ! -f tls/cert.pem ]; then
    mkdir -p tls
    openssl req -x509 -newkey ec -pkeyopt ec_paramgen_curve:P-256 -nodes -keyout tls/key.pem -out tls/cert.pem -days 365 \
        -subj "/CN=$(hostname -f)" -addext "subjectAltName=DNS:$(hostname -f),DNS:localhost" 2> /dev/null
    echo "A self-signed certificate for $(hostname -f): replace tls/cert.pem and tls/key.pem with one browsers trust."
fi
echo "Set up in $root. Next: the systemd units (README.md)."
