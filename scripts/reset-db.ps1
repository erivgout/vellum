# Drops a local Vellum development database so the next start recreates and reseeds it.
# Usage: ./scripts/reset-db.ps1 [-Database Vellum]
param([string]$Database = "Vellum")
$ErrorActionPreference = "Stop"
$sql = "IF DB_ID('$Database') IS NOT NULL BEGIN ALTER DATABASE [$Database] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE [$Database]; END"
docker exec vellum-sql /opt/mssql-tools18/bin/sqlcmd -C -S localhost -U sa -P "Vellum_Dev_2026!" -Q $sql
if ($LASTEXITCODE -ne 0) { throw "sqlcmd failed" }
$appData = Join-Path $PSScriptRoot "..\src\Vellum.Web\App_Data"
if ($Database -eq "Vellum" -and (Test-Path $appData)) { Remove-Item -Recurse -Force $appData }
Write-Host "Dropped database $Database"
