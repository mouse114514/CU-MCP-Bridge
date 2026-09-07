param(
    [switch]$NoBuild,
    [switch]$NoGame,
    [switch]$Dev
)

$projectRoot = "C:\Users\Administrator\Desktop\work\CU-MCP-Bridge"
$gameDir   = "C:\Program Files (x86)\Steam\steamapps\common\Casualties Unknown Demo"
$gameExe   = "$gameDir\CasualtiesUnknown.exe"
$gamePlugin = "$gameDir\BepInEx\plugins"
$modDll    = "$projectRoot\src\bridge_mod\bin\Release\net472\CU-MCP-Mod.dll"

Write-Host "=== CU-MCP-Bridge 一键启动 ===" -ForegroundColor Cyan

# 1. 构建
if (-not $NoBuild) {
    Write-Host "[1/4] 构建 Mod..." -ForegroundColor Yellow
    dotnet build "$projectRoot\src\bridge_mod\CU-MCP-Mod.csproj" -c Release -q
    if ($LASTEXITCODE -ne 0) { Write-Host "构建失败!" -ForegroundColor Red; exit 1 }
    Write-Host "  OK" -ForegroundColor Green
}

# 2. 部署
Write-Host "[2/4] 部署 Mod..." -ForegroundColor Yellow
Stop-Process -Name "CasualtiesUnknown" -Force -ErrorAction SilentlyContinue
Start-Sleep -Milliseconds 300
Remove-Item "$gamePlugin\CU-MCP-Mod.dll" -Force -ErrorAction SilentlyContinue
Copy-Item $modDll "$gamePlugin\CU-MCP-Mod.dll" -Force
Copy-Item "$projectRoot\src\bridge_mod\bin\Release\net472\Newtonsoft.Json.dll" "$gamePlugin\Newtonsoft.Json.dll" -Force -ErrorAction SilentlyContinue
Write-Host "  OK" -ForegroundColor Green

# 3. 启动游戏
if (-not $NoGame) {
    Write-Host "[3/4] 启动游戏..." -ForegroundColor Yellow
    $running = Get-Process -Name "CasualtiesUnknown" -ErrorAction SilentlyContinue
    if ($running) {
        Write-Host "  游戏已在运行" -ForegroundColor Green
    } else {
        if (Test-Path $gameExe) {
            Start-Process $gameExe
            Write-Host "  已启动: $gameExe" -ForegroundColor Green
        } else {
            Write-Host "  尝试 Steam 启动..." -ForegroundColor DarkYellow
            Start-Process "steam://rungameid/2873885940"
        }
        Start-Sleep -Seconds 3
    }
} else {
    Write-Host "[3/4] 跳过游戏启动" -ForegroundColor DarkYellow
}

# 4. MCP 服务
Write-Host "[4/4] Python MCP 桥接服务..." -ForegroundColor Yellow
$env:CU_MCP_HTTP_HOST = "127.0.0.1"
$env:CU_MCP_HTTP_PORT = "8765"

if ($Dev) {
    Write-Host "  前台模式 (Ctrl+C 退出)" -ForegroundColor Gray
    python "$projectRoot\src\bridge_server\server.py"
} else {
    Write-Host "  由 OpenCode 通过 stdio 自动管理" -ForegroundColor Gray
    Write-Host "  配置: $projectRoot\opencode_mcp.json" -ForegroundColor Gray
}

Write-Host "`n=== 启动完成 ===" -ForegroundColor Cyan
Write-Host "游戏已就绪，在 OpenCode 中开始对话即可激活 MCP 桥接" -ForegroundColor Gray
