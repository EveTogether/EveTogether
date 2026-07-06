# EveUtils — developer shortcuts
#
# Usage: make <target>   (run `make` or `make help` for the list)
#
# Runs in Development mode by default. Override the environment or db provider:
#   make server ENVIRONMENT=Production
#   make server PROVIDER=PostgreSql
#
# Cross-platform: works with GNU Make on Linux/macOS (sh) and on Windows (cmd.exe,
# no sh required). Environment variables are passed via Make's `export` instead of a
# shell-specific `VAR=value cmd` prefix, so the recipes never depend on a POSIX shell.

SLN         := EVE-Together.slnx
SERVER      := EveUtils.Server
CLIENT      := EveUtils.Client
CONFIG      ?= Debug
ENVIRONMENT ?= Development
PROVIDER    ?=

# Server is ASP.NET (ASPNETCORE_ENVIRONMENT drives app.Environment.IsDevelopment());
# the client is a generic host (DOTNET_ENVIRONMENT). Exporting both is harmless — each
# host reads only its own. Database__Provider is exported only when PROVIDER is set, so
# the default stays SQLite-dev.
export ASPNETCORE_ENVIRONMENT := $(ENVIRONMENT)
export DOTNET_ENVIRONMENT     := $(ENVIRONMENT)
ifneq ($(PROVIDER),)
export Database__Provider     := $(PROVIDER)
endif

DOTNET_RUN_SERVER := dotnet run --project $(SERVER) -c $(CONFIG)
DOTNET_RUN_CLIENT := dotnet run --project $(CLIENT) -c $(CONFIG)

# Migration helpers: bash on POSIX, PowerShell on Windows (same behaviour/output).
ifeq ($(OS),Windows_NT)
PWSH             := pwsh -NoProfile -ExecutionPolicy Bypass -File
ADD_MIGRATION    := $(PWSH) scripts/add-migration.ps1
REMOVE_MIGRATION := $(PWSH) scripts/remove-migration.ps1
else
ADD_MIGRATION    := scripts/add-migration.sh
REMOVE_MIGRATION := scripts/remove-migration.sh
endif

# Headless server suites run against an ISOLATED throwaway data dir (DB/cert/cache/auth), never the real
# anchored DB, so test fleets never pile up in production data (test isolation). `make server` keeps
# the anchored DB. Override the location with SERVER_TEST_DATA=… ; wipe it with `make clean-test-data`.
# The data dir is handed to the test recipes via a target-specific `export` below (not a `VAR=value cmd`
# prefix, which only works under a POSIX shell), so the test targets stay cross-platform like `make server`.
SERVER_TEST_DATA       ?= /tmp/eveutils-test-server-data
DOTNET_RUN_SERVER_TEST := dotnet run --project $(SERVER) -c $(CONFIG)

# Headless CLIENT suites run under an ISOLATED throwaway instance (EVEUTILS_INSTANCE), never the real
# client data dir that holds your actual characters/server links, so a test run can never pollute
# production state (the client-side counterpart of the server test isolation). `make client` keeps the real
# instance. Wipe the throwaway instance with `make clean-test-data`. The instance is handed to the test
# recipes via a target-specific `export` below, same cross-platform reason as the server data dir.
CLIENT_TEST_INSTANCE   ?= eveutils-test-client
DOTNET_RUN_CLIENT_TEST := dotnet run --project $(CLIENT) -c $(CONFIG)
CLIENT_TEST_DATA       := $(or $(XDG_DATA_HOME),$(HOME)/.local/share)/EveUtils/$(CLIENT_TEST_INSTANCE)

.DEFAULT_GOAL := help

## ---- Run ----

.PHONY: server
server: ## Run the server (SQLite-dev; set PROVIDER= to override)
	$(DOTNET_RUN_SERVER)

# Staging: target-specific exports override the global ENVIRONMENT for this target only, so the server loads
# appsettings.Staging.json (IsStaging()). Add a provider with `make server-staging PROVIDER=PostgreSql`.
.PHONY: server-staging
server-staging: export ASPNETCORE_ENVIRONMENT := Staging
server-staging: export DOTNET_ENVIRONMENT     := Staging
server-staging: ## Run the server in the Staging environment (loads appsettings.Staging.json)
	$(DOTNET_RUN_SERVER)

