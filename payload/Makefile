# PS5 Upload Server - Makefile
# Uses the maintained ps5-payload-dev/sdk (2026 release)

PS5_HOST ?= 192.168.0.160
PS5_PORT ?= 9021
PS5_PAYLOAD_SDK := /opt/ps5-payload-sdk

CC  := $(PS5_PAYLOAD_SDK)/bin/prospero-clang
CXX := $(PS5_PAYLOAD_SDK)/bin/prospero-clang++
LD  := $(PS5_PAYLOAD_SDK)/bin/prospero-lld

ELF := ps5_upload_server.elf
CFLAGS := -Wall -O3

all: $(ELF)

$(ELF): main.c
	$(CC) $(CFLAGS) -o $@ $^ -lkernel_sys -lSceSystemService -lSceUserService -lSceAppInstUtil

clean:
	rm -f $(ELF)

.PHONY: all clean
