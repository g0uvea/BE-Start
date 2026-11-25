# BE-Start

![.NET 10](https://img.shields.io/badge/.NET-10.0-512BD4?logo=dotnet)
![Avalonia UI](https://img.shields.io/badge/Avalonia%20UI-12.2-8B44AC)
![Plataformas](https://img.shields.io/badge/Windows%20%7C%20Linux%20%7C%20macOS-x64%20%7C%20ARM64-0078D6)
![Self-Contained](<https://img.shields.io/badge/Build-Self--Contained%20(Sem%20.NET)-2EA44F>)

Um gerenciador de ambiente local **ultra leve, portátil e multiplataforma** feito em **C# + Avalonia UI** para baixar, configurar e executar **Bancos SQL**, **Bancos NoSQL**, **PHP 8.x** e **PHP Composer 2.11.x** consumindo o mínimo possível de memória RAM e CPU do computador.

---

## Tecnologias Suportadas

- **Bancos SQL:**
  - **PostgreSQL** (`127.0.0.1:5432`)
  - **MariaDB** (`127.0.0.1:3306`)
  - **MySQL** (`127.0.0.1:3307`)
  - **Microsoft SQL Server** (`127.0.0.1:1433` — LocalDB / Express)
- **Bancos NoSQL:**
  - **MongoDB** (`127.0.0.1:27017`, cache WiredTiger reduzido para 256 MB)
  - **Redis** (`127.0.0.1:6379`, `maxmemory 128mb`)
  - **Apache Cassandra** (`127.0.0.1:9042`, JVM limitada a `-Xms256M -Xmx512M`)
- **PHP & Ferramentas:**
  - **PHP 8.x** (`http://127.0.0.1:8000`)
  - **PHP Composer 2.11.x** (com opção de instalação automática junto ao PHP)
  - **Gestor de Projetos (`www`)** integrado diretamente na aba PHP
  - **DBeaver Community Edition** (botão **Iniciar DBeaver** com download, atualização automática e execução integrados)

---

## Otimizações de Baixo Consumo

- **Workstation GC (`ServerGarbageCollection=false`)** e liberação de páginas ociosas (`RetainVMGarbageCollection=false`).
- **Serialização JSON Source-Generated (`AppJsonContext`)** sem reflexão em tempo de execução.
- **Cache local do catálogo (`catalog-cache.json`)** com TTL de 6 horas para evitar requisições repetidas ao abrir o app.
- **Lista virtualizada de logs** (`ListBox` com `VirtualizingStackPanel`) e pincéis estáticos pré-alocados (`ImmutableSolidColorBrush`).
- **Configuração enxuta dos bancos** (`shared_buffers=32MB`, `innodb_buffer_pool_size=32MB`, `cacheSizeGB: 0.25`, `maxmemory 128mb`, `-Xmx512M`).

---

## Estrutura do Projeto

- `BEStart/` — Código-fonte principal da aplicação (.NET 10 + Avalonia UI 12.2)
- `Assets/be-start-icon.ico` — Ícone oficial da aplicação (executável e janela)
- `BEStart.slnx` — Solução .NET
- `Makefile` — Automação de build e empacotamento multiplataforma

### Pasta de Dados Padrão (Runtime & Bancos)

- **Windows:** `%LOCALAPPDATA%\BEStart`
- **Linux:** `~/.local/share/BEStart` (ou `$XDG_DATA_HOME/BEStart`)
- **macOS:** `~/Library/Application Support/BEStart`

---

## Como Compilar

### Com `make` ([Makefile](Makefile))

| Comando | Descrição |
| --- | --- |
| `make build` | Compila em modo `Debug` |
| `make run` | Executa o BE-Start em modo desenvolvimento |
| `make standalone` | Gera executável único self-contained em `dist/<RID>-standalone/` |
| `make portable` | Gera pasta portátil self-contained em `dist/<RID>-portable/` |
| `make release` | Gera executável único + `.zip` em `dist/` |
| `make clean` | Remove `bin/`, `obj/` e `dist/` |

### Sem `make`

```bash
# Executar em desenvolvimento
dotnet run --project BEStart/BEStart.csproj

# Compilar solução
dotnet build BEStart.slnx -c Release
```
