# Despliegue en Railway (Docker)

Este repo contiene una API ASP.NET Core 8 (Lexico.API).

## Variables de entorno
- `PORT`: la establece Railway automáticamente. Local: usa 8080.
- Base de datos MySQL (si usas el plugin de Railway, se añaden automáticamente):
  - `MYSQLHOST`, `MYSQLPORT`, `MYSQLDATABASE`, `MYSQLUSER`, `MYSQLPASSWORD`
- `ASPNETCORE_ENVIRONMENT`: `Production` en Railway.

## Correr local con Docker
```bash
docker build -t lexico-api .
docker run --rm -p 8080:8080 -e PORT=8080 -e ASPNETCORE_ENVIRONMENT=Development lexico-api
# Swagger: http://localhost:8080/swagger
# Health:  http://localhost:8080/api/health
```

## Despliegue con Railway (UI)
1. Sube este repo a GitHub.
2. En Railway → **New Project** → **Deploy from GitHub repo**.
3. Selecciona el repo y asegúrate de que detecta este `Dockerfile`.
4. En *Variables*, añade (si no usas plugin MySQL) `MYSQLHOST`, `MYSQLPORT`, `MYSQLDATABASE`, `MYSQLUSER`, `MYSQLPASSWORD`.
5. Define `ASPNETCORE_ENVIRONMENT=Production`.
6. *Health check path*: `/api/health`.
7. Despliega. Railway inyecta `PORT`; el `Program.cs` ya lo usa para escuchar.

## Despliegue con CLI
```bash
npm i -g @railway/cli   # o usa su instalador
railway login
railway init
railway up              # construye y sube usando el Dockerfile
railway logs -f
```
