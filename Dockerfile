# Dockerfile - ASP.NET Core 8 API (Lexico)
# Etapa base (runtime)
FROM mcr.microsoft.com/dotnet/aspnet:8.0-bookworm-slim AS base
WORKDIR /app
# Railway provee PORT dinámico. Para local, mapea 8080.
EXPOSE 8080
ENV ASPNETCORE_ENVIRONMENT=Production \
    DOTNET_EnableDiagnostics=0

# Etapa de build/publish
FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src
# Copiamos todo el repo (Dockerfile debe estar en la raíz del repo)
COPY . .
# Restaurar dependencias
RUN dotnet restore ./global.sln
# Publicar (sin apphost para contenedores más ligeros)
RUN dotnet publish ./src/API/lexico/Lexico.API.csproj -c Release -o /out /p:UseAppHost=false

# Imagen final
FROM base AS final
WORKDIR /app
COPY --from=build /out ./
# Valor por defecto para correr localmente: 8080
ENV PORT=8080
ENTRYPOINT [ "dotnet", "Lexico.API.dll" ]
