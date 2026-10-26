FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

COPY Directory.Build.props Directory.Packages.props ./
COPY contracts/ ./contracts/
COPY tools/ ./tools/
COPY src/ ./src/

RUN dotnet publish src/Gateway.Api/Gateway.Api.csproj -c Release -o /app/publish

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS final
WORKDIR /app
COPY --from=build /app/publish .
EXPOSE 5150
ENTRYPOINT ["dotnet", "Gateway.Api.dll"]
