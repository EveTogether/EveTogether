#!/usr/bin/env pwsh
#
# Adds the SAME migration to the migration stacks of all relevant contexts +
# providers in one run, so an entity change never misses a stack.
# Windows counterpart of add-migration.sh (same behaviour/output).
#
# Matrix:
#   client : ClientDbContext  -> SQLite
#   server : ServerDbContext  -> SQLite (dev), MySQL, SQL Server, PostgreSQL
#
# Usage:
#   scripts/add-migration.ps1 <MigrationName> [scope]
#     scope = all (default) | client | server

[CmdletBinding()]
param(
    [Parameter(Mandatory = $true, Position = 0)]
    [string]$Name,

    [Parameter(Position = 1)]
    [ValidateSet('all', 'client', 'server')]
    [string]$Scope = 'all'
)

$ErrorActionPreference = 'Stop'
# Do NOT let a failing native command (dotnet) abort the loop — we collect failures ourselves.
if (Test-Path variable:PSNativeCommandUseErrorActionPreference) {
    $PSNativeCommandUseErrorActionPreference = $false
}

$root = Split-Path -Parent $PSScriptRoot   # scripts/.. = repo root

$clientStacks = @(
    @{ Proj = 'EveUtils.Migrations.Client.Sqlite'; Ctx = 'ClientDbContext' }
)
$serverStacks = @(
    @{ Proj = 'EveUtils.Migrations.Server.Sqlite';     Ctx = 'ServerDbContext' }
    @{ Proj = 'EveUtils.Migrations.Server.MySql';      Ctx = 'ServerDbContext' }
    @{ Proj = 'EveUtils.Migrations.Server.SqlServer';  Ctx = 'ServerDbContext' }
    @{ Proj = 'EveUtils.Migrations.Server.PostgreSql'; Ctx = 'ServerDbContext' }
)

$stacks = switch ($Scope) {
    'client' { $clientStacks }
    'server' { $serverStacks }
    default  { $clientStacks + $serverStacks }
}

dotnet ef --version *> $null
if ($LASTEXITCODE -ne 0) {
    Write-Error "'dotnet ef' not found. Install: dotnet tool install --global dotnet-ef"
    exit 1
}

Write-Host "==> Adding migration '$Name' (scope: $Scope) to $($stacks.Count) stack(s)"

$ok = @()
$fail = @()

foreach ($s in $stacks) {
    Write-Host ""
    Write-Host "----- $($s.Proj) ($($s.Ctx)) -----"
    $proj = Join-Path $root $s.Proj
    dotnet ef migrations add $Name `
        --context $s.Ctx `
        --project $proj `
        --startup-project $proj
    if ($LASTEXITCODE -eq 0) { $ok += $s.Proj } else { $fail += $s.Proj }
}

Write-Host ""
Write-Host "=================================================="
Write-Host ("Succeeded : " + $(if ($ok) { $ok -join ' ' } else { 'none' }))
if ($fail.Count -gt 0) {
    Write-Host ("FAILED    : " + ($fail -join ' '))
    Write-Host ""
    Write-Host "WARNING: inconsistent state. Fix the error and rerun, or roll back with"
    Write-Host "  make migrate-remove SCOPE=$Scope"
    exit 1
}

Write-Host "Remember to run 'make build' before 'make server/client' (stale migration DLL otherwise)."
Write-Host "=================================================="
