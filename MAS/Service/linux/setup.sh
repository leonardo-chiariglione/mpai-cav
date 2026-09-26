#!/bin/bash
# THE MAS SERVICE ON LINUX: what it needs beside itself, as it was set up and tested
# in WSL (Ubuntu 26.04, 2026/09/26). Run as the user that will run the Service; the
# one apt command needs root. See README.md.
set -e
HERE=~/mpai-linux
mkdir -p "$HERE" && cd "$HERE"

# The programs the AIMs call, in the versions the Windows Service runs.
#   espeak-ng, for GFD (phonemes for the face):   sudo apt-get install -y espeak-ng
#   whisper.cpp and Piper need the build tools:     sudo apt-get install -y g++ cmake
command -v espeak-ng > /dev/null || { echo "espeak-ng is missing: sudo apt-get install -y espeak-ng"; exit 1; }

# Piper 1.2.0 (release 2023.11.14-2), for TTS.
if [ ! -x piper/piper ]; then
    curl -sSL -o piper_linux_x86_64.tar.gz https://github.com/rhasspy/piper/releases/download/2023.11.14-2/piper_linux_x86_64.tar.gz
    tar xzf piper_linux_x86_64.tar.gz
fi
piper/piper --version

# whisper.cpp 1.9.3, for ASR: built here.
if [ ! -x whisper.cpp-1.9.3/build/bin/whisper-cli ]; then
    curl -sSL -o whisper.cpp-v1.9.3.tar.gz https://codeload.github.com/ggml-org/whisper.cpp/tar.gz/refs/tags/v1.9.3
    tar xzf whisper.cpp-v1.9.3.tar.gz
    (cd whisper.cpp-1.9.3 && cmake -B build -DCMAKE_BUILD_TYPE=Release -DWHISPER_BUILD_TESTS=OFF > /dev/null && cmake --build build -j"$(nproc)" --target whisper-cli > /dev/null)
fi
echo "whisper-cli SHA-256 (for SHA256:ExecutablePath in aim-settings.json):"
sha256sum whisper.cpp-1.9.3/build/bin/whisper-cli | cut -d' ' -f1 | tr a-f A-F

# A certificate for HTTPS, as PEM - self-signed here; a real one in production.
if [ ! -f tls/cert.pem ]; then
    mkdir -p tls
    openssl req -x509 -newkey ec -pkeyopt ec_paramgen_curve:P-256 -nodes -keyout tls/key.pem -out tls/cert.pem -days 365 \
        -subj "/CN=localhost" -addext "subjectAltName=DNS:localhost" 2> /dev/null
fi
echo "Ready in $HERE. Copy the published Service to $HERE/service, and mas-server.json and aim-settings.json here."