.PHONY: client
client: ## Run the Avalonia desktop client
	$(DOTNET_RUN_CLIENT)

.PHONY: smoke
smoke: ## Headless client data/CQRS verification (--smoke, isolated test instance)
	$(DOTNET_RUN_CLIENT_TEST) -- --smoke

## ---- Build ----

.PHONY: build
build: ## Build the whole solution
	dotnet build $(SLN) -c $(CONFIG)

.PHONY: restore
restore: ## Restore NuGet packages
	dotnet restore $(SLN)

.PHONY: clean
clean: ## Clean build output (bin/obj)
	dotnet clean $(SLN) -c $(CONFIG)

.PHONY: clean-test-data
clean-test-data: ## Wipe the isolated server + client test data dirs (fresh DB/cert/instance next run)
	rm -rf $(SERVER_TEST_DATA)
	rm -rf $(CLIENT_TEST_DATA)

## ---- Tests (headless suites) ----

# Target-specific exports (cross-platform: Make exports the var into the recipe's environment on any OS, where a
# `VAR=value cmd` prefix would need a POSIX shell). Only the test targets get the isolated data dir / instance;
# `make server` and `make client` are deliberately absent here and keep using the real anchored data.
test-server test-routing test-message test-fleet test-fleet-structure test-fleet-discovery \
test-fleet-invite test-fleet-participation test-fleet-cleanup test-fleet-active-guard test-fit-dedup test-fleet-autoplace test-sde test-fit-parse: \
	export EVEUTILS_SERVER_DATA_DIR := $(SERVER_TEST_DATA)

test-client smoke test-esi test-gamelog test-fleet-client test-fleet-metric test-remote: \
	export EVEUTILS_INSTANCE := $(CLIENT_TEST_INSTANCE)

.PHONY: test
test: test-server test-client ## Run all headless test suites

.PHONY: test-server
test-server: ## Run every server-side headless test suite
	$(DOTNET_RUN_SERVER_TEST) -- --routing-test
	$(DOTNET_RUN_SERVER_TEST) -- --message-test
	$(DOTNET_RUN_SERVER_TEST) -- --fleet-test
	$(DOTNET_RUN_SERVER_TEST) -- --fleet-structure-test
	$(DOTNET_RUN_SERVER_TEST) -- --fleet-discovery-test
	$(DOTNET_RUN_SERVER_TEST) -- --fleet-invite-test
	$(DOTNET_RUN_SERVER_TEST) -- --fleet-participation-test
	$(DOTNET_RUN_SERVER_TEST) -- --fleet-cleanup-test
	$(DOTNET_RUN_SERVER_TEST) -- --fleet-active-guard-test
	$(DOTNET_RUN_SERVER_TEST) -- --fit-dedup-test
	$(DOTNET_RUN_SERVER_TEST) -- --fleet-autoplace-test

.PHONY: test-client
test-client: ## Run every client-side headless test suite (isolated test instance)
	$(DOTNET_RUN_CLIENT_TEST) -- --smoke
	$(DOTNET_RUN_CLIENT_TEST) -- --esi-test
	$(DOTNET_RUN_CLIENT_TEST) -- --gamelog-test
	$(DOTNET_RUN_CLIENT_TEST) -- --fleet-client-test
	$(DOTNET_RUN_CLIENT_TEST) -- --fleet-metric-test
	$(DOTNET_RUN_CLIENT_TEST) -- --remote-test

# Individual suites — `make test-message`, `make test-esi`, etc.
.PHONY: test-routing test-message test-fleet test-fleet-structure test-fleet-discovery \
        test-fleet-invite test-fleet-participation test-fleet-cleanup test-fleet-active-guard test-fit-dedup test-fleet-autoplace
