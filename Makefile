APP_NAME := PHPost
PROJECT := src/PHPost/PHPost.csproj
VERSION ?= 1.0.0
RID ?= win-x64
R2R ?= false

DIST_DIR := dist
STANDALONE_DIR := $(DIST_DIR)/$(RID)-standalone
PORTABLE_DIR := $(DIST_DIR)/$(RID)-portable
STANDALONE_ZIP := $(DIST_DIR)/$(APP_NAME)-$(VERSION)-$(RID)-standalone.zip
PORTABLE_ZIP := $(DIST_DIR)/$(APP_NAME)-$(VERSION)-$(RID)-portable.zip

PUBLISH_COMMON := -c Release --self-contained true -p:DebugType=none -p:DebugSymbols=false -p:Version=$(VERSION) -p:PublishReadyToRun=$(R2R)
SINGLE_FILE_FLAGS := -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true

ifeq ($(OS),Windows_NT)
  PWSH := powershell -NoProfile -ExecutionPolicy Bypass -Command
  ECHO_EMPTY := cmd /c echo.
  RM_DIR = $(PWSH) "if (Test-Path '$(1)') { Remove-Item -Recurse -Force '$(1)' }"
  ZIP_DIR = $(PWSH) "New-Item -ItemType Directory -Force -Path '$(DIST_DIR)' | Out-Null; if (Test-Path '$(2)') { Remove-Item -Force '$(2)' }; Compress-Archive -Path '$(1)/*' -DestinationPath '$(2)' -CompressionLevel Optimal"
else
  ECHO_EMPTY := echo ""
  RM_DIR = rm -rf $(1)
  ZIP_DIR = mkdir -p $(DIST_DIR) && rm -f $(2) && (cd $(1) && zip -r ../../$(2) .)
endif

.DEFAULT_GOAL := standalone

.PHONY: all help restore build run standalone portable release release-portable release-arm64 release-all clean

all: standalone

help:
	@echo PHPost - Comandos de compilacao (Makefile)
	@$(ECHO_EMPTY)
	@echo Desenvolvimento:
	@echo   make restore           Restaura pacotes NuGet
	@echo   make build             Compila em modo Debug
	@echo   make run               Executa o PHPost em modo desenvolvimento
	@echo   make clean             Remove bin/, obj/ e dist/
	@$(ECHO_EMPTY)
	@echo Compilacao Self-Contained (NAO exige .NET instalado no usuario):
	@echo   make standalone        Gera executavel unico PHPost.exe em dist/$(RID)-standalone/
	@echo   make portable          Gera pasta portatil self-contained em dist/$(RID)-portable/
	@echo   make release           Gera executavel unico + .zip pronto para GitHub Releases
	@echo   make release-portable  Gera versao portatil + .zip pronto para GitHub Releases
	@echo   make release-arm64     Gera executavel unico + .zip para Windows ARM64
	@echo   make release-all       Gera standalone + portatil com .zip para win-x64
	@$(ECHO_EMPTY)
	@echo Variaveis opcionais:
	@echo   VERSION=1.2.0          Define a versao do executavel e do .zip (padrao: $(VERSION))
	@echo   RID=win-x64            Define o Runtime Identifier alvo (padrao: $(RID))
	@echo   R2R=true               Ativa ReadyToRun para inicializacao mais rapida (padrao: $(R2R))

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
	@echo Concluido: $(STANDALONE_DIR)/$(APP_NAME).exe

portable:
	@echo Compilando $(APP_NAME) v$(VERSION) [$(RID) portatil self-contained, sem necessidade de .NET]...
	@$(call RM_DIR,$(PORTABLE_DIR))
	dotnet publish $(PROJECT) -r $(RID) $(PUBLISH_COMMON) -p:PublishSingleFile=false -o $(PORTABLE_DIR)
	@echo Concluido: $(PORTABLE_DIR)/

release: standalone
	@echo Empacotando $(STANDALONE_ZIP)...
	@$(call ZIP_DIR,$(STANDALONE_DIR),$(STANDALONE_ZIP))
	@echo Pacote gerado: $(STANDALONE_ZIP)

release-portable: portable
	@echo Empacotando $(PORTABLE_ZIP)...
	@$(call ZIP_DIR,$(PORTABLE_DIR),$(PORTABLE_ZIP))
	@echo Pacote gerado: $(PORTABLE_ZIP)

release-arm64:
	@echo Compilando $(APP_NAME) v$(VERSION) [win-arm64 standalone single-file]...
	@$(call RM_DIR,$(DIST_DIR)/win-arm64-standalone)
	dotnet publish $(PROJECT) -r win-arm64 $(PUBLISH_COMMON) $(SINGLE_FILE_FLAGS) -o $(DIST_DIR)/win-arm64-standalone
	@echo Empacotando $(DIST_DIR)/$(APP_NAME)-$(VERSION)-win-arm64-standalone.zip...
	@$(call ZIP_DIR,$(DIST_DIR)/win-arm64-standalone,$(DIST_DIR)/$(APP_NAME)-$(VERSION)-win-arm64-standalone.zip)
	@echo Pacote gerado: $(DIST_DIR)/$(APP_NAME)-$(VERSION)-win-arm64-standalone.zip

release-all: release release-portable
	@echo Todos os pacotes de release foram gerados em $(DIST_DIR)/

clean:
	@echo Limpando artefatos de compilacao...
	dotnet clean $(PROJECT)
	@$(call RM_DIR,src/PHPost/bin)
	@$(call RM_DIR,src/PHPost/obj)
	@$(call RM_DIR,$(DIST_DIR))
	@echo Limpeza concluida.
