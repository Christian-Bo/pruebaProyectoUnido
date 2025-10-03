# Dockerfile - ASP.NET Core 8 API (paramétrico: lexico/auth)

# ========= Base (runtime) =========
FROM mcr.microsoft.com/dotnet/aspnet:8.0-bookworm-slim AS base
WORKDIR /app
# Railway usa PORT; exponemos 8080 por conveniencia local
EXPOSE 8080
ENV ASPNETCORE_ENVIRONMENT=Production \
    DOTNET_EnableDiagnostics=0 \
    ASPNETCORE_URLS=http://0.0.0.0:${PORT:-8080}

# ========= Build =========
FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src

# Argumento para elegir el proyecto: lexico | auth
ARG PROJECT=auth
# Mapa directorios por arg
# - auth -> src/API/auth/Auth.API.csproj
# - lexico -> src/API/lexico/Lexico.API.csproj
ENV PROJECT=${PROJECT}
RUN echo "Building PROJECT=${PROJECT}"

# Copiamos el repo completo
COPY . .

# Restaurar por solución
RUN dotnet restore ./global.sln

# Seleccionar csproj y nombre del dll de salida
# Nota: Usa nombres exactos de tus proyectos
RUN if [ "$PROJECT" = "lexico" ]; then \
      dotnet publish ./src/API/lexico/Lexico.API.csproj -c Release -o /out /p:UseAppHost=false; \
    else \
      dotnet publish ./src/API/auth/Auth.API.csproj   -c Release -o /out /p:UseAppHost=false; \
    fi

# ========= Final =========
FROM base AS final
WORKDIR /app
COPY --from=build /out ./
# Variable PORT (Railway) o 8080 local
ENV PORT=8080

# Elegimos dll a ejecutar en runtime leyendo /app/*.dll
# (El publish dejó solo la API elegida)
# Para evitar editar ENTRYPOINT entre proyectos:
ENTRYPOINT ["sh", "-c", "dotnet $(ls *.API.dll)"]
