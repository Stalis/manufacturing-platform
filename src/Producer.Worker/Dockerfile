FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

COPY Directory.Build.props Directory.Packages.props ./
COPY contracts/ ./contracts/
COPY tools/ ./tools/
COPY src/ ./src/

RUN dotnet publish src/Producer.Worker/Producer.Worker.csproj -c Release -o /app/publish

FROM mcr.microsoft.com/dotnet/runtime:10.0 AS final
WORKDIR /app
COPY --from=build /app/publish .
ENTRYPOINT ["dotnet", "Producer.Worker.dll"]
