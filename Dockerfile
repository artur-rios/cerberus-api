FROM mcr.microsoft.com/dotnet/sdk:10.0.402@sha256:747cbade5bfd918a175ac32f8f1d931edec2c7d0d5397a3de72eaef1ac1f66d2 AS build
WORKDIR /source
COPY global.json Directory.Build.props Directory.Packages.props ./
COPY src/ src/
RUN dotnet restore src/Presentation/ArturRios.Cerberus.WebApi/ArturRios.Cerberus.WebApi.csproj
RUN dotnet publish src/Presentation/ArturRios.Cerberus.WebApi/ArturRios.Cerberus.WebApi.csproj \
    --configuration Release --no-restore --output /publish

FROM mcr.microsoft.com/dotnet/aspnet:10.0.13@sha256:8af87eba5e7cdafb1514d7388be287365da97c132509e84dfe252ffd552398ca AS runtime
WORKDIR /app
COPY --from=build /publish ./
ENV ASPNETCORE_HTTP_PORTS=8080
EXPOSE 8080
USER app
ENTRYPOINT ["dotnet", "ArturRios.Cerberus.WebApi.dll"]
