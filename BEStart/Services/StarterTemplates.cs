using BEStart.Models;

namespace BEStart.Services;

public static class StarterTemplates
{
    public static string GetRootDashboardPhp(AppSettings settings)
    {
        var mariaPort = settings.GetPort(ServiceKind.MariaDb);
        var pgPort = settings.GetPort(ServiceKind.PostgreSql);
        var mysqlPort = settings.GetPort(ServiceKind.MySql);

        return $$"""
<?php
if (isset($_GET['phpinfo'])) {
    phpinfo();
    exit;
}

$mariaPort = {{mariaPort}};
$pgPort = {{pgPort}};
$mysqlPort = {{mysqlPort}};

$projects = [];
foreach (scandir(__DIR__) as $item) {
    if ($item === '.' || $item === '..' || str_starts_with($item, '.')) {
        continue;
    }
    if (is_dir(__DIR__ . DIRECTORY_SEPARATOR . $item)) {
        $projects[] = $item;
    }
}
sort($projects);

$keyExtensions = ['pdo_mysql', 'mysqli', 'pdo_pgsql', 'pgsql', 'mbstring', 'openssl', 'curl', 'gd', 'zip', 'fileinfo', 'intl'];
?>
<!DOCTYPE html>
<html lang="pt-BR">
<head>
    <meta charset="UTF-8">
    <meta name="viewport" content="width=device-width, initial-scale=1.0">
    <title>BE-Start — Ambiente Local</title>
    <style>
        :root {
            --bg: #0b0f19;
            --card: #121826;
            --border: #1f293d;
            --text: #e5ecf6;
            --muted: #8b9bb4;
            --accent: #6366f1;
            --green: #22c55e;
        }
        * { box-sizing: border-box; margin: 0; padding: 0; }
        body {
            font-family: 'Segoe UI', Inter, system-ui, sans-serif;
            background: radial-gradient(circle at top right, #17203a, var(--bg) 55%);
            color: var(--text);
            min-height: 100vh;
            padding: 2.5rem 1.5rem;
        }
        .container { max-width: 980px; margin: 0 auto; }
        header {
            display: flex; flex-wrap: wrap; justify-content: space-between; align-items: center;
            gap: 1rem; margin-bottom: 2rem;
        }
        .brand h1 { font-size: 1.9rem; letter-spacing: -0.03em; }
        .brand span { color: var(--accent); }
        .brand p { color: var(--muted); margin-top: 0.25rem; font-size: 0.95rem; }
        .btn {
            display: inline-block; padding: 0.65rem 1.1rem; border-radius: 10px;
            background: var(--accent); color: white; text-decoration: none;
            font-weight: 600; font-size: 0.9rem;
        }
        .card {
            background: var(--card); border: 1px solid var(--border);
            border-radius: 16px; padding: 1.4rem; margin-bottom: 1.25rem;
        }
        .card h2 { font-size: 1.05rem; margin-bottom: 0.85rem; display: flex; align-items: center; justify-content: space-between; }
        .pill { font-size: 0.78rem; padding: 0.2rem 0.65rem; border-radius: 999px; font-weight: 600; background: rgba(34, 197, 94, 0.15); color: var(--green); }
        .meta { color: var(--muted); font-size: 0.9rem; line-height: 1.6; }
        .meta strong { color: var(--text); }
        .tags { display: flex; flex-wrap: wrap; gap: 0.45rem; margin-top: 0.85rem; }
        .tag { font-size: 0.78rem; padding: 0.25rem 0.6rem; border-radius: 8px; border: 1px solid var(--border); color: var(--muted); }
        .tag.on { border-color: rgba(34,197,94,.35); color: #86efac; }
        .projects { display: grid; grid-template-columns: repeat(auto-fill, minmax(210px, 1fr)); gap: 0.85rem; margin-top: 0.75rem; }
        .proj {
            display: block; padding: 0.95rem 1.1rem; border-radius: 12px;
            background: rgba(255,255,255,0.03); border: 1px solid var(--border);
            color: var(--text); text-decoration: none; font-weight: 600;
        }
        .proj:hover { border-color: var(--accent); }
        .proj small { display: block; color: var(--muted); font-weight: 400; margin-top: 0.25rem; }
    </style>
</head>
<body>
<div class="container">
    <header>
        <div class="brand">
            <h1>BE<span>-Start</span></h1>
            <p>Ambiente local leve de PHP, Bancos SQL e NoSQL.</p>
        </div>
        <a class="btn" href="?phpinfo=1">Ver phpinfo()</a>
    </header>

    <div class="card">
        <h2>PHP Runtime <span class="pill">Online</span></h2>
        <div class="meta">
            <div>Versão: <strong><?= htmlspecialchars(PHP_VERSION) ?></strong> • SAPI: <strong><?= htmlspecialchars(PHP_SAPI) ?></strong></div>
            <div>php.ini: <strong><?= htmlspecialchars(php_ini_loaded_file() ?: 'Padrão') ?></strong></div>
            <div>Portas padrão: MariaDB <code>127.0.0.1:<?= $mariaPort ?></code> • MySQL <code>127.0.0.1:<?= $mysqlPort ?></code> • PostgreSQL <code>127.0.0.1:<?= $pgPort ?></code></div>
        </div>
        <div class="tags">
            <?php foreach ($keyExtensions as $ext): ?>
                <span class="tag <?= extension_loaded($ext) ? 'on' : '' ?>"><?= $ext ?></span>
            <?php endforeach; ?>
        </div>
    </div>

    <div class="card">
        <h2>Seus Projetos (<?= count($projects) ?>)</h2>
        <?php if (empty($projects)): ?>
            <p class="meta">Nenhum projeto ainda. Crie um novo projeto na aba <strong>PHP</strong> do BE-Start ou adicione uma pasta em <code><?= htmlspecialchars(__DIR__) ?></code>.</p>
        <?php else: ?>
            <div class="projects">
                <?php foreach ($projects as $p): ?>
                    <a class="proj" href="/<?= rawurlencode($p) ?>/">
                        <?= htmlspecialchars($p) ?>
                        <small>/<?= htmlspecialchars($p) ?>/</small>
                    </a>
                <?php endforeach; ?>
            </div>
        <?php endif; ?>
    </div>
</div>
</body>
</html>
""";
    }

