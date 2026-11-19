# PHPost

![.NET 10](https://img.shields.io/badge/.NET-10.0-512BD4?logo=dotnet)
![Avalonia UI](https://img.shields.io/badge/Avalonia%20UI-11.3-8B44AC)
![Plataformas](https://img.shields.io/badge/Windows%20%7C%20Linux%20%7C%20macOS-x64%20%7C%20ARM64-0078D6)
![Self-Contained](https://img.shields.io/badge/Build-Self--Contained%20(Sem%20.NET)-2EA44F)

Um gerenciador de ambiente local **leve, portátil e multiplataforma** feito em **C# + Avalonia UI** para baixar, configurar e executar **PHP** e **MariaDB** no **Windows, Linux e macOS** — no estilo do XAMPP, porém mais rápido, limpo e sem exigir privilégios de administrador/root.

---

## Principais Recursos

- **Suporte nativo a Windows, Linux e macOS (x64 & ARM64):**
  - **Windows:** PHP oficial (`windows.php.net`, ZIP x64) + MariaDB Portátil oficial (`downloads.mariadb.org`, ZIP x64) com verificação **SHA-256**.
  - **Linux (x64 / ARM64):** PHP Static CLI completo com extensões embutidas (`dl.static-php.dev`, TAR.GZ) + MariaDB Portátil oficial (`downloads.mariadb.org`, TAR.GZ x64 com SHA-256; ou Homebrew em ARM64).
  - **macOS (Apple Silicon & Intel):** PHP Static CLI completo (`dl.static-php.dev`, TAR.GZ) + MariaDB gerenciado via **Homebrew** (`formulae.brew.sh`) com banco isolado no diretório do PHPost.
- **Instalação Automática em 1 Clique ("Instalar & Iniciar Tudo"):**
  - Baixa e extrai as versões recomendadas de PHP e MariaDB;
  - Gera e configura automaticamente o `php.ini` (UTF-8 sem BOM, ativando extensões essenciais como `pdo_mysql`, `mysqli`, `mbstring`, `curl`, `openssl`, `gd`, `zip`, `intl`, `fileinfo`);
  - Inicializa automaticamente o diretório de dados do MariaDB (`mariadb-install-db`) e gera `my.ini` (Windows) / `my.cnf` (Linux/macOS) em **UTF-8 sem BOM** com suporte a `utf8mb4`;
  - Configura automaticamente o **PATH do usuário** (Registro do Windows, `~/.bashrc`/`~/.profile`/`~/.zshrc`/`fish` no Linux e `~/.zshrc` no macOS) e cria atalhos no Desktop / menu de aplicativos;
  - Cria uma página inicial em `www/index.php` com diagnóstico em tempo real de PHP + conexão MariaDB.
- **Gerenciador de Múltiplas Versões:** instale, troque ou remova versões de PHP e MariaDB facilmente.
- **Criador de Projetos PHP:** crie novos projetos dentro da pasta `www` já com um `index.php` pronto para testar a conexão PDO com MariaDB.
- **Terminal Pré-configurado:** abre PowerShell (Windows), Terminal.app (macOS) ou seu emulador de terminal (Linux) já na pasta `www` com `php` e `mariadb` disponíveis no `PATH`.
- **Console de Logs em Tempo Real:** acompanhe saídas UTF-8 do PHP, MariaDB e do PHPost com filtros por serviço.
- **Encerramento Seguro:** encerra o MariaDB de forma graciosa (`mariadb-admin shutdown`) ao parar o serviço ou fechar o app.

---

## Para Usuários Finais (Sem Precisar Instalar o .NET)

Os executáveis distribuídos do **PHPost** são compilados em modo **Self-Contained** (autocontido), com o runtime .NET embutido:

- **Windows:** baixe `PHPost-<versao>-win-x64-standalone.zip`, extraia e abra **`PHPost.exe`**.
- **Linux:** baixe `PHPost-<versao>-linux-x64-standalone.zip` (ou `linux-arm64`), extraia, dê permissão de execução (`chmod +x PHPost`) e execute `./PHPost`.
- **macOS:** baixe `PHPost-<versao>-osx-arm64-standalone.zip` (Apple Silicon) ou `osx-x64` (Intel), extraia, dê permissão (`chmod +x PHPost`) e execute `./PHPost` (para MariaDB no macOS, tenha o [Homebrew](https://brew.sh) instalado).

Depois basta clicar em **"Instalar & Iniciar Tudo"** para configurar PHP e MariaDB automaticamente.

---

## Como Compilar (Desenvolvedores)

### Pré-requisitos

- [.NET 10 SDK](https://dotnet.microsoft.com/download)
- `make` (opcional, recomendado)

### Compilando com o `Makefile`

O [Makefile](Makefile) detecta automaticamente seu sistema operacional (`win-x64`, `linux-x64`, `linux-arm64`, `osx-arm64` ou `osx-x64`):

| Comando | Descrição |
| --- | --- |
| `make` ou `make standalone` | Compila um **único executável** autocontido em `dist/<RID>-standalone/` (não precisa de .NET) |
| `make portable` | Compila a versão **portátil em pasta** autocontida em `dist/<RID>-portable/` |
| `make release` | Gera o executável único + arquivo `.zip` em `dist/` para o sistema atual |
| `make release-win-x64` | Gera pacote `.zip` standalone para **Windows x64** |
| `make release-win-arm64` | Gera pacote `.zip` standalone para **Windows ARM64** |
| `make release-linux-x64` | Gera pacote `.zip` standalone para **Linux x64** |
| `make release-linux-arm64` | Gera pacote `.zip` standalone para **Linux ARM64** |
| `make release-osx-arm64` | Gera pacote `.zip` standalone para **macOS Apple Silicon** |
| `make release-osx-x64` | Gera pacote `.zip` standalone para **macOS Intel** |
| `make release-all-platforms` | Gera releases para **Windows, Linux e macOS** |
| `make run` | Executa o aplicativo em modo de desenvolvimento |
| `make build` | Compila em modo `Debug` |
| `make clean` | Remove `bin/`, `obj/` e `dist/` |

#### Compilando manualmente (sem `make`)

```bash
# Executar em desenvolvimento
dotnet run --project src/PHPost/PHPost.csproj

# Publicar executável único self-contained (troque o -r para win-x64, linux-x64, linux-arm64, osx-arm64 ou osx-x64)
dotnet publish src/PHPost/PHPost.csproj \
  -c Release \
  -r win-x64 \
  --self-contained true \
  -p:PublishSingleFile=true \
  -p:IncludeNativeLibrariesForSelfExtract=true \
  -p:EnableCompressionInSingleFile=true \
  -p:DebugType=none \
  -p:DebugSymbols=false \
  -o dist/win-x64-standalone
```

---

## Estrutura de Pastas de Dados Padrão

Todos os arquivos gerenciados pelo PHPost ficam no diretório de dados do usuário (configurável em **Desktop & Ajustes**):

- **Windows:** `%LOCALAPPDATA%\PHPost`
- **Linux:** `~/.local/share/PHPost` (ou `$XDG_DATA_HOME/PHPost`)
- **macOS:** `~/Library/Application Support/PHPost`

Dentro dele:

- `runtimes/php/<versao>/` — Binários do PHP e `php.ini`
- `runtimes/mariadb/<versao>/` — Binários (ou referência Homebrew) do MariaDB
- `data/mariadb/` — Bancos de dados e `my.ini` (Windows) / `my.cnf` (Linux/macOS)
- `www/` — Seus projetos PHP (Document Root)
- `downloads/` — Cache dos pacotes baixados
- `settings.json` — Configurações do PHPost

---

## Tecnologias Utilizadas

- **C# / .NET 10**
- **Avalonia UI 11** (Fluent Theme + Inter Font)
- **CommunityToolkit.Mvvm**
