# PHPost

![.NET 10](https://img.shields.io/badge/.NET-10.0-512BD4?logo=dotnet)
![Avalonia UI](https://img.shields.io/badge/Avalonia%20UI-11.3-8B44AC)
![Plataforma](https://img.shields.io/badge/Windows-x64%20%7C%20ARM64-0078D6?logo=windows)
![Self-Contained](https://img.shields.io/badge/Build-Self--Contained%20(Sem%20.NET)-2EA44F)

Um gerenciador de ambiente local **leve, portátil e moderno** feito em **C# + Avalonia UI** para baixar, configurar e executar **PHP** e **MariaDB** diretamente das fontes oficiais — no estilo do XAMPP, porém mais rápido, limpo e sem exigir privilégios de administrador.

---

## Principais Recursos

- **Download direto das fontes oficiais** com verificação de integridade **SHA-256**:
  - **PHP para Windows (x64)** via `windows.php.net/downloads/releases/releases.json`
  - **MariaDB Portátil (ZIP x64)** via `downloads.mariadb.org/rest-api/mariadb/`
- **Instalação Automática em 1 Clique ("Instalar & Iniciar Tudo")**:
  - Baixa e extrai as versões recomendadas de PHP e MariaDB;
  - Gera e configura automaticamente o `php.ini` ativando extensões essenciais (`pdo_mysql`, `mysqli`, `mbstring`, `curl`, `openssl`, `gd`, `zip`, `intl`, `fileinfo`);
  - Inicializa automaticamente o diretório de dados do MariaDB (`mariadb-install-db.exe`) e gera o `my.ini` com suporte a `utf8mb4`;
  - Adiciona PHP e MariaDB ao **PATH do usuário do Windows** (sem UAC/Admin) e cria atalhos na **Área de Trabalho** (`PHPost` e `Projetos PHPost (www)`);
  - Cria uma página inicial em `www/index.php` com diagnóstico em tempo real de PHP + conexão MariaDB.
- **Gerenciador de Múltiplas Versões**: instale, troque ou remova versões de PHP e MariaDB facilmente.
- **Criador de Projetos PHP**: crie novos projetos dentro da pasta `www` já com um `index.php` pronto para testar a conexão PDO com MariaDB.
- **Terminal Pré-configurado**: abra um PowerShell já na pasta `www` com `php` e `mariadb` disponíveis no `PATH`.
- **Console de Logs em Tempo Real**: acompanhe saídas do PHP, MariaDB e do PHPost com filtros por serviço.
- **Encerramento Seguro**: encerra o MariaDB de forma graciosa (`mariadb-admin shutdown`) ao parar o serviço ou fechar o app.

---

## Para Usuários Finais (Sem Precisar Instalar o .NET)

Os executáveis distribuídos do **PHPost** são compilados em modo **Self-Contained** (autocontido), o que significa que o runtime do .NET já vem embutido no próprio executável:

1. Baixe o arquivo `PHPost-<versao>-win-x64-standalone.zip` na aba **Releases**;
2. Extraia e execute **`PHPost.exe`** — **não é necessário instalar o .NET Runtime nem o SDK**;
3. Clique em **"Instalar & Iniciar Tudo"** para configurar PHP e MariaDB automaticamente.

---

## Como Compilar (Desenvolvedores)

### Pré-requisitos

- [.NET 10 SDK](https://dotnet.microsoft.com/download)
- `make` (opcional, recomendado — disponível via GnuWin32, Chocolatey, Scoop, MSYS2 ou Git Bash)

### Compilando com o `Makefile`

O projeto inclui um [Makefile](Makefile) pronto para gerar executáveis **self-contained** (que rodam em qualquer Windows sem exigir o .NET instalado):

| Comando | Descrição |
| --- | --- |
| `make` ou `make standalone` | Compila um **único `PHPost.exe`** autocontido em `dist/win-x64-standalone/` (não precisa de .NET) |
| `make portable` | Compila a versão **portátil em pasta** autocontida em `dist/win-x64-portable/` (não precisa de .NET) |
| `make release` | Gera o executável único + arquivo `.zip` em `dist/` pronto para GitHub Releases |
| `make release-portable` | Gera a versão portátil + arquivo `.zip` em `dist/` |
| `make release-arm64` | Gera o executável único + `.zip` para Windows ARM64 (`win-arm64`) |
| `make release-all` | Gera ambos os pacotes `.zip` (`standalone` e `portable`) para `win-x64` |
| `make run` | Executa o aplicativo em modo de desenvolvimento |
| `make build` | Compila em modo `Debug` |
| `make restore` | Restaura os pacotes NuGet |
| `make clean` | Remove `bin/`, `obj/` e `dist/` |
| `make help` | Exibe a ajuda com todos os alvos disponíveis |

#### Opções adicionais no `Makefile`

Você pode personalizar a versão, arquitetura ou habilitar ReadyToRun (inicialização mais rápida):

```powershell
make release VERSION=1.1.0
make standalone RID=win-arm64
make release VERSION=1.1.0 R2R=true
```

### Standalone vs. Portátil

- **Standalone (`make standalone` / `make release`)**: gera um **único arquivo `PHPost.exe`** com runtime .NET e bibliotecas nativas do Avalonia/SkiaSharp embutidos e comprimidos. Ideal para distribuição simples (basta baixar e abrir).
- **Portátil (`make portable` / `make release-portable`)**: gera uma pasta com `PHPost.exe` e as DLLs ao lado, também **sem exigir .NET instalado**. Inicia um pouco mais rápido na primeira execução por não precisar extrair bibliotecas nativas.

### Compilando manualmente (sem `make`)

Caso não tenha o `make` instalado, execute diretamente com o `dotnet`:

```powershell
# Executar em desenvolvimento
dotnet run --project src/PHPost/PHPost.csproj

# Gerar executável único self-contained (sem necessidade de .NET no usuário)
dotnet publish src/PHPost/PHPost.csproj `
  -c Release `
  -r win-x64 `
  --self-contained true `
  -p:PublishSingleFile=true `
  -p:IncludeNativeLibrariesForSelfExtract=true `
  -p:EnableCompressionInSingleFile=true `
  -p:DebugType=none `
  -p:DebugSymbols=false `
  -o dist/win-x64-standalone
```

---

## Estrutura do Projeto

```text
PHPost/
├── .gitattributes
├── .gitignore
├── Makefile
├── PHPost.slnx
├── README.md
└── src/
    └── PHPost/
        ├── Converters/        # Conversores de binding da UI
        ├── Models/            # Modelos de configuração, logs, projetos e releases
        ├── Services/          # Download, instalação, processos, PATH e templates
        ├── ViewModels/        # ViewModels MVVM (CommunityToolkit.Mvvm)
        ├── Views/             # Interface Avalonia UI (.axaml)
        ├── App.axaml(.cs)
        ├── PHPost.csproj
        └── Program.cs
```

---

## Estrutura de Pastas de Dados Padrão

Por padrão, todos os arquivos gerenciados pelo PHPost ficam em `%LOCALAPPDATA%\PHPost` (configurável em **Desktop & Ajustes**):

- `runtimes/php/<versao>/` — Binários do PHP e `php.ini`
- `runtimes/mariadb/<versao>/` — Binários do MariaDB
- `data/mariadb/` — Bancos de dados e `my.ini`
- `www/` — Seus projetos PHP (Document Root)
- `downloads/` — Cache dos pacotes `.zip` baixados
- `settings.json` — Configurações do PHPost

---

## Tecnologias Utilizadas

- **C# / .NET 10**
- **Avalonia UI 11** (Fluent Theme + Inter Font)
- **CommunityToolkit.Mvvm**