    public static string GetNewProjectIndexPhp(string projectName, int mariaDbPort) => $$"""
<?php
declare(strict_types=1);

$projectName = '{{projectName.Replace("'", "\\'")}}';
$dbHost = '127.0.0.1';
$dbPort = {{mariaDbPort}};
$dbUser = 'root';
$dbPass = '';

$dbMessage = 'Banco SQL offline (inicie no BE-Start)';
$dbOk = false;

try {
    $pdo = new PDO("mysql:host={$dbHost};port={$dbPort};charset=utf8mb4", $dbUser, $dbPass, [
        PDO::ATTR_ERRMODE => PDO::ERRMODE_EXCEPTION,
        PDO::ATTR_TIMEOUT => 2,
    ]);
    $dbVersion = $pdo->query('SELECT VERSION()')->fetchColumn();
    $dbMessage = "Conectado ao banco {$dbVersion}";
    $dbOk = true;
} catch (Throwable $e) {
    $dbMessage = 'Sem conexão SQL: ' . $e->getMessage();
}
?>
<!DOCTYPE html>
<html lang="pt-BR">
<head>
    <meta charset="UTF-8">
    <meta name="viewport" content="width=device-width, initial-scale=1.0">
    <title><?= htmlspecialchars($projectName) ?> — BE-Start</title>
    <style>
        body {
            font-family: 'Segoe UI', system-ui, sans-serif;
            background: #0b0f19; color: #e5ecf6;
            display: grid; place-items: center; min-height: 100vh; margin: 0;
        }
        .box {
            background: #121826; border: 1px solid #1f293d; border-radius: 16px;
            padding: 2rem 2.4rem; max-width: 520px; width: 92%;
        }
        h1 { margin: 0 0 .5rem; font-size: 1.6rem; }
        p { color: #8b9bb4; line-height: 1.5; }
        .badge {
            display: inline-block; margin-top: 1rem; padding: .35rem .75rem;
            border-radius: 999px; font-size: .85rem; font-weight: 600;
            background: <?= $dbOk ? 'rgba(34,197,94,.16)' : 'rgba(239,68,68,.16)' ?>;
            color: <?= $dbOk ? '#4ade80' : '#f87171' ?>;
        }
        a { color: #818cf8; text-decoration: none; display: inline-block; margin-top: 1.25rem; }
    </style>
</head>
<body>
    <div class="box">
        <h1><?= htmlspecialchars($projectName) ?></h1>
        <p>Projeto criado com <strong>BE-Start</strong> rodando em PHP <strong><?= PHP_VERSION ?></strong>.</p>
        <div class="badge"><?= htmlspecialchars($dbMessage) ?></div><br>
        <a href="/">&larr; Voltar ao painel BE-Start</a>
    </div>
</body>
</html>
""";
}
