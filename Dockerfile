FROM mcr.microsoft.com/dotnet/sdk:10.0@sha256:e70cdb7f80b0348f5cb85f19a8f670fca061f033d57eed12fa003d58b0e06317 AS build
WORKDIR /source
COPY global.json Directory.Build.props Directory.Packages.props ./
COPY src/ src/
RUN dotnet restore src/Presentation/ArturRios.Cerberus.WebApi/ArturRios.Cerberus.WebApi.csproj
RUN dotnet publish src/Presentation/ArturRios.Cerberus.WebApi/ArturRios.Cerberus.WebApi.csproj \
    --configuration Release --no-restore --output /publish

FROM mcr.microsoft.com/dotnet/aspnet:10.0@sha256:222759b391a1aaf241166672c8f99b2d4ada452e7b5319f3c6e8f265a37b5ad4 AS runtime
WORKDIR /app
COPY --from=build /publish ./
ENV ASPNETCORE_HTTP_PORTS=8080
EXPOSE 8080
USER app
ENTRYPOINT ["dotnet", "ArturRios.Cerberus.WebApi.dll"]
