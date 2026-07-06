#!/usr/bin/env pwsh
#
# Removes the LAST migration from the relevant migration stacks (per context +
# provider). Windows counterpart of remove-migration.sh (same behaviour/output).
#
# Uses --force so it works OFFLINE: 'migrations remove' normally connects to the
# database to check whether the migration has already been applied, and refuses
# when the server is unreachable. With --force only the migration files are
# rolled back, without a DB check. Safe for a just-added, NOT YET deployed
# migration. Do NOT use it when the last migration is already on a (production) DB.
#
# Usage:
#   scripts/remove-migration.ps1 [scope]
#     scope = all (default) | client | server

[CmdletBinding()]
param(
    [Parameter(Position = 0)]
    [ValidateSet('all', 'client', 'server')]
    [string]$Scope = 'all'
)

$ErrorActionPreference = 'Stop'
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

Write-Host "==> Removing last migration (scope: $Scope) from $($stacks.Count) stack(s)"

$ok = @()
$fail = @()

foreach ($s in $stacks) {
    Write-Host ""
    Write-Host "----- $($s.Proj) ($($s.Ctx)) -----"
    $proj = Join-Path $root $s.Proj
    dotnet ef migrations remove --force `
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
    exit 1
}
Write-Host "=================================================="
