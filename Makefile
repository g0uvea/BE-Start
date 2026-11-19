APP_NAME := PHPost
PROJECT := src/PHPost/PHPost.csproj
VERSION ?= 1.1.0
R2R ?= false

ifeq ($(OS),Windows_NT)
  DEFAULT_RID := win-x64
  PWSH := powershell -NoProfile -ExecutionPolicy Bypass -Command
  ECHO_EMPTY := cmd /c echo.
  RM_DIR = $(PWSH) "if (Test-Path '$(1)') { Remove-Item -Recurse -Force '$(1)' }"
  ZIP_DIR = $(PWSH) "New-Item -ItemType Directory -Force -Path '$(DIST_DIR)' | Out-Null; if (Test-Path '$(2)') { Remove-Item -Force '$(2)' }; Compress-Archive -Path '$(1)/*' -DestinationPath '$(2)' -CompressionLevel Optimal"
else
  UNAME_S := $(shell uname -s)
  UNAME_M := $(shell uname -m)
  ifeq ($(UNAME_S),Darwin)
    ifeq ($(UNAME_M),arm64)
      DEFAULT_RID := osx-arm64
    else
      DEFAULT_RID := osx-x64
    endif
  else
    ifeq ($(UNAME_M),aarch64)
      DEFAULT_RID := linux-arm64
    else ifeq ($(UNAME_M),arm64)
      DEFAULT_RID := linux-arm64
    else
      DEFAULT_RID := linux-x64
    endif
  endif
  ECHO_EMPTY := echo ""
  RM_DIR = rm -rf $(1)
  ZIP_DIR = mkdir -p $(DIST_DIR) && rm -f $(2) && (cd $(1) && zip -r ../../$(2) .)
endif

RID ?= $(DEFAULT_RID)

DIST_DIR := dist
STANDALONE_DIR := $(DIST_DIR)/$(RID)-standalone
PORTABLE_DIR := $(DIST_DIR)/$(RID)-portable
STANDALONE_ZIP := $(DIST_DIR)/$(APP_NAME)-$(VERSION)-$(RID)-standalone.zip
PORTABLE_ZIP := $(DIST_DIR)/$(APP_NAME)-$(VERSION)-$(RID)-portable.zip

PUBLISH_COMMON := -c Release --self-contained true -p:DebugType=none -p:DebugSymbols=false -p:Version=$(VERSION) -p:PublishReadyToRun=$(R2R)
SINGLE_FILE_FLAGS := -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true

.DEFAULT_GOAL := standalone

.PHONY: all help restore build run standalone portable release release-portable \
        release-win-x64 release-win-arm64 release-linux-x64 release-linux-arm64 \
        release-osx-arm64 release-osx-x64 release-arm64 release-all release-all-platforms clean

all: standalone

help:
	@echo PHPost - Comandos de compilacao multiplataforma (Makefile)
	@$(ECHO_EMPTY)
	@echo Desenvolvimento:
	@echo   make restore               Restaura pacotes NuGet
	@echo   make build                 Compila em modo Debug
	@echo   make run                   Executa o PHPost em modo desenvolvimento
	@echo   make clean                 Remove bin/, obj/ e dist/
	@$(ECHO_EMPTY)
	@echo Compilacao Self-Contained (NAO exige .NET instalado no usuario):
	@echo   make standalone            Gera executavel unico em dist/$(RID)-standalone/
	@echo   make portable              Gera pasta portatil self-contained em dist/$(RID)-portable/
	@echo   make release               Gera executavel unico + .zip para o RID atual ($(RID))
	@echo   make release-portable      Gera versao portatil + .zip para o RID atual ($(RID))
	@$(ECHO_EMPTY)
	@echo Releases por Plataforma:
	@echo   make release-win-x64       Windows x64 (.zip)
	@echo   make release-win-arm64     Windows ARM64 (.zip)
	@echo   make release-linux-x64     Linux x64 (.zip)
	@echo   make release-linux-arm64   Linux ARM64 (.zip)
	@echo   make release-osx-arm64     macOS Apple Silicon (.zip)
	@echo   make release-osx-x64       macOS Intel (.zip)
	@echo   make release-all           Gera standalone + portatil para $(RID)
	@echo   make release-all-platforms Gera releases para Windows, Linux e macOS
	@$(ECHO_EMPTY)
	@echo Variaveis opcionais:
	@echo   VERSION=1.1.0              Define a versao do executavel e do .zip (padrao: $(VERSION))
	@echo   RID=win-x64                Runtime Identifier alvo (padrao detectado: $(DEFAULT_RID))
	@echo   R2R=true                   Ativa ReadyToRun para inicializacao mais rapida (padrao: $(R2R))

restore:
	dotnet restore $(PROJECT)

build:
	dotnet build $(PROJECT) -c Debug

run:
	dotnet run --project $(PROJECT)

standalone:
	@echo Compilando $(APP_NAME) v$(VERSION) [$(RID) standalone single-file, sem necessidade de .NET]...
	@$(call RM_DIR,$(STANDALONE_DIR))
	dotnet publish $(PROJECT) -r $(RID) $(PUBLISH_COMMON) $(SINGLE_FILE_FLAGS) -o $(STANDALONE_DIR)
	@echo Concluido em $(STANDALONE_DIR)/

portable:
	@echo Compilando $(APP_NAME) v$(VERSION) [$(RID) portatil self-contained, sem necessidade de .NET]...
	@$(call RM_DIR,$(PORTABLE_DIR))
	dotnet publish $(PROJECT) -r $(RID) $(PUBLISH_COMMON) -p:PublishSingleFile=false -o $(PORTABLE_DIR)
	@echo Concluido em $(PORTABLE_DIR)/

release: standalone
	@echo Empacotando $(STANDALONE_ZIP)...
	@$(call ZIP_DIR,$(STANDALONE_DIR),$(STANDALONE_ZIP))
	@echo Pacote gerado: $(STANDALONE_ZIP)

release-portable: portable
	@echo Empacotando $(PORTABLE_ZIP)...
	@$(call ZIP_DIR,$(PORTABLE_DIR),$(PORTABLE_ZIP))
	@echo Pacote gerado: $(PORTABLE_ZIP)

release-win-x64:
	$(MAKE) release RID=win-x64 VERSION=$(VERSION) R2R=$(R2R)

release-win-arm64:
	$(MAKE) release RID=win-arm64 VERSION=$(VERSION) R2R=$(R2R)

release-arm64: release-win-arm64

release-linux-x64:
	$(MAKE) release RID=linux-x64 VERSION=$(VERSION) R2R=$(R2R)

release-linux-arm64:
	$(MAKE) release RID=linux-arm64 VERSION=$(VERSION) R2R=$(R2R)

release-osx-arm64:
	$(MAKE) release RID=osx-arm64 VERSION=$(VERSION) R2R=$(R2R)

release-osx-x64:
	$(MAKE) release RID=osx-x64 VERSION=$(VERSION) R2R=$(R2R)

release-all: release release-portable
	@echo Todos os pacotes de release para $(RID) foram gerados em $(DIST_DIR)/

release-all-platforms: release-win-x64 release-linux-x64 release-linux-arm64 release-osx-arm64 release-osx-x64
	@echo Todos os pacotes multiplataforma foram gerados em $(DIST_DIR)/

clean:
	@echo Limpando artefatos de compilacao...
	dotnet clean $(PROJECT)
	@$(call RM_DIR,src/PHPost/bin)
	@$(call RM_DIR,src/PHPost/obj)
	@$(call RM_DIR,$(DIST_DIR))
	@echo Limpeza concluida.
