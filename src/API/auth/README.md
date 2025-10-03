# Auth API Integration (Clean Architecture-style)

Proyectos agregados bajo `src`:
- `Domain/auth` → **Auth.Domain**
- `Application/auth` → **Auth.Application**
- `Infrastructure/auth` → **Auth.Infrastructure**
- `API/auth` → **Auth.API** (Web API)

## Cómo agregar a la solución existente

Desde la carpeta raíz del repo (donde está `global.sln`):

```bash
dotnet sln global.sln add src/Domain/auth/Auth.Domain.csproj
dotnet sln global.sln add src/Application/auth/Auth.Application.csproj
dotnet sln global.sln add src/Infrastructure/auth/Auth.Infrastructure.csproj
dotnet sln global.sln add src/API/auth/Auth.API.csproj
```

## Restaurar y ejecutar

```bash
dotnet restore
dotnet build

# Ejecutar sólo Auth.API
dotnet run --project src/API/auth/Auth.API.csproj
```

Swagger: `https://localhost:5001/swagger` o `http://localhost:5000/swagger`

## Configuración

Edita `src/API/auth/appsettings.json`:
- `"ConnectionStrings:Default"` (MySQL)
- `"Jwt"`: `Issuer`, `Audience`, `Key` (64+ caracteres), `AccessTokenMinutes`

## Endpoints

- `POST /api/auth/register`
- `POST /api/auth/login`
- `POST /api/auth/logout` (requiere Bearer)
- `GET  /api/auth/me`     (requiere Bearer)

La API guarda **hash del token** en `sesiones` para poder **revocar** en `logout`.
