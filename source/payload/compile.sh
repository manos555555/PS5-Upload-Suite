#!/bin/bash

echo "================================================"
echo "PS5 Upload Server - Compilation"
echo "By Manos"
echo "================================================"
echo ""

echo "[+] Compiling..."
rm -f ps5_upload_server.elf
/opt/ps5-payload-sdk/bin/prospero-clang -Wall -O3 -pthread -o ps5_upload_server.elf main.c

if [ -f "ps5_upload_server.elf" ]; then
    echo ""
    echo "================================================"
    echo "[+] SUCCESS! Compiled ps5_upload_server.elf"
    echo "================================================"
    echo ""
    ls -lh ps5_upload_server.elf
    file ps5_upload_server.elf
    echo ""
    echo "Upload to PS5 and run with elfldr"
    echo "Server will listen on port 9113"
else
    echo ""
    echo "================================================"
    echo "[-] COMPILATION FAILED"
    echo "================================================"
    exit 1
fi
