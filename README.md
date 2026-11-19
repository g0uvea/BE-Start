# PHPost

Um gerenciador de ambiente local **leve e moderno** feito em **C# + Avalonia UI** para baixar, configurar e executar **PHP** e **MariaDB** diretamente da web — no estilo do XAMPP, porém mais rápido, portátil e sem exigir privilégios de administrador.

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

## Como Executar

Pré-requisito: [.NET 10 SDK](https://dotnet.microsoft.com/download) (Windows x64).

```powershell
dotnet run --project src/PHPost/PHPost.csproj
```

Para gerar um executável pronto para distribuição:

```powershell
dotnet publish src/PHPost/PHPost.csproj -c Release -r win-x64 --self-contained false
```

## Estrutura de Pastas Padrão

Por padrão, todos os arquivos gerenciados ficam em `%LOCALAPPDATA%\PHPost` (configurável em **Desktop & Ajustes**):

- `runtimes/php/<versao>/` — Binários do PHP e `php.ini`
- `runtimes/mariadb/<versao>/` — Binários do MariaDB
- `data/mariadb/` — Bancos de dados e `my.ini`
- `www/` — Seus projetos PHP (Document Root)
- `downloads/` — Cache dos pacotes `.zip` baixados
- `settings.json` — Configurações do PHPost
