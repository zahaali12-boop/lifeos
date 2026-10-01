# Quicker ERP developer entry points (ADR-0024). Every target works on a clean machine with Docker, .NET 10 and pnpm.
SHELL := /bin/bash
.DEFAULT_GOAL := help

COMPOSE ?= docker compose
# Owner connection used by tests to create per-class databases. Override to point at any PostgreSQL 16+ cluster.
QUICKER_TEST_CONNECTION ?= Host=127.0.0.1;Port=5432;Database=postgres;Username=quicker_owner;Password=quicker

.PHONY: help up down logs migrate seed demo backup restore verify build test test-dotnet test-web lint format web api clean

help: ## List targets
	@grep -E '^[a-zA-Z_-]+:.*?## ' $(MAKEFILE_LIST) | awk 'BEGIN {FS = ":.*?## "}; {printf "  \033[36m%-14s\033[0m %s\n", $$1, $$2}'

up: ## Start PostgreSQL, MinIO, Mailpit; run migrations and seeds; start API and web (hot reload)
	$(COMPOSE) up -d postgres minio mailpit
	$(COMPOSE) run --rm migrator
	$(COMPOSE) up -d api worker web
	@echo "API: http://localhost:8080  Worker: http://localhost:8081/health/ready  Web: http://localhost:5173  Mail: http://localhost:8025  MinIO: http://localhost:9001"
	@echo "Demo tenant: sign in as owner@quicker.example / DemoPass2026! (make demo rebuilds it)"

observe: ## Start everything plus Grafana/Tempo/Prometheus/Loki (http://localhost:3000, admin/admin); API and worker export traces, metrics and logs
	docker compose -f docker-compose.yml -f deploy/observability/docker-compose.observability.yml up --build -d

down: ## Stop everything (keeps data volumes)
	$(COMPOSE) down

logs: ## Tail service logs
	$(COMPOSE) logs -f --tail=200

migrate: ## Apply migrations and repeatable scripts to the local database
	dotnet run --project src/Host/Quicker.Migrator -- migrate

seed: ## Apply reference-data seeds (idempotent)
	dotnet run --project src/Host/Quicker.Migrator -- seed

demo: ## Rebuild the demo tenant from scratch (migrations and seeds first); sign in as owner@quicker.example / DemoPass2026!
	dotnet run --project src/Host/Quicker.Migrator -- demo

backup: ## Back up the local database with its manifest (OUT=file, default backups/quicker-<UTC time>.dump)
	dotnet run --project src/Host/Quicker.Migrator -- backup --out $(or $(OUT),backups/quicker-$(shell date -u +%Y%m%dT%H%M%SZ).dump)

restore: ## Restore a backup into a new database and prove it: rows against the manifest, then the invariant harness (FROM=file DB=name)
	@test -n "$(FROM)" -a -n "$(DB)" || { echo "usage: make restore FROM=backups/<file>.dump DB=<new database name>"; exit 2; }
	dotnet run --project src/Host/Quicker.Migrator -- restore --from $(FROM) --database $(DB)

verify: ## Run the invariant harness over every active tenant of the local database (TENANT=slug for one)
	dotnet run --project src/Host/Quicker.Migrator -- verify $(if $(TENANT),--tenant $(TENANT))

build: ## Build .NET solution and web packages
	dotnet build Quicker.sln
	pnpm -r build

test: test-dotnet test-web ## Run every test suite the way CI does

test-dotnet: ## .NET unit, integration, architecture and scenario tests (needs a PostgreSQL owner connection)
	QUICKER_TEST_CONNECTION="$(QUICKER_TEST_CONNECTION)" dotnet test --solution Quicker.sln

test-web: ## Web unit tests
	pnpm -r test

lint: ## Format check, analyzers, ESLint, Stylelint, type check
	dotnet format Quicker.sln --verify-no-changes
	pnpm -r lint
	pnpm -r typecheck

format: ## Apply formatters
	dotnet format Quicker.sln
	pnpm -r format

api: ## Run the API with hot reload against the local database
	dotnet watch --project src/Host/Quicker.Api

web: ## Run the web app dev server
	pnpm --filter @quicker/web dev

e2e: ## Playwright journeys in EN and AR with axe (needs the API on 8080: make api)
	E2E_API_URL=http://127.0.0.1:8080 pnpm --filter @quicker/web e2e

api-contract: ## Rebuild the API (regenerates contracts/openapi-v1.json) and the typed web client
	dotnet build src/Host/Quicker.Api/Quicker.Api.csproj -c Release
	pnpm --filter @quicker/web generate:api

clean: ## Remove build outputs
	dotnet clean Quicker.sln >/dev/null
	rm -rf apps/*/dist packages/*/dist