test-fleet-active-guard: ; $(DOTNET_RUN_SERVER_TEST) -- --fleet-active-guard-test
test-fit-dedup:         ; $(DOTNET_RUN_SERVER_TEST) -- --fit-dedup-test
test-fleet-autoplace:   ; $(DOTNET_RUN_SERVER_TEST) -- --fleet-autoplace-test
test-routing:            ; $(DOTNET_RUN_SERVER_TEST) -- --routing-test
test-message:           ; $(DOTNET_RUN_SERVER_TEST) -- --message-test
test-fleet:             ; $(DOTNET_RUN_SERVER_TEST) -- --fleet-test
test-fleet-structure:   ; $(DOTNET_RUN_SERVER_TEST) -- --fleet-structure-test
test-fleet-discovery:   ; $(DOTNET_RUN_SERVER_TEST) -- --fleet-discovery-test
test-fleet-invite:      ; $(DOTNET_RUN_SERVER_TEST) -- --fleet-invite-test
test-fleet-participation: ; $(DOTNET_RUN_SERVER_TEST) -- --fleet-participation-test
test-fleet-cleanup:     ; $(DOTNET_RUN_SERVER_TEST) -- --fleet-cleanup-test

# Real CCP download (~80 MB) + JSONL→SQLite build + accessor roundtrip. Kept out of the `test-server` aggregate
# because it hits the network and downloads the full SDE; run on demand.
.PHONY: test-sde
test-sde:               ; $(DOTNET_RUN_SERVER_TEST) -- --sde-import

# EFT + DNA fit parsers resolved against the real SDE store (needs the store; reuses it if already built).
.PHONY: test-fit-parse
test-fit-parse:         ; $(DOTNET_RUN_SERVER_TEST) -- --fit-parse-test

.PHONY: test-esi test-gamelog test-fleet-client test-fleet-metric test-remote
test-esi:               ; $(DOTNET_RUN_CLIENT_TEST) -- --esi-test
test-gamelog:           ; $(DOTNET_RUN_CLIENT_TEST) -- --gamelog-test
test-fleet-client:      ; $(DOTNET_RUN_CLIENT_TEST) -- --fleet-client-test
test-fleet-metric:      ; $(DOTNET_RUN_CLIENT_TEST) -- --fleet-metric-test
test-remote:            ; $(DOTNET_RUN_CLIENT_TEST) -- --remote-test

## ---- Migrations ----

.PHONY: migrate-add
migrate-add: ## Add a migration to all stacks: make migrate-add NAME=Foo [SCOPE=all|client|server]
	@$(if $(strip $(NAME)),,$(error Set NAME=<MigrationName>, e.g. make migrate-add NAME=AddFoo))
	$(ADD_MIGRATION) $(NAME) $(or $(SCOPE),all)

.PHONY: migrate-remove
migrate-remove: ## Remove the last migration: make migrate-remove [SCOPE=all|client|server]
	$(REMOVE_MIGRATION) $(or $(SCOPE),all)

## ---- Help ----

.PHONY: help
ifeq ($(OS),Windows_NT)
help: ## Show this help
	@echo.
	@echo EveUtils - developer shortcuts
	@echo.
	@echo Run:
	@echo   server                 Run the server (SQLite-dev; set PROVIDER= to override)
	@echo   client                 Run the Avalonia desktop client
	@echo   smoke                  Headless client data/CQRS verification (--smoke)
	@echo.
	@echo Build:
	@echo   build                  Build the whole solution
	@echo   restore                Restore NuGet packages
	@echo   clean                  Clean build output (bin/obj)
	@echo.
	@echo Tests:
	@echo   test                   Run all headless test suites
	@echo   test-server            Run every server-side headless test suite
	@echo   test-client            Run every client-side headless test suite
	@echo   test-^<suite^>           Individual suite, e.g. test-message, test-esi
	@echo.
	@echo Migrations:
	@echo   migrate-add            make migrate-add NAME=Foo (SCOPE=all, client or server)
	@echo   migrate-remove         make migrate-remove (SCOPE=all, client or server)
	@echo.
	@echo Overrides: CONFIG=Release  ENVIRONMENT=Production  PROVIDER=PostgreSql
	@echo.
else
help: ## Show this help
	@awk 'BEGIN {FS = ":.*## "} \
		/^## /        { printf "\n\033[1m%s\033[0m\n", substr($$0, 4) } \
		/^[a-zA-Z0-9_-]+:.*## / { printf "  \033[36m%-22s\033[0m %s\n", $$1, $$2 }' \
		$(MAKEFILE_LIST)
endif
